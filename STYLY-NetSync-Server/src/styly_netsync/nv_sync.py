"""Delta-based Network Variable synchronisation primitives.

This module implements the server-side data structures required for the
MessagePack based protocol described in the "STYLY NetSync — Delta-based
Network Variables (NV) Sync" specification.  The goal of this module is to
provide an isolated, easily testable implementation that can be integrated by
both the ZeroMQ server and alternative transports.

The design focuses on three responsibilities:

* :class:`NameTable` – simple name interning with digest support.  It assigns
  monotonically increasing ``nameId`` values and emits delta/full/digest
  payloads.
* :class:`RoomState` – mutable state for a single room.  It manages the global
  and per-client NV scope, keeps a delta log ring buffer and builds outgoing
  protocol payloads.
* :class:`DeltaRecord` – typed representation of a single NV mutation.

The classes intentionally avoid any direct networking code so that unit tests
can exercise the behaviour without sockets.  ``msgpack`` is only used when
encoding payload bytes, keeping the bulk of the logic in regular Python data
structures.
"""

from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import struct
import time
from typing import Any, Iterable, Literal
import zlib

import msgpack

__all__ = [
    "ACK_MESSAGE_TYPE",
    "DELTA_MESSAGE_TYPE",
    "NAME_TABLE_DELTA_MESSAGE_TYPE",
    "NAME_TABLE_DIGEST_MESSAGE_TYPE",
    "NAME_TABLE_FULL_MESSAGE_TYPE",
    "SNAPSHOT_MESSAGE_TYPE",
    "DeltaRecord",
    "NameTable",
    "RoomState",
]


# Message type identifiers (see specification §3.1)
SNAPSHOT_MESSAGE_TYPE = 0x20
DELTA_MESSAGE_TYPE = 0x21
ACK_MESSAGE_TYPE = 0x22
NAME_TABLE_FULL_MESSAGE_TYPE = 0x30
NAME_TABLE_DELTA_MESSAGE_TYPE = 0x31
NAME_TABLE_DIGEST_MESSAGE_TYPE = 0x32


ScopeLiteral = Literal["g", "c"]
OperationLiteral = Literal["set", "del"]


@dataclass(slots=True)
class DeltaRecord:
    """Representation of a single Network Variable mutation."""

    seq: int
    scope: ScopeLiteral
    op: OperationLiteral
    name_id: int
    value: Any | None
    client_no: int | None = None

    def to_payload(self) -> dict[str, Any]:
        """Convert the record into the serialisable dictionary form."""

        payload: dict[str, Any] = {
            "seq": self.seq,
            "scope": self.scope,
            "op": self.op,
            "nameId": self.name_id,
        }
        if self.scope == "c":
            payload["clientNo"] = self.client_no or 0
        if self.op == "set":
            payload["value"] = self.value
        return payload


