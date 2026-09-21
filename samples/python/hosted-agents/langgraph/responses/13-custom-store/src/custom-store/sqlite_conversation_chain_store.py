# Copyright (c) Microsoft. All rights reserved.

"""SQLite conversation-chain storage for the hybrid deployment sample."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from types import TracebackType
from typing import Self

import aiosqlite
from langchain_azure_ai.agents.hosting import ConversationChainStoreProtocol

logger = logging.getLogger(__name__)


class SqliteConversationChainStore(ConversationChainStoreProtocol):
    """Store conversation-chain records in a SQLite file."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)
        self._connection: aiosqlite.Connection | None = None

    async def __aenter__(self) -> Self:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._connection = await aiosqlite.connect(self._path)
        await self._connection.execute("PRAGMA journal_mode=WAL")
        await self._connection.execute(
            """
            CREATE TABLE IF NOT EXISTS conversation_chain_records (
                conversation_chain_id TEXT NOT NULL,
                key TEXT NOT NULL,
                data TEXT NOT NULL,
                PRIMARY KEY (conversation_chain_id, key)
            )
            """
        )
        await self._connection.commit()
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        if self._connection is not None:
            await self._connection.close()
            self._connection = None

    @property
    def _db(self) -> aiosqlite.Connection:
        if self._connection is None:
            raise RuntimeError("SqliteConversationChainStore is not open")
        return self._connection

    async def get(
        self,
        conversation_chain_id: str,
        key: str,
    ) -> dict[str, str] | None:
        async with self._db.execute(
            """
            SELECT data
            FROM conversation_chain_records
            WHERE conversation_chain_id = ? AND key = ?
            """,
            (conversation_chain_id, key),
        ) as cursor:
            row = await cursor.fetchone()
        if row is None:
            logger.info(
                "Conversation chain load: chain_id=%r key=%r found=False",
                conversation_chain_id,
                key,
            )
            return None

        data = json.loads(row[0])
        if not isinstance(data, dict) or not all(
            isinstance(item_key, str) and isinstance(value, str)
            for item_key, value in data.items()
        ):
            raise TypeError(f"Stored record {key!r} is not a string dictionary")
        logger.info(
            "Conversation chain load: chain_id=%r key=%r found=True",
            conversation_chain_id,
            key,
        )
        return data

    async def set(
        self,
        conversation_chain_id: str,
        key: str,
        data: dict[str, str],
    ) -> None:
        payload = json.dumps(
            data,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        await self._db.execute(
            """
            INSERT INTO conversation_chain_records (
                conversation_chain_id,
                key,
                data
            ) VALUES (?, ?, ?)
            ON CONFLICT(conversation_chain_id, key)
            DO UPDATE SET data = excluded.data
            """,
            (conversation_chain_id, key, payload),
        )
        await self._db.commit()
        logger.info(
            "Conversation chain store: chain_id=%r key=%r committed=True",
            conversation_chain_id,
            key,
        )