from __future__ import annotations

import logging
import threading
import time
from typing import Dict

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field, constr

from .client import net_sync_manager

logger = logging.getLogger(__name__)

MAX_NAME = 64
MAX_VALUE = 1024
MAX_CLIENT_VARS = 20


class UpsertBody(BaseModel):
    """Request body for client variable upsert."""

    vars: Dict[constr(min_length=1, max_length=MAX_NAME), constr(max_length=MAX_VALUE)] = Field(  # type: ignore[type-arg]
        default_factory=dict
    )


class PreseedStore:
    """In-memory storage for queued device variables."""

    def __init__(self) -> None:
        self._data: dict[tuple[str, str], dict[str, str]] = {}
        self._lock = threading.RLock()

    def upsert(
        self, room_id: str, device_id: str, kvs: dict[str, str]
    ) -> dict[str, str]:
        """Merge incoming key-values for a device within a room."""
        with self._lock:
            key = (room_id, device_id)
            current = self._data.get(key, {})
            new_keys = [name for name in kvs if name not in current]
            if len(current) + len(new_keys) > MAX_CLIENT_VARS:
                raise ValueError(
                    f"Too many client variables (> {MAX_CLIENT_VARS}) for device {device_id}"
                )
            current.update(kvs)
            self._data[key] = current
            return dict(current)

    def get(self, room_id: str, device_id: str) -> dict[str, str]:
        """Return a copy of stored variables for a device."""
        with self._lock:
            return dict(self._data.get((room_id, device_id), {}))

    def all_for_room(self, room_id: str) -> dict[str, dict[str, str]]:
        """Return all stored variables for the given room."""
        with self._lock:
            result: dict[str, dict[str, str]] = {}
            for (stored_room, device_id), variables in self._data.items():
                if stored_room == room_id:
                    result[device_id] = dict(variables)
            return result


store = PreseedStore()


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
                        self.flush_all_known_mappings()
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

    def flush_all_known_mappings(self) -> None:
        """Flush queued variables for devices whose client numbers are known."""
        pending = store.all_for_room(self.room_id)
        for device_id, kvs in pending.items():
            client_no = self.get_client_no(device_id)
            if client_no:
                self._apply_to_client(client_no, kvs)


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


def create_app(server_addr: str, dealer_port: int, sub_port: int) -> FastAPI:
    """Create the FastAPI application hosting the REST bridge."""
    app = FastAPI(title="NetSync REST Bridge", version="1.0.0")

    # Add CORS middleware
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=True,
        allow_methods=["GET", "POST", "PUT", "DELETE", "OPTIONS"],
        allow_headers=["Content-Type", "Authorization"],
    )

    manager = BridgeManager(server_addr, dealer_port, sub_port)

    @app.get("/")
    def health_check() -> dict[str, str]:
        """Health check endpoint."""
        return {"status": "ok"}

    @app.post("/v1/rooms/{room_id}/devices/{device_id}/client-variables")
    def upsert(room_id: str, device_id: str, body: UpsertBody) -> dict[str, object]:
        if not body.vars:
            raise HTTPException(status_code=400, detail="vars must not be empty")
        try:
            store.upsert(room_id, device_id, body.vars)
        except ValueError as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc

        bridge = manager.get(room_id)
        statuses = bridge.apply_now_or_queue(device_id, body.vars)
        client_no = bridge.get_client_no(device_id)

        return {
            "roomId": room_id,
            "deviceId": device_id,
            "mapping": {"clientNo": client_no},
            "result": {name: {"state": state} for name, state in statuses.items()},
        }

    return app


def run_uvicorn_in_thread(
    app: FastAPI, host: str = "0.0.0.0", port: int = 8800
) -> tuple[threading.Thread, "uvicorn.Server"]:
    """Spawn a Uvicorn server for the given FastAPI app in a background thread."""
    import uvicorn

    config = uvicorn.Config(
        app=app, host=host, port=port, log_level="warning", lifespan="off"
    )
    server = uvicorn.Server(config=config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    return thread, server