class NameTable:
    """Simple name interning table with digest helpers."""

    __slots__ = (
        "_name_to_id",
        "_id_to_name",
        "_next_name_id",
        "_pending_added",
        "_delta_base_version",
        "version",
        "count",
        "crc32",
        "_last_used",
    )

    def __init__(self, *, start_name_id: int = 1) -> None:
        self._name_to_id: dict[str, int] = {}
        self._id_to_name: dict[int, str] = {}
        self._next_name_id = start_name_id
        self._pending_added: list[tuple[int, str]] = []
        self._delta_base_version: int | None = None
        self.version: int = 0
        self.count: int = 0
        self.crc32: int = 0
        self._last_used: dict[int, float] = {}

    # ------------------------------------------------------------------
    # Lookup helpers
    # ------------------------------------------------------------------
    def lookup(self, name: str) -> int | None:
        """Return the interned ``nameId`` for *name* or ``None``."""

        return self._name_to_id.get(name)

    def resolve(self, name: str) -> tuple[int, bool]:
        """Return ``(name_id, is_new)`` for *name*, creating an entry if needed."""

        existing = self.lookup(name)
        if existing is not None:
            self.touch(existing)
            return existing, False

        name_id = self._next_name_id
        self._next_name_id += 1

        self._name_to_id[name] = name_id
        self._id_to_name[name_id] = name
        self.count = len(self._id_to_name)

        if self._delta_base_version is None:
            self._delta_base_version = self.version
        self.version += 1

        self._pending_added.append((name_id, name))
        self._recompute_crc32()
        self.touch(name_id)
        return name_id, True

    def touch(self, name_id: int) -> None:
        """Mark ``name_id`` as recently used (for GC heuristics)."""

        self._last_used[name_id] = time.monotonic()

    # ------------------------------------------------------------------
    # Payload generation helpers
    # ------------------------------------------------------------------
    def entries(self) -> list[tuple[int, str]]:
        """Return all table entries sorted by ``nameId``."""

        return sorted(self._id_to_name.items())

    def build_full_payload(self, room_id: str) -> dict[str, Any]:
        """Build a ``NAME_TABLE_FULL`` payload."""

        return {
            "type": NAME_TABLE_FULL_MESSAGE_TYPE,
            "roomId": room_id,
            "version": self.version,
            "entries": [[name_id, name] for name_id, name in self.entries()],
        }

    def build_digest_payload(self, room_id: str) -> dict[str, Any]:
        """Build a ``NAME_TABLE_DIGEST`` payload."""

        return {
            "type": NAME_TABLE_DIGEST_MESSAGE_TYPE,
            "roomId": room_id,
            "version": self.version,
            "count": self.count,
            "crc32": self.crc32,
        }

    def collect_delta_payload(self, room_id: str) -> dict[str, Any] | None:
        """Return a ``NAME_TABLE_DELTA`` payload if new names were added."""

        if not self._pending_added:
            return None

        base_version = self._delta_base_version if self._delta_base_version is not None else self.version
        payload = {
            "type": NAME_TABLE_DELTA_MESSAGE_TYPE,
            "roomId": room_id,
            "baseVersion": base_version,
            "added": [[name_id, name] for name_id, name in self._pending_added],
            "newVersion": self.version,
        }

        self._pending_added.clear()
        self._delta_base_version = None
        return payload

    # ------------------------------------------------------------------
    # Maintenance helpers
    # ------------------------------------------------------------------
    def digest_tuple(self) -> tuple[int, int, int]:
        """Return ``(version, count, crc32)`` for snapshot embedding."""

        return self.version, self.count, self.crc32

    def trim_stale(self, *, stale_after: float) -> Iterable[int]:
        """Yield name IDs that were removed due to staleness.

        ``stale_after`` is expressed in seconds.  Removed name IDs are not reused
        – the monotonic mapping requirement keeps ``_next_name_id`` untouched.
        """

        if not self._last_used:
            return []

        cutoff = time.monotonic() - stale_after
        removed: list[int] = []
        for name_id, last_used in list(self._last_used.items()):
            if last_used < cutoff:
                removed.append(name_id)

        for name_id in removed:
            self._last_used.pop(name_id, None)
            name = self._id_to_name.pop(name_id, None)
            if name is None:
                continue
            self._name_to_id.pop(name, None)

        if removed:
            self.count = len(self._id_to_name)
            self._recompute_crc32()

        return removed

    def _recompute_crc32(self) -> None:
        payload = bytearray()
        for name_id, name in self.entries():
            payload.extend(struct.pack("<H", name_id))
            payload.extend(name.encode("utf-8"))
        self.crc32 = zlib.crc32(payload) & 0xFFFFFFFF


