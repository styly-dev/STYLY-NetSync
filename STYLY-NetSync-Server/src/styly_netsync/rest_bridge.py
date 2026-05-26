from __future__ import annotations

import logging
import threading
import time
from typing import TYPE_CHECKING, Annotated, Protocol

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field, StringConstraints

from .client import net_sync_manager

if TYPE_CHECKING:
    import uvicorn

logger = logging.getLogger(__name__)

MAX_NAME = 64
MAX_VALUE = 1024
MAX_GLOBAL_VARS = 100

# Constrained string types for variable names and values
VarName = Annotated[str, StringConstraints(min_length=1, max_length=MAX_NAME)]
VarValue = Annotated[str, StringConstraints(max_length=MAX_VALUE)]


class UpsertBody(BaseModel):
    """Request body for client or global variable upsert."""

    variables: dict[VarName, VarValue] = Field(default_factory=dict)


class ClientVariableServer(Protocol):
    """Server-side operations used by REST client-variable endpoints."""

    def upsert_client_variables_for_device(
        self, room_id: str, device_id: str, variables: dict[str, str]
    ) -> tuple[int | None, dict[str, str]]:
        """Upsert client variables and return mapping plus per-key states."""
        ...

    def get_client_variables_for_device(
        self, room_id: str, device_id: str
    ) -> tuple[int | None, dict[str, str]]:
        """Return the mapped client number and current variables."""
        ...

    def delete_client_variables_for_device(
        self, room_id: str, device_id: str, name: str | None = None
    ) -> tuple[int | None, int]:
        """Delete client variables and return mapping plus deletion count."""
        ...


class GlobalVarStore:
    """In-memory storage for queued room-level global variables."""

    def __init__(self) -> None:
        self._data: dict[str, dict[str, str]] = {}
        self._lock = threading.RLock()

    def upsert(self, room_id: str, kvs: dict[str, str]) -> dict[str, str]:
        """Merge incoming key-values for a room."""
        with self._lock:
            current = self._data.get(room_id, {})
            new_keys = [name for name in kvs if name not in current]
            if len(current) + len(new_keys) > MAX_GLOBAL_VARS:
                raise ValueError(
                    f"Too many global variables (> {MAX_GLOBAL_VARS}) for room {room_id}"
                )
            current.update(kvs)
            self._data[room_id] = current
            return dict(current)

    def get(self, room_id: str) -> dict[str, str]:
        """Return a copy of stored global variables for a room."""
        with self._lock:
            return dict(self._data.get(room_id, {}))

    def pop(self, room_id: str) -> dict[str, str]:
        """Atomically retrieve and remove stored global variables for a room."""
        with self._lock:
            return self._data.pop(room_id, {})


global_store = GlobalVarStore()


