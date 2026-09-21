# Copyright (c) Microsoft. All rights reserved.

"""Model backend contract for the Voice Live Bridge sample."""

from __future__ import annotations

from collections.abc import AsyncIterator, Sequence
from typing import Protocol

from state import ModelMessage


class StreamingModelClient(Protocol):
    """Response backend consumed by the Voice Live Bridge runtime."""

    model_name: str
    server_address: str

    async def complete(self, messages: Sequence[ModelMessage]) -> AsyncIterator[str]:
        """Yield ordered assistant text chunks."""
        if False:
            yield ""

    async def close(self) -> None:
        """Release backend resources."""
