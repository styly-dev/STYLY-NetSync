"""Tests for the terminal-free Unity project setup helpers."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from styly_netsync.unity_setup import (
    BACKUP_SUFFIX,
    OPENUPM_REGISTRY_NAME,
    OPENUPM_REGISTRY_URL,
    PACKAGE_NAME,
    REQUIRED_SCOPES,
    UnitySetupError,
    apply_manifest_changes,
    inspect_project,
    install_package,
    normalize_project_dir,
    read_editor_version,
    validate_version,
)

VERSION = "0.17.4"


def make_project(
    root: Path,
    *,
    manifest: dict[str, object] | None = None,
    editor_version: str | None = "6000.0.70f1",
) -> Path:
    """Create a minimal on-disk Unity project."""
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)

    if editor_version is not None:
        (root / "ProjectSettings" / "ProjectVersion.txt").write_text(
            f"m_EditorVersion: {editor_version}\n"
            f"m_EditorVersionWithRevision: {editor_version} (0d9e1a373c8b)\n",
            encoding="utf-8",
        )

    payload = manifest if manifest is not None else {"dependencies": {}}
    (root / "Packages" / "manifest.json").write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    return root


def read_manifest_file(root: Path) -> dict[str, object]:
    text = (root / "Packages" / "manifest.json").read_text(encoding="utf-8")
    loaded = json.loads(text)
    assert isinstance(loaded, dict)
    return loaded


# --------------------------------------------------------------------------
# Project detection
# --------------------------------------------------------------------------


def test_inspect_rejects_non_unity_folder(tmp_path: Path) -> None:
    with pytest.raises(UnitySetupError, match="not a Unity project"):
        inspect_project(tmp_path)


def test_inspect_rejects_missing_folder(tmp_path: Path) -> None:
    with pytest.raises(UnitySetupError, match="does not exist"):
        inspect_project(tmp_path / "nope")


def test_normalize_accepts_folder_inside_project(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    for child in ("Assets", "Packages", "ProjectSettings"):
        assert normalize_project_dir(root / child) == root


def test_normalize_keeps_unrelated_assets_folder(tmp_path: Path) -> None:
    stray = tmp_path / "Assets"
    stray.mkdir()
    assert normalize_project_dir(stray) == stray


def test_inspect_reports_editor_and_package_version(tmp_path: Path) -> None:
    root = make_project(
        tmp_path / "MyGame",
        manifest={"dependencies": {PACKAGE_NAME: "0.17.0"}},
    )
    project = inspect_project(root)

    assert project.root == root
    assert project.editor_version == "6000.0.70f1"
    assert project.installed_version == "0.17.0"
    assert project.is_embedded is False
    assert "NetSync: 0.17.0" in project.summary()


def test_inspect_detects_embedded_package(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    embedded = root / "Packages" / PACKAGE_NAME
    embedded.mkdir()
    (embedded / "package.json").write_text(
        json.dumps({"name": PACKAGE_NAME, "version": "0.17.4"}), encoding="utf-8"
    )

    project = inspect_project(root)
    assert project.is_embedded is True
    assert project.installed_version == "0.17.4"
    assert "embedded" in project.summary()


def test_read_editor_version_missing_file(tmp_path: Path) -> None:
    assert read_editor_version(tmp_path) is None


# --------------------------------------------------------------------------
# Manifest editing
# --------------------------------------------------------------------------


def test_apply_adds_registry_and_dependency() -> None:
    manifest: dict[str, object] = {"dependencies": {"com.unity.ugui": "2.0.0"}}

    updated, changes = apply_manifest_changes(manifest, VERSION)

    registries = updated["scopedRegistries"]
    assert isinstance(registries, list)
    assert registries[0]["url"] == OPENUPM_REGISTRY_URL
    assert registries[0]["name"] == OPENUPM_REGISTRY_NAME
    assert set(registries[0]["scopes"]) == set(REQUIRED_SCOPES)

    dependencies = updated["dependencies"]
    assert isinstance(dependencies, dict)
    assert dependencies[PACKAGE_NAME] == VERSION
    assert dependencies["com.unity.ugui"] == "2.0.0"
    assert len(changes) == 2


def test_apply_merges_into_existing_registry_without_dropping_scopes() -> None:
    manifest: dict[str, object] = {
        "dependencies": {},
        "scopedRegistries": [
            {
                "name": "package.openupm.com",
                "url": "https://package.openupm.com/",
                "scopes": ["com.veriorpies.parrelsync", "org.nuget.netmq"],
            }
        ],
    }

    updated, changes = apply_manifest_changes(manifest, VERSION)

    registries = updated["scopedRegistries"]
    assert isinstance(registries, list)
    assert len(registries) == 1, "must not add a second OpenUPM registry"
    scopes = registries[0]["scopes"]
    assert "com.veriorpies.parrelsync" in scopes, "existing scopes must survive"
    assert set(REQUIRED_SCOPES).issubset(scopes)
    assert scopes == sorted(scopes)
    assert any("scope" in change for change in changes)


def test_apply_leaves_other_registries_alone() -> None:
    other = {"name": "corp", "url": "https://npm.corp.example", "scopes": ["com.corp"]}
    manifest: dict[str, object] = {"dependencies": {}, "scopedRegistries": [other]}

    updated, _ = apply_manifest_changes(manifest, VERSION)

    registries = updated["scopedRegistries"]
    assert isinstance(registries, list)
    assert other in registries
    assert len(registries) == 2


def test_apply_is_idempotent() -> None:
    manifest: dict[str, object] = {"dependencies": {}}
    apply_manifest_changes(manifest, VERSION)

    _, changes = apply_manifest_changes(manifest, VERSION)

    assert changes == []


def test_apply_reports_version_upgrade() -> None:
    manifest: dict[str, object] = {
        "dependencies": {PACKAGE_NAME: "0.16.0"},
        "scopedRegistries": [
            {
                "name": OPENUPM_REGISTRY_NAME,
                "url": OPENUPM_REGISTRY_URL,
                "scopes": list(REQUIRED_SCOPES),
            }
        ],
    }

    _, changes = apply_manifest_changes(manifest, VERSION)

    assert changes == [f"Updated {PACKAGE_NAME} 0.16.0 -> {VERSION}"]


def test_apply_keeps_dependencies_sorted_when_already_sorted() -> None:
    manifest: dict[str, object] = {
        "dependencies": {"com.unity.ai.navigation": "2.0.11", "com.unity.ugui": "2.0.0"}
    }

    updated, _ = apply_manifest_changes(manifest, VERSION)

    dependencies = updated["dependencies"]
    assert isinstance(dependencies, dict)
    assert list(dependencies) == sorted(dependencies)


def test_apply_appends_when_dependencies_are_unsorted() -> None:
    manifest: dict[str, object] = {
        "dependencies": {"com.unity.ugui": "2.0.0", "com.unity.ai.navigation": "2.0.11"}
    }

    updated, _ = apply_manifest_changes(manifest, VERSION)

    dependencies = updated["dependencies"]
    assert isinstance(dependencies, dict)
    assert list(dependencies)[-1] == PACKAGE_NAME
    assert list(dependencies)[:2] == ["com.unity.ugui", "com.unity.ai.navigation"]


def test_apply_rejects_wrong_types() -> None:
    with pytest.raises(UnitySetupError, match="must be a list"):
        apply_manifest_changes({"scopedRegistries": {}}, VERSION)
    with pytest.raises(UnitySetupError, match="must be an object"):
        apply_manifest_changes({"dependencies": []}, VERSION)


@pytest.mark.parametrize("version", ["0.17.4", "1.0.0", "2.3.4-beta.1", "1.2.3+build"])
def test_validate_version_accepts(version: str) -> None:
    assert validate_version(f"  {version} ") == version


@pytest.mark.parametrize("version", ["", "latest", "0.17", "1.2.3; rm -rf /", "v1.2.3"])
def test_validate_version_rejects(version: str) -> None:
    with pytest.raises(UnitySetupError, match="not a valid package version"):
        validate_version(version)


# --------------------------------------------------------------------------
# install_package
# --------------------------------------------------------------------------


def test_install_writes_manifest_and_backup(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")

    project, changes = install_package(root, VERSION)

    assert changes
    assert project.root == root
    manifest = read_manifest_file(root)
    dependencies = manifest["dependencies"]
    assert isinstance(dependencies, dict)
    assert dependencies[PACKAGE_NAME] == VERSION

    backup = root / "Packages" / ("manifest.json" + BACKUP_SUFFIX)
    assert backup.is_file()
    assert json.loads(backup.read_text(encoding="utf-8")) == {"dependencies": {}}


def test_install_second_run_is_a_no_op(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    install_package(root, VERSION)
    before = (root / "Packages" / "manifest.json").read_text(encoding="utf-8")

    _, changes = install_package(root, VERSION)

    assert changes == []
    assert (root / "Packages" / "manifest.json").read_text(encoding="utf-8") == before


def test_install_can_skip_backup(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")

    install_package(root, VERSION, backup=False)

    assert not (root / "Packages" / ("manifest.json" + BACKUP_SUFFIX)).exists()


def test_install_refuses_embedded_project(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    embedded = root / "Packages" / PACKAGE_NAME
    embedded.mkdir()
    (embedded / "package.json").write_text('{"version": "0.17.4"}', encoding="utf-8")

    with pytest.raises(UnitySetupError, match="embeds NetSync"):
        install_package(root, VERSION)


def test_install_reports_broken_manifest(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    (root / "Packages" / "manifest.json").write_text("{ nope", encoding="utf-8")

    with pytest.raises(UnitySetupError, match="not valid JSON"):
        install_package(root, VERSION)


def test_install_reports_missing_manifest(tmp_path: Path) -> None:
    root = make_project(tmp_path / "MyGame")
    (root / "Packages" / "manifest.json").unlink()

    with pytest.raises(UnitySetupError, match="Manifest not found"):
        install_package(root, VERSION)


def test_install_output_is_valid_json_unity_can_read(tmp_path: Path) -> None:
    root = make_project(
        tmp_path / "MyGame",
        manifest={
            "dependencies": {"com.unity.ugui": "2.0.0"},
            "scopedRegistries": [],
            "testables": ["com.unity.inputsystem"],
        },
    )

    install_package(root, VERSION)

    manifest = read_manifest_file(root)
    assert manifest["testables"] == ["com.unity.inputsystem"], "keys must be preserved"
    text = (root / "Packages" / "manifest.json").read_text(encoding="utf-8")
    assert text.endswith("\n")
    assert "\r\n" not in text


def test_manifest_is_written_without_a_byte_order_mark(tmp_path: Path) -> None:
    """Unity's Package Manager rejects manifest.json outright if it has a BOM.

    It fails with 'Non-whitespace before {[' and loads no packages at all, so
    this is not a cosmetic detail.
    """
    root = make_project(tmp_path / "MyGame")

    install_package(root, VERSION)

    raw = (root / "Packages" / "manifest.json").read_bytes()
    assert not raw.startswith(b"\xef\xbb\xbf"), "a BOM breaks Unity package resolution"
    assert raw.lstrip()[:1] == b"{"
