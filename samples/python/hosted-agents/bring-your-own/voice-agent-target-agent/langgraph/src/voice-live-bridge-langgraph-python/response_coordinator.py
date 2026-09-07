# Copyright (c) Microsoft. All rights reserved.

"""Response execution and terminal sequencing for Voice Live Bridge sessions."""

from __future__ import annotations

import asyncio
import contextvars
import logging
from collections.abc import Coroutine
from contextlib import nullcontext
from typing import Any

from azure.ai.agentserver.invocations.voice import (
    AgentError,
    EndCall,
    EndCallMode,
    ResponseCancel,
    ResponseCreated,
    ResponseDone,
    ResponseNone,
    ResponseOutputTextDelta,
    ResponseOutputTextDone,
    Session,
    new_item_id,
    new_response_id,
)

from model_contract import StreamingModelClient
from response_telemetry import ResponseTelemetry
from state import (
    MAX_PENDING_PROACTIVE,
    ApplicationTurn,
    InputOrigin,
    OperationOutcome,
    ProactiveRequest,
    ResponseOperation,
    SessionState,
)

logger = logging.getLogger("voice_live_bridge")

MAX_OUTPUT_CHUNKS = 4096
MAX_OUTPUT_UTF8_BYTES = 512 * 1024


class ResponseCoordinator:
    """Coordinate response tasks for explicit connection-scoped state."""

    def __init__(self, model_client: StreamingModelClient) -> None:
        self._model_client = model_client
        self._detached_tasks: set[asyncio.Task[None]] = set()

    def start_response(
        self,
        session: Session,
        state: SessionState,
        input_item_id: str | None,
        *,
        text: str,
        use_model: bool,
        streaming: bool,
        voice: dict[str, Any] | None,
        origin: InputOrigin,
        response_id: str | None = None,
        response_started: bool = False,
        user_message_received_at: float | None = None,
    ) -> bool:
        """Start one fixed or model-backed response task."""
        del origin
        if not state.can_start_response():
            return False
        response_id = response_id or new_response_id()
        input_ids = (input_item_id,) if input_item_id is not None else ()
        operation = ResponseOperation(
            session=session,
            state=state,
            response_id=response_id,
            input_ids=input_ids,
            turn=ApplicationTurn(),
            prompt=text if use_model else None,
            response_started=response_started,
            telemetry=(
                ResponseTelemetry(response_id, input_item_id, user_message_received_at)
                if input_item_id is not None and user_message_received_at is not None
                else None
            ),
        )
        state.responses[response_id] = operation
        if input_item_id is not None:
            state.input_responses[input_item_id] = response_id
        self._start_task(
            state,
            operation,
            self._run_response(operation, text, use_model, streaming, voice),
        )
        return True

    def start_self_cancel(
        self,
        session: Session,
        state: SessionState,
        input_item_id: str,
        text: str,
        user_message_received_at: float,
    ) -> bool:
        """Start a response that requests its own cancellation."""
        if not state.can_start_response():
            return False
        response_id = new_response_id()
        operation = ResponseOperation(
            session=session,
            state=state,
            response_id=response_id,
            input_ids=(input_item_id,),
            turn=ApplicationTurn(),
            prompt=None,
            response_started=False,
            terminal_event=asyncio.Event(),
            telemetry=ResponseTelemetry(
                response_id,
                input_item_id,
                user_message_received_at,
            ),
        )
        state.responses[operation.response_id] = operation
        state.input_responses[input_item_id] = operation.response_id
        self._start_task(state, operation, self._run_self_cancel(operation, text))
        return True

    def reserve_proactive(self, state: SessionState, text: str) -> str | None:
        """Reserve bounded proactive admission state."""
        if not state.active or len(state.proactive_requests) >= MAX_PENDING_PROACTIVE:
            return None
        response_id = new_response_id()
        state.proactive_requests[response_id] = ProactiveRequest(text, True)
        return response_id

    async def send_proactive_request(
        self, session: Session, state: SessionState, response_id: str
    ) -> bool:
        """Request proactive admission and roll back the reservation on failure."""
        if not state.active or response_id not in state.proactive_requests:
            state.proactive_requests.pop(response_id, None)
            return False
        try:
            await session.send(
                ResponseCreated(
                    response_id=response_id,
                    admission_timeout_ms=5000,
                    supersede_key="voice-live-bridge-langgraph-python",
                )
            )
        except BaseException:
            state.proactive_requests.pop(response_id, None)
            raise
        return True

    async def accept_proactive(
        self, session: Session, state: SessionState, response_id: str
    ) -> None:
        """Start reserved proactive work after Voice Live accepts it."""
        request = state.proactive_requests.pop(response_id, None)
        if request is None or not state.active:
            return
        if not state.can_start_response():
            await session.send(ResponseCancel(response_id=response_id, reason="capacity_exceeded"))
            return
        started = self.start_response(
            session,
            state,
            input_item_id=None,
            text=request.text,
            use_model=request.use_model,
            streaming=request.use_model,
            voice=None,
            origin=InputOrigin.PROACTIVE,
            response_id=response_id,
            response_started=True,
        )
        if not started:
            await session.send(ResponseCancel(response_id=response_id, reason="capacity_exceeded"))

    def barge_in(self, state: SessionState, response_id: str, heard_text: str) -> None:
        """Reconcile heard output and cancel active generation."""
        operation = state.responses.get(response_id)
        if operation is not None:
            if operation.prompt is not None:
                state.history.reconcile(response_id, operation.prompt, heard_text)
                operation.history_committed = True
            operation.select_outcome(OperationOutcome.CANCELLED)
            if operation.task is not None:
                operation.task.cancel()
        else:
            state.history.reconcile(response_id, None, heard_text)

    def response_cancelled(self, state: SessionState, response_id: str, heard_text: str) -> None:
        """Correlate a cancellation terminal event with its operation."""
        operation = state.responses.get(response_id)
        if operation is None:
            state.history.reconcile(response_id, None, heard_text)
            return
        operation.select_outcome(OperationOutcome.CANCELLED)
        if operation.prompt is not None:
            state.history.reconcile(response_id, operation.prompt, heard_text)
            operation.history_committed = True
        if operation.terminal_event is not None:
            operation.terminal_event.set()
        elif operation.task is not None:
            operation.task.cancel()

    def response_timeout(
        self,
        state: SessionState,
        response_id: str | None,
        item_ids: tuple[str, ...] | list[str] | None,
    ) -> None:
        """Cancel proactive or active work selected by a timeout event."""
        if response_id is not None:
            state.proactive_requests.pop(response_id, None)
            self.cancel_response(state, response_id, OperationOutcome.TIMEOUT)
            return
        for input_id in item_ids or ():
            if mapped_response_id := state.input_responses.get(input_id):
                self.cancel_response(state, mapped_response_id, OperationOutcome.TIMEOUT)

    @staticmethod
    def cancel_response(state: SessionState, response_id: str, outcome: OperationOutcome) -> None:
        """Select an outcome and cancel one active response."""
        operation = state.responses.get(response_id)
        if operation is None:
            return
        operation.select_outcome(outcome)
        if operation.task is not None:
            operation.task.cancel()

    async def send_none(
        self,
        session: Session,
        input_item_id: str,
        origin: InputOrigin,
        *,
        reason: str = "no_reply_needed",
    ) -> None:
        """Send a terminal response.none decision."""
        del origin
        turn = ApplicationTurn()
        try:
            with turn.activate():
                await session.send(ResponseNone(in_reply_to=(input_item_id,), reason=reason))
            turn.complete(outcome=OperationOutcome.NONE, output_item_count=0)
        except BaseException:
            if not turn.is_completed:
                turn.complete(outcome=OperationOutcome.ERROR, output_item_count=0)
            raise

    async def send_error(self, session: Session, input_item_id: str, origin: InputOrigin) -> None:
        """Send the sample response-scoped error sequence."""
        del origin
        response_id = new_response_id()
        response_started = False
        turn = ApplicationTurn()
        try:
            with turn.activate():
                await session.send(
                    ResponseCreated(response_id=response_id, in_reply_to=(input_item_id,))
                )
                response_started = True
                await session.send(
                    AgentError(
                        code="sample_error",
                        message="The sample emitted the requested error.",
                        response_id=response_id,
                    )
                )
            turn.complete(
                outcome=OperationOutcome.ERROR,
                response_id=response_id,
                output_item_count=0,
            )
        except BaseException:
            if not turn.is_completed:
                turn.complete(
                    outcome=OperationOutcome.ERROR,
                    response_id=response_id if response_started else None,
                    output_item_count=0,
                )
            raise

    async def send_session_error(self, session: Session, origin: InputOrigin) -> None:
        """Send the sample session-scoped error."""
        del origin
        turn = ApplicationTurn()
        try:
            with turn.activate():
                await session.send(
                    AgentError(
                        code="sample_session_error",
                        message="The sample emitted the requested session error.",
                    )
                )
            turn.complete(outcome=OperationOutcome.ERROR, output_item_count=0)
        except BaseException:
            if not turn.is_completed:
                turn.complete(outcome=OperationOutcome.ERROR, output_item_count=0)
            raise

    async def send_end_call(
        self,
        session: Session,
        mode: EndCallMode,
        origin: InputOrigin,
        *,
        reason: str | None = None,
    ) -> None:
        """Send a terminal call instruction."""
        turn = ApplicationTurn()
        try:
            with turn.activate():
                await session.send(
                    EndCall(
                        reason=reason
                        or (
                            "repeated_no_input"
                            if origin is InputOrigin.NO_INPUT
                            else "sample_completed"
                        ),
                        mode=mode,
                    )
                )
            turn.complete(outcome=OperationOutcome.END_CALL, output_item_count=0)
        except BaseException:
            if not turn.is_completed:
                turn.complete(outcome=OperationOutcome.ERROR, output_item_count=0)
            raise

    async def terminate_state(self, state: SessionState, outcome: OperationOutcome) -> None:
        """Cancel and await all operations belonging to one state."""
        tasks = self._mark_terminating(state, outcome)
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        for operation in tuple(state.responses.values()):
            self._finalize(state, operation, outcome)
        self._clear_state(state)

    def detach_terminating_state(self, state: SessionState, outcome: OperationOutcome) -> None:
        """Synchronously terminate state and track tasks that must finish later."""
        tasks = self._mark_terminating(state, outcome)
        self._clear_state(state)
        for task in tasks:
            self._detached_tasks.add(task)
            task.add_done_callback(self._detached_tasks.discard)

    async def wait_for_detached_tasks(self) -> None:
        """Wait for all transport-detached response tasks."""
        while self._detached_tasks:
            await asyncio.gather(*tuple(self._detached_tasks), return_exceptions=True)

    def _start_task(
        self,
        state: SessionState,
        operation: ResponseOperation,
        coroutine: Coroutine[Any, Any, None],
    ) -> None:
        try:
            activation = operation.telemetry.activate() if operation.telemetry else nullcontext()
            with activation:
                task = asyncio.create_task(
                    coroutine,
                    name=f"voice-response-{operation.response_id}",
                )
        except BaseException:
            coroutine.close()
            self._finalize(state, operation, OperationOutcome.ABANDONED)
            raise
        operation.task = task
        task.add_done_callback(
            lambda done: self._task_done(state, operation, done),
            context=contextvars.Context(),
        )

    async def _run_response(
        self,
        operation: ResponseOperation,
        text: str,
        use_model: bool,
        streaming: bool,
        voice: dict[str, Any] | None,
    ) -> None:
        try:
            if not operation.response_started:
                await operation.session.send(
                    ResponseCreated(
                        response_id=operation.response_id,
                        in_reply_to=operation.input_ids,
                    )
                )
                operation.response_started = True
            with operation.turn.activate():
                if use_model:
                    async with operation.state.history.generation_gate:
                        answer = await self._send_model_output(operation, text, streaming, voice)
                        operation.commit_history(answer)
                else:
                    await self._send_fixed_output(operation, text, streaming, voice)
            operation.select_outcome(OperationOutcome.RESPONSE)
        except asyncio.CancelledError:
            operation.select_outcome(OperationOutcome.CANCELLED)
            raise
        except Exception as exception:
            operation.select_outcome(OperationOutcome.ERROR)
            if operation.telemetry is not None:
                operation.telemetry.record_failure(exception)
            logger.exception("Voice response failed")
            await self._report_failure(operation)

    async def _send_model_output(
        self,
        operation: ResponseOperation,
        prompt: str,
        streaming: bool,
        voice: dict[str, Any] | None,
    ) -> str:
        chunks: list[str] = []
        utf8_bytes = 0
        item_id = new_item_id()
        first_chunk = True
        request = operation.state.history.create_request(prompt)
        async for chunk in self._model_client.complete(request):
            encoded = len(chunk.encode("utf-8"))
            if len(chunks) >= MAX_OUTPUT_CHUNKS or utf8_bytes + encoded > MAX_OUTPUT_UTF8_BYTES:
                raise RuntimeError("Model output exceeded sample limits")
            chunks.append(chunk)
            utf8_bytes += encoded
            if streaming:
                await operation.session.send(
                    ResponseOutputTextDelta(
                        response_id=operation.response_id,
                        item_id=item_id,
                        delta=chunk,
                        voice=voice if first_chunk else None,
                    )
                )
                first_chunk = False
        answer = "".join(chunks)
        if not answer:
            raise RuntimeError("The model returned no text")
        await operation.session.send(
            ResponseOutputTextDone(
                response_id=operation.response_id,
                item_id=item_id,
                text=answer,
                voice=voice,
            )
        )
        operation.output_item_count = 1
        operation.release_capacity()
        await operation.session.send(ResponseDone(response_id=operation.response_id))
        if operation.telemetry is not None:
            operation.telemetry.record_response_done_sent()
        return answer

    @staticmethod
    async def _send_fixed_output(
        operation: ResponseOperation,
        text: str,
        streaming: bool,
        voice: dict[str, Any] | None,
    ) -> None:
        item_id = new_item_id()
        if streaming:
            await operation.session.send(
                ResponseOutputTextDelta(
                    response_id=operation.response_id,
                    item_id=item_id,
                    delta=text,
                    voice=voice,
                )
            )
        await operation.session.send(
            ResponseOutputTextDone(
                response_id=operation.response_id,
                item_id=item_id,
                text=text,
                voice=voice,
            )
        )
        operation.output_item_count = 1
        operation.release_capacity()
        await operation.session.send(ResponseDone(response_id=operation.response_id))
        if operation.telemetry is not None:
            operation.telemetry.record_response_done_sent()

    async def _run_self_cancel(self, operation: ResponseOperation, text: str) -> None:
        try:
            item_id = new_item_id()
            with operation.turn.activate():
                await operation.session.send(
                    ResponseCreated(
                        response_id=operation.response_id,
                        in_reply_to=operation.input_ids,
                    )
                )
                operation.response_started = True
                await operation.session.send(
                    ResponseOutputTextDelta(
                        response_id=operation.response_id,
                        item_id=item_id,
                        delta=text,
                    )
                )
                await operation.session.send(
                    ResponseCancel(
                        response_id=operation.response_id,
                        reason="sample_self_correction",
                    )
                )
            assert operation.terminal_event is not None
            await operation.terminal_event.wait()
        except asyncio.CancelledError:
            operation.select_outcome(OperationOutcome.CANCELLED)
            raise
        except Exception:
            operation.select_outcome(OperationOutcome.ERROR)
            logger.exception("Self-cancelling response failed")
            await self._report_failure(operation)

    def _task_done(
        self,
        state: SessionState,
        operation: ResponseOperation,
        task: asyncio.Task[None],
    ) -> None:
        if not task.cancelled():
            task.exception()
        self._finalize(state, operation, OperationOutcome.ABANDONED)

    @staticmethod
    def _finalize(
        state: SessionState,
        operation: ResponseOperation,
        fallback: OperationOutcome,
    ) -> None:
        operation.complete(fallback)
        state.responses.pop(operation.response_id, None)
        for input_id in operation.input_ids:
            if state.input_responses.get(input_id) == operation.response_id:
                state.input_responses.pop(input_id, None)

    @staticmethod
    def _mark_terminating(
        state: SessionState, outcome: OperationOutcome
    ) -> list[asyncio.Task[None]]:
        state.terminating = True
        state.proactive_requests.clear()
        tasks: list[asyncio.Task[None]] = []
        for operation in tuple(state.responses.values()):
            operation.select_outcome(outcome)
            if operation.task is not None and not operation.task.done():
                operation.task.cancel()
                tasks.append(operation.task)
        return tasks

    @staticmethod
    def _clear_state(state: SessionState) -> None:
        state.history.clear()
        state.seen_inputs.clear()

    @staticmethod
    async def _report_failure(operation: ResponseOperation) -> None:
        try:
            await operation.session.send(
                AgentError(
                    code="generation_failed",
                    message="The response could not be generated.",
                    response_id=(operation.response_id if operation.response_started else None),
                )
            )
        except BaseException:
            logger.debug("Could not report response failure")
