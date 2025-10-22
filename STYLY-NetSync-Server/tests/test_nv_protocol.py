"""Unit tests for the delta-based NV synchronisation primitives."""

from __future__ import annotations

import msgpack
import pytest

from styly_netsync.nv_sync import (
    DELTA_MESSAGE_TYPE,
    NAME_TABLE_DELTA_MESSAGE_TYPE,
    NAME_TABLE_DIGEST_MESSAGE_TYPE,
    NAME_TABLE_FULL_MESSAGE_TYPE,
    SNAPSHOT_MESSAGE_TYPE,
    RoomState,
)


def unpack(payload: dict[str, object] | bytes) -> dict[str, object]:
    if isinstance(payload, bytes):
        return msgpack.loads(payload, strict_map_key=False)
    return payload


def test_snapshot_contains_full_state() -> None:
    room = RoomState(room_id="demo")
    room.set_global("score", 10)
    room.set_client(2, "health", 75)

    snapshot = unpack(room.build_snapshot_payload())

    assert snapshot["type"] == SNAPSHOT_MESSAGE_TYPE
    assert snapshot["nvSeq"] == 2
    assert snapshot["globals"] == {1: 10}
    assert snapshot["clients"] == {2: {2: 75}}

    name_table = snapshot["nameTable"]
    assert name_table["version"] == 2
    assert name_table["count"] == 2
    assert name_table["entries"] == [[1, "score"], [2, "health"]]


def test_delta_batch_and_ring_floor() -> None:
    room = RoomState(room_id="arena", delta_ring_size=3)

    room.set_global("g1", True)
    room.set_global("g2", False)
    room.set_global("g3", 1)
    room.set_global("g4", 2)

    delta = unpack(room.collect_delta_payload())

    assert delta["type"] == DELTA_MESSAGE_TYPE
    assert delta["baseSeq"] == 0
    assert [item["seq"] for item in delta["items"]] == [1, 2, 3, 4]

    assert room.oldest_seq_available() == 2
    assert room.requires_resync(last_seq=0)
    assert not room.requires_resync(last_seq=3)


def test_name_table_delta_and_digest() -> None:
    room = RoomState(room_id="delta")
    room.set_global("alpha", 1)
    room.set_global("beta", 2)

    name_delta = unpack(room.collect_name_table_delta())
    assert name_delta["type"] == NAME_TABLE_DELTA_MESSAGE_TYPE
    assert name_delta["baseVersion"] == 0
    assert name_delta["newVersion"] == 2
    assert name_delta["added"] == [[1, "alpha"], [2, "beta"]]

    digest = unpack(room.build_name_table_digest())
    assert digest["type"] == NAME_TABLE_DIGEST_MESSAGE_TYPE
    assert digest["version"] == 2
    assert digest["count"] == 2
    assert isinstance(digest["crc32"], int)


def test_name_table_full_payload() -> None:
    room = RoomState(room_id="full")
    for idx in range(3):
        room.set_global(f"key{idx}", idx)

    full = unpack(room.build_name_table_full())
    assert full["type"] == NAME_TABLE_FULL_MESSAGE_TYPE
    assert full["version"] == 3
    assert full["entries"] == [[1, "key0"], [2, "key1"], [3, "key2"]]


def test_client_scope_delete() -> None:
    room = RoomState(room_id="clients")
    room.set_client(5, "ammo", 12)
    assert room.delete_client(5, "ammo") is not None

    delta = unpack(room.collect_delta_payload())
    assert len(delta["items"]) == 2
    set_item, del_item = delta["items"]
    assert set_item["op"] == "set"
    assert del_item["op"] == "del"
    assert del_item["clientNo"] == 5


def test_delete_unknown_returns_none() -> None:
    room = RoomState(room_id="noop")
    assert room.delete_global("missing") is None
    assert room.delete_client(1, "missing") is None
    assert room.collect_delta_payload() is None


@pytest.mark.parametrize("count", [1, 3, 5])
def test_encode_roundtrip(count: int) -> None:
    room = RoomState(room_id="encode")
    for idx in range(count):
        room.set_global(f"k{idx}", idx)

    payload = room.collect_delta_payload()
    assert payload is not None
    encoded = RoomState.encode_payload(payload)
    decoded = unpack(encoded)
    assert decoded == payload
