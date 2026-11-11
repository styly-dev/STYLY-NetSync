"""Assert the protocol v8 golden fixtures stay in sync with the serializer.

The same .bin files are consumed by the Unity C# test
``BinarySerializerGoldenBytesTests`` to guard cross-language wire parity. This
test regenerates the bytes in-memory and compares them to the committed files,
so a serializer change that forgets to regenerate the fixtures fails here.

Regenerate intentionally with:
    uv run python tests/fixtures/protocol_v8/generate_golden.py
"""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path

import pytest

FIXTURE_DIR = Path(__file__).parent / "fixtures" / "protocol_v8"


def _load_generator():
    spec = importlib.util.spec_from_file_location(
        "generate_golden", FIXTURE_DIR / "generate_golden.py"
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# Built once at collection; the cases are deterministic pure functions of the
# serializer, so every test can share the same dict.
_CASES: dict[str, bytes] = _load_generator().build_cases()


def test_manifest_lists_every_case() -> None:
    manifest = json.loads((FIXTURE_DIR / "manifest.json").read_text())
    assert set(_CASES.keys()) == set(manifest.keys())


@pytest.mark.parametrize("name", sorted(_CASES.keys()))
def test_golden_bytes_match_serializer(name: str) -> None:
    expected = (FIXTURE_DIR / f"{name}.bin").read_bytes()
    assert _CASES[name] == expected, (
        f"Golden fixture '{name}' is stale. Regenerate with "
        "`uv run python tests/fixtures/protocol_v8/generate_golden.py`."
    )
