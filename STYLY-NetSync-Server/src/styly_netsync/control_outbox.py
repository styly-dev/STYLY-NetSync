"""Bounded event delivery and replaceable state for the server control lane."""

from __future__ import annotations

import threading
from collections import OrderedDict, deque
from dataclasses import dataclass, field
from queue import Empty, Full

ControlPacket = tuple[bytes, bytes, bytes]


class ControlEventQueue:
    """Reserve in-flight capacity so retrying a packet never needs a free slot.

    Only the network thread takes/finishes batches. Producers can enqueue while
    it sends. Deferred packets return ahead of newer events, preserving order
    for each identity without blocking delivery to other identities in a batch.
    """

    def __init__(self, maxsize: int) -> None:
        self.maxsize = maxsize
        self._items: OrderedDict[bytes, deque[ControlPacket]] = OrderedDict()
        self._queued = 0
        self._inflight = 0
        self._lock = threading.Lock()

    def put_nowait(self, packet: ControlPacket) -> None:
        with self._lock:
            if self._queued + self._inflight >= self.maxsize:
                raise Full
            self._items.setdefault(packet[0], deque()).append(packet)
            self._queued += 1

    def get_nowait(self) -> ControlPacket:
        with self._lock:
            if not self._items:
                raise Empty
            return self._pop()

    def _pop(self) -> ControlPacket:
        identity, items = self._items.popitem(last=False)
        packet = items.popleft()
        if items:
            self._items[identity] = items
        self._queued -= 1
        return packet

    def qsize(self) -> int:
        with self._lock:
            return self._queued + self._inflight

    def empty(self) -> bool:
        return self.qsize() == 0

    def take_batch(self, limit: int) -> list[ControlPacket]:
        with self._lock:
            batch = [self._pop() for _ in range(min(limit, self._queued))]
            self._inflight += len(batch)
            return batch

    def finish_batch(self, size: int, deferred: list[ControlPacket]) -> None:
        with self._lock:
            for packet in reversed(deferred):
                self._items.setdefault(packet[0], deque()).appendleft(packet)
            self._queued += len(deferred)
            self._inflight -= size


@dataclass
class PendingStateSync:
    """One receiver's intent, protected by the server room lock.

    Payloads are built from authoritative state when sending, never accumulated
    in this object. None means all client variables; an empty set means none.
    A revision guards completion against a concurrent new sync request.
    """

    identity: bytes
    since: float
    revision: int = 0
    mapping_generation: int = -1
    bootstrap: bool = True
    globals: bool = False
    clients: set[int] | None = field(default_factory=set)
    retry_at: float = 0.0
