from typing import Dict, Any, Optional, List
from .types import transform, client_transform

def _transform_to_dict(t: Optional[transform]) -> Optional[Dict[str, Any]]:
    if t is None:
        return None
    return {
        "posX": t.pos_x,
        "posY": t.pos_y,
        "posZ": t.pos_z,
        "rotX": t.rot_x,
        "rotY": t.rot_y,
        "rotZ": t.rot_z,
        "isLocalSpace": t.is_local_space
    }

def _dict_to_transform(d: Optional[Dict[str, Any]]) -> Optional[transform]:
    if d is None:
        return None
    return transform(
        pos_x=d.get("posX", 0.0),
        pos_y=d.get("posY", 0.0),
        pos_z=d.get("posZ", 0.0),
        rot_x=d.get("rotX", 0.0),
        rot_y=d.get("rotY", 0.0),
        rot_z=d.get("rotZ", 0.0),
        is_local_space=d.get("isLocalSpace", False)
    )

def client_transform_to_dict(ct: client_transform) -> Dict[str, Any]:
    """Converts a client_transform object to a dictionary with camelCase keys."""
    data = {}
    if ct.client_no is not None:
        data["clientNo"] = ct.client_no
    if ct.device_id is not None:
        data["deviceId"] = ct.device_id
    if ct.physical:
        data["physical"] = _transform_to_dict(ct.physical)
    if ct.head:
        data["head"] = _transform_to_dict(ct.head)
    if ct.right_hand:
        data["rightHand"] = _transform_to_dict(ct.right_hand)
    if ct.left_hand:
        data["leftHand"] = _transform_to_dict(ct.left_hand)
    if ct.virtuals:
        data["virtuals"] = [_transform_to_dict(v) for v in ct.virtuals]
    return data

def dict_to_client_transform(d: Dict[str, Any]) -> client_transform:
    """Converts a dictionary with camelCase keys to a client_transform object."""
    return client_transform(
        client_no=d.get("clientNo"),
        device_id=d.get("deviceId"),
        physical=_dict_to_transform(d.get("physical")),
        head=_dict_to_transform(d.get("head")),
        right_hand=_dict_to_transform(d.get("rightHand")),
        left_hand=_dict_to_transform(d.get("leftHand")),
        virtuals=[_dict_to_transform(v) for v in d.get("virtuals", [])]
    )
