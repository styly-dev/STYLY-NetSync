"""
Adapters for converting between snake_case Python naming and camelCase wire protocol.

The binary_serializer expects camelCase field names to match the Unity C# client.
This adapter layer converts between Python snake_case and wire format.
"""

import math
from typing import Any

from .types import client_transform_data, transform_data


def transform_to_wire(t: transform_data) -> dict[str, Any]:
    """Convert snake_case transform to camelCase wire format."""
    return {
        "posX": t.pos_x,
        "posY": t.pos_y,
        "posZ": t.pos_z,
        "rotX": t.rot_x,
        "rotY": t.rot_y,
        "rotZ": t.rot_z,
        "isLocalSpace": t.is_local_space,
    }


def transform_from_wire(data: dict[str, Any]) -> transform_data:
    """Convert camelCase wire format to snake_case transform."""
    return transform_data(
        pos_x=data.get("posX", 0.0),
        pos_y=data.get("posY", 0.0),
        pos_z=data.get("posZ", 0.0),
        rot_x=data.get("rotX", 0.0),
        rot_y=data.get("rotY", 0.0),
        rot_z=data.get("rotZ", 0.0),
        is_local_space=data.get("isLocalSpace", False),
    )


def client_transform_to_wire(ct: client_transform_data) -> dict[str, Any]:
    """Convert snake_case client_transform_data to camelCase wire format."""
    result = {}

    if ct.device_id is not None:
        result["deviceId"] = ct.device_id
    if ct.client_no is not None:
        result["clientNo"] = ct.client_no
    if ct.physical is not None:
        result["physical"] = transform_to_wire(ct.physical)
    if ct.head is not None:
        result["head"] = transform_to_wire(ct.head)
    if ct.right_hand is not None:
        result["rightHand"] = transform_to_wire(ct.right_hand)
    if ct.left_hand is not None:
        result["leftHand"] = transform_to_wire(ct.left_hand)
    if ct.virtuals is not None:
        result["virtuals"] = [transform_to_wire(v) for v in ct.virtuals]

    return result


def client_transform_from_wire(data: dict[str, Any]) -> client_transform_data:
    """Convert camelCase wire format to snake_case client_transform_data."""
    result = client_transform_data()

    result.device_id = data.get("deviceId")
    result.client_no = data.get("clientNo")

    if "physical" in data and data["physical"]:
        result.physical = transform_from_wire(data["physical"])
    if "head" in data and data["head"]:
        result.head = transform_from_wire(data["head"])
    if "rightHand" in data and data["rightHand"]:
        result.right_hand = transform_from_wire(data["rightHand"])
    if "leftHand" in data and data["leftHand"]:
        result.left_hand = transform_from_wire(data["leftHand"])
    if "virtuals" in data and data["virtuals"]:
        result.virtuals = [transform_from_wire(v) for v in data["virtuals"]]

    return result


def create_stealth_transform() -> client_transform_data:
    """Create a stealth handshake transform with NaN values."""
    nan_transform = transform_data(
        pos_x=float("nan"),
        pos_y=float("nan"),
        pos_z=float("nan"),
        rot_x=float("nan"),
        rot_y=float("nan"),
        rot_z=float("nan"),
    )
    return client_transform_data(
        physical=nan_transform,
        head=nan_transform,
        right_hand=nan_transform,
        left_hand=nan_transform,
        virtuals=[],
    )


def is_stealth_transform(ct: client_transform_data) -> bool:
    """Check if a client_transform_data represents a stealth client (all NaN values)."""
    if not ct.physical or not ct.head or not ct.right_hand or not ct.left_hand:
        return False

    def is_nan_transform(t: transform_data) -> bool:
        return (
            math.isnan(t.pos_x)
            and math.isnan(t.pos_y)
            and math.isnan(t.pos_z)
            and math.isnan(t.rot_x)
            and math.isnan(t.rot_y)
            and math.isnan(t.rot_z)
        )

    return (
        is_nan_transform(ct.physical)
        and is_nan_transform(ct.head)
        and is_nan_transform(ct.right_hand)
        and is_nan_transform(ct.left_hand)
    )
