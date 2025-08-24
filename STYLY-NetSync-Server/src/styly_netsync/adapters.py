from __future__ import annotations

import time
from typing import Any

from .types import client_transform, room_snapshot, transform


def transform_to_wire(t: transform) -> dict[str, Any]:
    return {
        "posX": t.pos_x,
        "posY": t.pos_y,
        "posZ": t.pos_z,
        "rotX": t.rot_x,
        "rotY": t.rot_y,
        "rotZ": t.rot_z,
        "isLocalSpace": t.is_local_space,
    }


def transform_from_wire(data: dict[str, Any]) -> transform:
    return transform(
        pos_x=data.get("posX", 0.0),
        pos_y=data.get("posY", 0.0),
        pos_z=data.get("posZ", 0.0),
        rot_x=data.get("rotX", 0.0),
        rot_y=data.get("rotY", 0.0),
        rot_z=data.get("rotZ", 0.0),
        is_local_space=data.get("isLocalSpace", False),
    )


def client_transform_to_wire(ct: client_transform) -> dict[str, Any]:
    data: dict[str, Any] = {}
    if ct.client_no is not None:
        data["clientNo"] = ct.client_no
    if ct.device_id is not None:
        data["deviceId"] = ct.device_id
    if ct.physical is not None:
        data["physical"] = transform_to_wire(ct.physical)
    if ct.head is not None:
        data["head"] = transform_to_wire(ct.head)
    if ct.right_hand is not None:
        data["rightHand"] = transform_to_wire(ct.right_hand)
    if ct.left_hand is not None:
        data["leftHand"] = transform_to_wire(ct.left_hand)
    if ct.virtuals:
        data["virtuals"] = [transform_to_wire(v) for v in ct.virtuals]
    return data


def client_transform_from_wire(data: dict[str, Any]) -> client_transform:
    virtuals: list[transform] | None = None
    if "virtuals" in data:
        virtuals = [transform_from_wire(v) for v in data["virtuals"]]
    return client_transform(
        client_no=data.get("clientNo"),
        device_id=data.get("deviceId"),
        physical=(
            transform_from_wire(data["physical"]) if data.get("physical") else None
        ),
        head=transform_from_wire(data["head"]) if data.get("head") else None,
        right_hand=(
            transform_from_wire(data["rightHand"]) if data.get("rightHand") else None
        ),
        left_hand=(
            transform_from_wire(data["leftHand"]) if data.get("leftHand") else None
        ),
        virtuals=virtuals,
    )


def room_snapshot_from_wire(data: dict[str, Any]) -> room_snapshot:
    clients: dict[int, client_transform] = {}
    for client in data.get("clients", []):
        ct = client_transform_from_wire(client)
        if ct.client_no is not None:
            clients[ct.client_no] = ct
    return room_snapshot(
        room_id=data.get("roomId", ""), clients=clients, timestamp=time.monotonic()
    )
