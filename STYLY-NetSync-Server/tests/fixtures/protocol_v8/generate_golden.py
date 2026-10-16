"""Generate protocol v8 golden-byte fixtures from the local server serializer.

These .bin files are the cross-language contract: the Unity EditMode test
``BinarySerializerGoldenBytesTests`` serializes the same inputs with the C#
``BinarySerializer`` and asserts byte-for-byte equality. Regenerate (and review
the diff) whenever the wire protocol changes intentionally:

    uv run --directory STYLY-NetSync-Server python tests/fixtures/protocol_v8/generate_golden.py

The companion pytest ``tests/test_golden_fixtures.py`` asserts these files stay
in sync with the serializer without needing Unity.
"""

from __future__ import annotations

import json
from pathlib import Path

from styly_netsync import binary_serializer as bs

# Each case: name -> serialized bytes. Inputs are chosen to avoid quantization
# midpoints so the result is identical across the C# and Python encoders.
# The C# test hardcodes the SAME inputs; keep the two in sync on protocol changes.


def build_cases() -> dict[str, bytes]:
    cases: dict[str, bytes] = {}

    cases["client_hello_visible"] = bs.serialize_client_hello("device-123", False)
    cases["client_hello_stealth"] = bs.serialize_client_hello("device-123", True)

    cases["rpc_broadcast"] = bs.serialize_rpc_message(
        {
            "senderClientNo": 7,
            "deviceId": "sender-dev",
            "targetClientNos": [],
            "functionName": "DoThing",
            "argumentsJson": '["a",1,true]',
        }
    )
    cases["rpc_targeted"] = bs.serialize_rpc_message(
        {
            "senderClientNo": 7,
            "deviceId": "sender-dev",
            "targetClientNos": [3, 4, 65535],
            "functionName": "Targeted",
            "argumentsJson": "[]",
        }
    )

    cases["global_var_set"] = bs.serialize_global_var_set(
        {
            "senderClientNo": 5,
            "deviceId": "dev",
            "variableName": "score",
            "variableValue": "100",
        }
    )
    cases["client_var_set"] = bs.serialize_client_var_set(
        {
            "senderClientNo": 5,
            "deviceId": "dev",
            "targetClientNo": 8,
            "variableName": "hp",
            "variableValue": "50",
        }
    )
    cases["client_var_clear"] = bs.serialize_client_var_clear(
        {"senderClientNo": 3, "deviceId": "dev"}
    )

    cases["device_id_mapping"] = bs.serialize_device_id_mapping(
        [(1, "device-a", False), (2, "device-b", True)],
        version=(0, 17, 1),
    )

    # Object pose: positions are chosen away from int24 quantization midpoints
    # (and rotation is identity), so the C# and Python encoders round to the
    # same quantized values even though the floats are not exactly representable.
    cases["object_pose_identity"] = bs.serialize_object_pose(
        {
            "deviceId": "owner",
            "objectId": 0xDEADBEEF,
            "poseSeq": 77,
            "posX": 1.23,
            "posY": -4.56,
            "posZ": 7.89,
            "rotX": 0.0,
            "rotY": 0.0,
            "rotZ": 0.0,
            "rotW": 1.0,
        }
    )

    return cases


def main() -> None:
    out_dir = Path(__file__).resolve().parent
    cases = build_cases()
    manifest = {}
    for name, payload in cases.items():
        (out_dir / f"{name}.bin").write_bytes(payload)
        manifest[name] = {"length": len(payload)}
    (out_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n"
    )
    print(f"Wrote {len(cases)} golden fixtures to {out_dir}")


if __name__ == "__main__":
    main()