class RoomState:
    """Mutable NV state for a single room."""

    def __init__(
        self,
        *,
        room_id: str,
        delta_ring_size: int = 10_000,
    ) -> None:
        if delta_ring_size <= 0:
            msg = "delta_ring_size must be positive"
            raise ValueError(msg)

        self.room_id = room_id
        self.nv_seq: int = 0
        self._delta_ring_size = delta_ring_size
        self.delta_log: deque[DeltaRecord] = deque(maxlen=delta_ring_size)
        self._delta_floor: int = 1
        self.pending_deltas: list[DeltaRecord] = []
        self.globals_by_id: dict[int, Any] = {}
        self.clients_by_no: dict[int, dict[int, Any]] = {}
        self.name_table = NameTable()

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------
    def _next_seq(self) -> int:
        self.nv_seq += 1
        return self.nv_seq

    def _append_delta(self, record: DeltaRecord) -> None:
        self.delta_log.append(record)
        self.pending_deltas.append(record)
        stored = len(self.delta_log)
        self._delta_floor = self.nv_seq - stored + 1

    def _ensure_client_scope(self, client_no: int) -> dict[int, Any]:
        return self.clients_by_no.setdefault(client_no, {})

    # ------------------------------------------------------------------
    # Public mutation API
    # ------------------------------------------------------------------
    def set_global(self, name: str, value: Any) -> DeltaRecord:
        name_id, _ = self.name_table.resolve(name)
        self.globals_by_id[name_id] = value
        seq = self._next_seq()
        record = DeltaRecord(
            seq=seq,
            scope="g",
            op="set",
            name_id=name_id,
            value=value,
        )
        self._append_delta(record)
        return record

    def delete_global(self, name: str) -> DeltaRecord | None:
        name_id = self.name_table.lookup(name)
        if name_id is None:
            return None

        self.globals_by_id.pop(name_id, None)
        self.name_table.touch(name_id)
        seq = self._next_seq()
        record = DeltaRecord(
            seq=seq,
            scope="g",
            op="del",
            name_id=name_id,
            value=None,
        )
        self._append_delta(record)
        return record

    def set_client(self, client_no: int, name: str, value: Any) -> DeltaRecord:
        name_id, _ = self.name_table.resolve(name)
        scope = self._ensure_client_scope(client_no)
        scope[name_id] = value
        seq = self._next_seq()
        record = DeltaRecord(
            seq=seq,
            scope="c",
            op="set",
            name_id=name_id,
            value=value,
            client_no=client_no,
        )
        self._append_delta(record)
        return record

    def delete_client(self, client_no: int, name: str) -> DeltaRecord | None:
        name_id = self.name_table.lookup(name)
        if name_id is None:
            return None

        scope = self._ensure_client_scope(client_no)
        if name_id not in scope:
            return None

        scope.pop(name_id, None)
        self.name_table.touch(name_id)
        seq = self._next_seq()
        record = DeltaRecord(
            seq=seq,
            scope="c",
            op="del",
            name_id=name_id,
            value=None,
            client_no=client_no,
        )
        self._append_delta(record)
        return record

    # ------------------------------------------------------------------
    # Snapshot & delta generation
    # ------------------------------------------------------------------
    def build_snapshot_payload(self) -> dict[str, Any]:
        version, count, crc32 = self.name_table.digest_tuple()
        globals_payload = {name_id: value for name_id, value in self.globals_by_id.items()}
        clients_payload = {
            client_no: {name_id: value for name_id, value in scope.items()}
            for client_no, scope in self.clients_by_no.items()
        }
        return {
            "type": SNAPSHOT_MESSAGE_TYPE,
            "roomId": self.room_id,
            "nvSeq": self.nv_seq,
            "globals": globals_payload,
            "clients": clients_payload,
            "nameTable": {
                "version": version,
                "entries": [
                    [name_id, name]
                    for name_id, name in self.name_table.entries()
                ],
                "count": count,
                "crc32": crc32,
            },
        }

    def collect_delta_payload(self) -> dict[str, Any] | None:
        if not self.pending_deltas:
            return None

        items = [record.to_payload() for record in self.pending_deltas]
        base_seq = self.pending_deltas[0].seq - 1
        self.pending_deltas = []
        return {
            "type": DELTA_MESSAGE_TYPE,
            "roomId": self.room_id,
            "baseSeq": base_seq,
            "items": items,
        }

    def collect_name_table_delta(self) -> dict[str, Any] | None:
        return self.name_table.collect_delta_payload(self.room_id)

    def build_name_table_full(self) -> dict[str, Any]:
        return self.name_table.build_full_payload(self.room_id)

    def build_name_table_digest(self) -> dict[str, Any]:
        return self.name_table.build_digest_payload(self.room_id)

    # ------------------------------------------------------------------
    # Ring buffer helpers
    # ------------------------------------------------------------------
    def oldest_seq_available(self) -> int:
        """Return the smallest sequence still available in the ring."""

        return self._delta_floor

    def requires_resync(self, last_seq: int) -> bool:
        """Return ``True`` if the caller must receive a fresh snapshot."""

        return last_seq < self._delta_floor - 1

    # ------------------------------------------------------------------
    # Encoding helpers
    # ------------------------------------------------------------------
    @staticmethod
    def encode_payload(payload: dict[str, Any]) -> bytes:
        """Encode a payload dictionary using MessagePack."""

        return msgpack.dumps(payload, use_bin_type=True)