class RoomBridge:
    """Internal client per room responsible for flushing queued variables."""

    def __init__(
        self, server_addr: str, dealer_port: int, sub_port: int, room_id: str
    ) -> None:
        self.room_id = room_id
        self._manager = net_sync_manager(
            server=server_addr, dealer_port=dealer_port, sub_port=sub_port, room=room_id
        )
        self._running = False
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._apply_lock = threading.RLock()

    @property
    def manager(self) -> net_sync_manager:
        """Return the underlying net_sync_manager instance."""
        return self._manager

    def start(self) -> None:
        """Start the internal client and background loop."""
        if self._running:
            return
        self._manager.start()
        self._running = True
        self._thread.start()

    def stop(self) -> None:
        """Stop the background loop and client."""
        self._running = False
        try:
            self._manager.stop()
        except Exception as exc:  # pragma: no cover - defensive
            logger.debug("Failed to stop room bridge manager cleanly: %s", exc)

    def _loop(self) -> None:
        """Drive stealth handshakes and flush queued variables."""
        next_handshake_at = 0.0
        while self._running:
            try:
                now = time.monotonic()
                if not self._manager.client_no or self._manager.client_no == 0:
                    if now >= next_handshake_at:
                        try:
                            self._manager.send_stealth_handshake()
                        except Exception as exc:
                            logger.debug("Stealth handshake failed: %s", exc)
                        next_handshake_at = now + 0.5
                else:
                    try:
                        self.flush_global_vars()
                    except Exception as exc:
                        logger.debug("Flush failed: %s", exc)
                time.sleep(0.1)
            except Exception as exc:  # pragma: no cover - defensive
                logger.debug("Bridge loop error: %s", exc)
                time.sleep(0.5)

    def apply_now_or_queue(self, device_id: str, kvs: dict[str, str]) -> dict[str, str]:
        """Attempt to apply variables immediately, otherwise mark them queued."""
        statuses: dict[str, str] = {}
        client_no = self.get_client_no(device_id)
        if client_no:
            applied = self._apply_to_client(client_no, kvs)
            for name in kvs:
                statuses[name] = "applied" if name in applied else "failed"
        else:
            for name in kvs:
                statuses[name] = "queued"
        return statuses

    def get_client_no(self, device_id: str) -> int | None:
        """Return client number for device if mapping is known."""
        try:
            return self._manager.get_client_no(device_id)
        except Exception as exc:  # pragma: no cover - defensive
            logger.debug("Failed to lookup client number for %s: %s", device_id, exc)
            return None

    def get_global_variables(self) -> dict[str, str]:
        """Return a snapshot of cached global variables for this room."""
        try:
            return self._manager.get_all_global_variables()
        except Exception as exc:  # pragma: no cover - defensive
            logger.debug("get_all_global_variables failed: %s", exc)
            return {}

    def get_client_variables(self, device_id: str) -> tuple[int | None, dict[str, str]]:
        """Return (client_no, snapshot) for device; client_no is None if unmapped."""
        client_no = self.get_client_no(device_id)
        if client_no is None:
            return None, {}
        try:
            return client_no, self._manager.get_all_client_variables(client_no)
        except Exception as exc:  # pragma: no cover - defensive
            logger.debug("get_all_client_variables failed: %s", exc)
            return client_no, {}

    def _apply_to_client(self, client_no: int, kvs: dict[str, str]) -> set[str]:
        """Apply stored variables to the target client via set_client_variable."""
        applied: set[str] = set()
        if not self._manager.client_no:
            return applied
        with self._apply_lock:
            for name, value in kvs.items():
                ok = False
                try:
                    ok = self._manager.set_client_variable(client_no, name, value)
                except Exception as exc:  # pragma: no cover - defensive
                    logger.debug(
                        "set_client_variable failed (room=%s, device=%s, key=%s): %s",
                        self.room_id,
                        client_no,
                        name,
                        exc,
                    )
                if ok:
                    applied.add(name)
        return applied

    def apply_global_now_or_queue(self, kvs: dict[str, str]) -> dict[str, str]:
        """Attempt to apply global variables immediately, otherwise mark them queued."""
        statuses: dict[str, str] = {}
        if self._manager.client_no:
            applied = self._apply_global(kvs)
            for name in kvs:
                statuses[name] = "applied" if name in applied else "failed"
        else:
            for name in kvs:
                statuses[name] = "queued"
        return statuses

    def _apply_global(self, kvs: dict[str, str]) -> set[str]:
        """Apply global variables via set_global_variable."""
        applied: set[str] = set()
        if not self._manager.client_no:
            return applied
        with self._apply_lock:
            for name, value in kvs.items():
                ok = False
                try:
                    ok = self._manager.set_global_variable(name, value)
                except Exception as exc:  # pragma: no cover - defensive
                    logger.debug(
                        "set_global_variable failed (room=%s, key=%s): %s",
                        self.room_id,
                        name,
                        exc,
                    )
                if ok:
                    applied.add(name)
        return applied

    def flush_global_vars(self) -> None:
        """Flush queued global variables for this room."""
        pending = global_store.pop(self.room_id)
        if not pending:
            return
        applied = self._apply_global(pending)
        # Re-queue only variables that failed to apply
        failed = {name: value for name, value in pending.items() if name not in applied}
        if failed:
            global_store.upsert(self.room_id, failed)


class BridgeManager:
    """Factory and cache for per-room bridges."""

    def __init__(self, server_addr: str, dealer_port: int, sub_port: int) -> None:
        self._server_addr = server_addr
        self._dealer_port = dealer_port
        self._sub_port = sub_port
        self._bridges: dict[str, RoomBridge] = {}
        self._lock = threading.RLock()

    def get(self, room_id: str) -> RoomBridge:
        """Return an active bridge for the requested room."""
        with self._lock:
            bridge = self._bridges.get(room_id)
            if bridge is None:
                bridge = RoomBridge(
                    self._server_addr, self._dealer_port, self._sub_port, room_id
                )
                bridge.start()
                self._bridges[room_id] = bridge
            return bridge


