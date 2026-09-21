"""Small SQLite response provider for the single-process hybrid sample."""

from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator, Iterable
from contextlib import asynccontextmanager
from pathlib import Path
from types import TracebackType
from typing import Any, Self, cast

import aiosqlite
from azure.ai.agentserver.responses import (
    PlatformContext,
    ResponseObject,
    ResponseProviderProtocol,
)
from azure.ai.agentserver.responses.models import OutputItem
from azure.ai.agentserver.responses.store import ResponseAlreadyExistsError


def _encode(value: Any) -> str:
    return json.dumps(value, default=lambda model: model.as_dict())


class SqliteResponseStore(ResponseProviderProtocol):
    """Persist response envelopes, items, and history references in SQLite."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)
        self._connection: aiosqlite.Connection | None = None
        self._lock = asyncio.Lock()

    async def __aenter__(self) -> Self:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._connection = await aiosqlite.connect(self._path)
        try:
            await self._db.executescript(
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS responses (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    partition TEXT NOT NULL,
                    response_id TEXT NOT NULL,
                    conversation_id TEXT,
                    record TEXT NOT NULL,
                    UNIQUE (partition, response_id)
                );
                CREATE TABLE IF NOT EXISTS response_items (
                    partition TEXT NOT NULL,
                    item_id TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY (partition, item_id)
                );
                """
            )
        except BaseException:
            await self._db.close()
            self._connection = None
            raise
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        await self._db.close()
        self._connection = None

    @property
    def _db(self) -> aiosqlite.Connection:
        if self._connection is None:
            raise RuntimeError("SqliteResponseStore is not open")
        return self._connection

    @staticmethod
    def _partition(context: PlatformContext | None) -> str:
        return json.dumps(context.user_id_key if context is not None else None)

    @asynccontextmanager
    async def _transaction(self) -> AsyncIterator[None]:
        async with self._lock:
            await self._db.execute("BEGIN")
            try:
                yield
                await self._db.commit()
            except BaseException:
                await self._db.rollback()
                raise

    async def _record(self, partition: str, response_id: str) -> dict[str, Any]:
        async with self._db.execute(
            "SELECT record FROM responses WHERE partition = ? AND response_id = ?",
            (partition, response_id),
        ) as cursor:
            row = await cursor.fetchone()
        if row is None:
            raise KeyError(response_id)
        return json.loads(row[0])

    async def _save_items(
        self, partition: str, items: Iterable[OutputItem]
    ) -> list[str]:
        item_ids = []
        for item in items:
            item_id = item.get("id")
            if item_id is not None:
                await self._db.execute(
                    "INSERT OR REPLACE INTO response_items VALUES (?, ?, ?)",
                    (partition, item_id, _encode(item)),
                )
                item_ids.append(item_id)
        return item_ids

    async def _items(
        self, partition: str, item_ids: Iterable[str]
    ) -> list[OutputItem | None]:
        items = []
        for item_id in item_ids:
            async with self._db.execute(
                "SELECT payload FROM response_items WHERE partition = ? AND item_id = ?",
                (partition, item_id),
            ) as cursor:
                row = await cursor.fetchone()
            items.append(cast(OutputItem, json.loads(row[0])) if row else None)
        return items

    async def create_response(
        self,
        response: ResponseObject,
        input_items: Iterable[OutputItem] | None,
        history_item_ids: Iterable[str] | None,
        *,
        context: PlatformContext | None = None,
    ) -> None:
        partition = self._partition(context)
        response_id = response["id"]
        async with self._transaction():
            try:
                existing = await self._record(partition, response_id)
            except KeyError:
                existing = None
            if existing is not None and not existing["deleted"]:
                raise ResponseAlreadyExistsError(response_id)
            record = {
                "response": response,
                "input_ids": await self._save_items(partition, input_items or []),
                "output_ids": await self._save_items(
                    partition, response.get("output") or []
                ),
                "history_ids": list(history_item_ids or []),
                "deleted": False,
            }
            conversation = response.get("conversation")
            conversation_id = (
                conversation
                if isinstance(conversation, str)
                else conversation.get("id")
                if conversation
                else None
            )
            await self._db.execute(
                """INSERT OR REPLACE INTO responses
                   (partition, response_id, conversation_id, record) VALUES (?, ?, ?, ?)""",
                (partition, response_id, conversation_id, _encode(record)),
            )

    async def get_response(
        self, response_id: str, *, context: PlatformContext | None = None
    ) -> ResponseObject:
        async with self._lock:
            record = await self._record(self._partition(context), response_id)
            if record["deleted"]:
                raise KeyError(response_id)
            return cast(ResponseObject, record["response"])

    async def update_response(
        self, response: ResponseObject, *, context: PlatformContext | None = None
    ) -> None:
        partition = self._partition(context)
        response_id = response["id"]
        async with self._transaction():
            record = await self._record(partition, response_id)
            if record["deleted"]:
                raise KeyError(response_id)
            record["response"] = response
            record["output_ids"] = await self._save_items(
                partition, response.get("output") or []
            )
            await self._db.execute(
                "UPDATE responses SET record = ? WHERE partition = ? AND response_id = ?",
                (_encode(record), partition, response_id),
            )

    async def delete_response(
        self, response_id: str, *, context: PlatformContext | None = None
    ) -> None:
        partition = self._partition(context)
        async with self._transaction():
            record = await self._record(partition, response_id)
            if record["deleted"]:
                raise KeyError(response_id)
            record.update(deleted=True, response=None)
            await self._db.execute(
                "UPDATE responses SET record = ? WHERE partition = ? AND response_id = ?",
                (_encode(record), partition, response_id),
            )

    async def get_input_items(
        self,
        response_id: str,
        limit: int = 20,
        ascending: bool = False,
        after: str | None = None,
        before: str | None = None,
        *,
        context: PlatformContext | None = None,
    ) -> list[OutputItem]:
        partition = self._partition(context)
        async with self._lock:
            record = await self._record(partition, response_id)
            if record["deleted"]:
                raise ValueError(f"response {response_id!r} has been deleted")
            item_ids = record["history_ids"] + record["input_ids"]
            if not ascending:
                item_ids.reverse()
            if after in item_ids:
                item_ids = item_ids[item_ids.index(after) + 1 :]
            if before in item_ids:
                item_ids = item_ids[: item_ids.index(before)]
            items = await self._items(partition, item_ids[: max(1, min(100, limit))])
            return [item for item in items if item is not None]

    async def get_items(
        self, item_ids: Iterable[str], *, context: PlatformContext | None = None
    ) -> list[OutputItem | None]:
        async with self._lock:
            return await self._items(self._partition(context), item_ids)

    async def get_history_item_ids(
        self,
        previous_response_id: str | None,
        conversation_id: str | None,
        limit: int,
        *,
        context: PlatformContext | None = None,
    ) -> list[str]:
        if limit <= 0:
            return []
        partition = self._partition(context)
        async with self._lock:
            records = []
            if previous_response_id is not None:
                try:
                    records.append(await self._record(partition, previous_response_id))
                except KeyError:
                    pass
            if conversation_id is not None:
                async with self._db.execute(
                    """SELECT record FROM responses
                       WHERE partition = ? AND conversation_id = ? ORDER BY sequence""",
                    (partition, conversation_id),
                ) as cursor:
                    records.extend(
                        json.loads(row[0]) for row in await cursor.fetchall()
                    )
            item_ids = [
                item_id
                for record in records
                if not record["deleted"]
                for item_id in record["history_ids"]
                + record["input_ids"]
                + record["output_ids"]
            ]
            return item_ids[-limit:]