def create_app(
    server_addr: str,
    dealer_port: int,
    sub_port: int,
    server: ClientVariableServer | None = None,
) -> FastAPI:
    """Create the FastAPI application hosting the REST bridge."""
    app = FastAPI(title="NetSync REST Bridge", version="1.0.0")

    # Add CORS middleware
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=True,
        allow_methods=["GET", "POST", "PUT", "DELETE", "OPTIONS"],
        allow_headers=["Content-Type", "Authorization"],
        max_age=3600,  # Cache preflight requests for 1 hour
    )

    manager = BridgeManager(server_addr, dealer_port, sub_port)

    @app.get("/")
    def health_check() -> dict[str, str]:
        """Health check endpoint."""
        return {"status": "ok"}

    @app.post("/v1/rooms/{room_id}/devices/{device_id}/client-variables")
    def upsert(room_id: str, device_id: str, body: UpsertBody) -> dict[str, object]:
        if not body.variables:
            raise HTTPException(status_code=400, detail="variables must not be empty")
        if server is not None:
            try:
                client_no, statuses = server.upsert_client_variables_for_device(
                    room_id, device_id, body.variables
                )
            except ValueError as exc:
                raise HTTPException(status_code=409, detail=str(exc)) from exc
        else:
            bridge = manager.get(room_id)
            statuses = bridge.apply_now_or_queue(device_id, body.variables)
            client_no = bridge.get_client_no(device_id)

        return {
            "roomId": room_id,
            "deviceId": device_id,
            "mapping": {"clientNo": client_no},
            "result": {name: {"state": state} for name, state in statuses.items()},
        }

    @app.post("/v1/rooms/{room_id}/global-variables")
    def upsert_global(room_id: str, body: UpsertBody) -> dict[str, object]:
        if not body.variables:
            raise HTTPException(status_code=400, detail="variables must not be empty")

        bridge = manager.get(room_id)
        statuses = bridge.apply_global_now_or_queue(body.variables)

        # Only store variables that were queued (not applied immediately)
        queued = {
            name: body.variables[name]
            for name, state in statuses.items()
            if state != "applied"
        }
        if queued:
            try:
                global_store.upsert(room_id, queued)
            except ValueError as exc:
                raise HTTPException(status_code=409, detail=str(exc)) from exc

        return {
            "roomId": room_id,
            "result": {name: {"state": state} for name, state in statuses.items()},
        }

    @app.get("/v1/rooms/{room_id}/global-variables")
    def get_global_variables(room_id: str) -> dict[str, object]:
        bridge = manager.get(room_id)
        return {"variables": bridge.get_global_variables()}

    @app.get("/v1/rooms/{room_id}/global-variables/{name}")
    def get_global_variable(room_id: str, name: VarName) -> dict[str, object]:
        bridge = manager.get(room_id)
        variables = bridge.get_global_variables()
        if name not in variables:
            raise HTTPException(
                status_code=404, detail=f"Global variable '{name}' not found"
            )
        return {"value": variables[name]}

    @app.get("/v1/rooms/{room_id}/devices/{device_id}/client-variables")
    def get_client_variables(room_id: str, device_id: str) -> dict[str, object]:
        if server is not None:
            client_no, variables = server.get_client_variables_for_device(
                room_id, device_id
            )
        else:
            bridge = manager.get(room_id)
            client_no, variables = bridge.get_client_variables(device_id)
        return {"clientNo": client_no, "variables": variables}

    @app.get("/v1/rooms/{room_id}/devices/{device_id}/client-variables/{name}")
    def get_client_variable(
        room_id: str, device_id: str, name: VarName
    ) -> dict[str, object]:
        if server is not None:
            client_no, variables = server.get_client_variables_for_device(
                room_id, device_id
            )
        else:
            bridge = manager.get(room_id)
            client_no, variables = bridge.get_client_variables(device_id)
        if name not in variables:
            raise HTTPException(
                status_code=404,
                detail=f"Client variable '{name}' not found for device '{device_id}'",
            )
        return {"clientNo": client_no, "value": variables[name]}

    @app.delete("/v1/rooms/{room_id}/devices/{device_id}/client-variables")
    def delete_client_variables(room_id: str, device_id: str) -> dict[str, object]:
        if server is None:
            raise HTTPException(
                status_code=501,
                detail="Client variable deletion requires an injected server",
            )
        client_no, deleted_count = server.delete_client_variables_for_device(
            room_id, device_id
        )
        return {"clientNo": client_no, "deleted": deleted_count}

    @app.delete("/v1/rooms/{room_id}/devices/{device_id}/client-variables/{name}")
    def delete_client_variable(
        room_id: str, device_id: str, name: VarName
    ) -> dict[str, object]:
        if server is None:
            raise HTTPException(
                status_code=501,
                detail="Client variable deletion requires an injected server",
            )
        client_no, deleted_count = server.delete_client_variables_for_device(
            room_id, device_id, name
        )
        return {"clientNo": client_no, "deleted": deleted_count > 0}

    return app


def run_uvicorn_in_thread(
    app: FastAPI, host: str = "0.0.0.0", port: int = 8800
) -> tuple[threading.Thread, uvicorn.Server]:
    """Spawn a Uvicorn server for the given FastAPI app in a background thread."""
    import uvicorn

    config = uvicorn.Config(
        app=app, host=host, port=port, log_level="warning", lifespan="off"
    )
    server = uvicorn.Server(config=config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    return thread, server
