"""Unity project setup helpers for STYLY NetSync.

GUI-free logic used by :mod:`styly_netsync.launcher` to add or update the STYLY
NetSync Unity package in a Unity project without Node.js, the OpenUPM CLI, or a
terminal.

Installing the package is exactly two edits to ``Packages/manifest.json``:

1. Register the OpenUPM scoped registry for the scopes NetSync and its
   dependencies are published under.
2. Pin ``com.styly.styly-netsync`` to the requested version.

Unity resolves the package itself the next time the Editor regains focus, so no
external tooling is involved.
"""

from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any

PACKAGE_NAME = "com.styly.styly-netsync"
OPENUPM_REGISTRY_NAME = "package.openupm.com"
OPENUPM_REGISTRY_URL = "https://package.openupm.com"

# Scopes that NetSync and its transitive OpenUPM dependencies are published
# under. Everything else (com.unity.*) resolves from the Unity registry.
REQUIRED_SCOPES: tuple[str, ...] = (
    "com.styly.device-id-provider",
    "com.styly.shader-collection.urp",
    "com.styly.styly-netsync",
    "com.styly.styly-xr-rig",
    "org.nuget.asyncio",
    "org.nuget.nacl.net",
    "org.nuget.netmq",
    "org.nuget.system.buffers",
    "org.nuget.system.memory",
    "org.nuget.system.numerics.vectors",
    "org.nuget.system.runtime.compilerservices.unsafe",
    "org.nuget.system.security.principal.windows",
    "org.nuget.system.servicemodel.primitives",
)

BACKUP_SUFFIX = ".netsync-backup"

_VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-+]+)?$")
_EDITOR_VERSION_RE = re.compile(r"^m_EditorVersion:\s*(\S+)", re.MULTILINE)


class UnitySetupError(Exception):
    """Raised when a Unity project cannot be inspected or modified."""


@dataclass(frozen=True)
class UnityProject:
    """A located Unity project and its current STYLY NetSync state."""

    root: Path
    manifest_path: Path
    editor_version: str | None
    installed_version: str | None
    is_embedded: bool

    @property
    def name(self) -> str:
        return self.root.name

    def summary(self) -> str:
        """Human-readable status shown in the launcher UI."""
        lines = [f"Project: {self.root}"]
        lines.append(f"Unity version: {self.editor_version or 'unknown'}")
        if self.is_embedded:
            lines.append(
                f"NetSync: embedded at Packages/{PACKAGE_NAME} "
                "(managed by this repository, not by the registry)"
            )
        elif self.installed_version:
            lines.append(f"NetSync: {self.installed_version} (from registry)")
        else:
            lines.append("NetSync: not installed")
        return "\n".join(lines)


def normalize_project_dir(project_dir: Path | str) -> Path:
    """Resolve *project_dir* to a Unity project root.

    Accepts the project root itself or a folder inside it that unambiguously
    identifies the root (``Assets``, ``Packages``, or ``ProjectSettings``), so a
    stray pick in the folder browser still works.
    """
    path = Path(project_dir).expanduser()
    try:
        path = path.resolve()
    except OSError as exc:  # pragma: no cover - platform specific
        raise UnitySetupError(f"Cannot read '{project_dir}': {exc}") from exc

    if path.name in {"Assets", "Packages", "ProjectSettings"} and _looks_like_project(
        path.parent
    ):
        return path.parent
    return path


def _looks_like_project(root: Path) -> bool:
    return (root / "Assets").is_dir() and (root / "ProjectSettings").is_dir()


def inspect_project(project_dir: Path | str) -> UnityProject:
    """Inspect a Unity project folder.

    Raises:
        UnitySetupError: if the folder is not a Unity project.
    """
    root = normalize_project_dir(project_dir)

    if not root.is_dir():
        raise UnitySetupError(f"Folder does not exist:\n{root}")
    if not _looks_like_project(root):
        raise UnitySetupError(
            f"'{root}' is not a Unity project.\n\n"
            "Pick the folder that contains the 'Assets' and 'ProjectSettings' "
            "folders."
        )

    manifest_path = root / "Packages" / "manifest.json"
    embedded_manifest = root / "Packages" / PACKAGE_NAME / "package.json"
    is_embedded = embedded_manifest.is_file()

    installed_version: str | None = None
    if is_embedded:
        installed_version = _read_json_str(embedded_manifest, "version")
    elif manifest_path.is_file():
        manifest = read_manifest(manifest_path)
        dependencies = manifest.get("dependencies")
        if isinstance(dependencies, dict):
            value = dependencies.get(PACKAGE_NAME)
            if isinstance(value, str):
                installed_version = value

    return UnityProject(
        root=root,
        manifest_path=manifest_path,
        editor_version=read_editor_version(root),
        installed_version=installed_version,
        is_embedded=is_embedded,
    )


def read_editor_version(root: Path) -> str | None:
    """Read ``m_EditorVersion`` from ``ProjectSettings/ProjectVersion.txt``."""
    version_file = root / "ProjectSettings" / "ProjectVersion.txt"
    try:
        text = version_file.read_text(encoding="utf-8")
    except OSError:
        return None
    match = _EDITOR_VERSION_RE.search(text)
    return match.group(1) if match else None


def _read_json_str(path: Path, key: str) -> str | None:
    try:
        data: Any = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    if isinstance(data, dict):
        value = data.get(key)
        if isinstance(value, str):
            return value
    return None


def read_manifest(manifest_path: Path) -> dict[str, Any]:
    """Load ``Packages/manifest.json``.

    Raises:
        UnitySetupError: if the file is missing or is not valid JSON.
    """
    try:
        text = manifest_path.read_text(encoding="utf-8")
    except FileNotFoundError as exc:
        raise UnitySetupError(f"Manifest not found:\n{manifest_path}") from exc
    except OSError as exc:
        raise UnitySetupError(f"Cannot read '{manifest_path}': {exc}") from exc

    try:
        data: Any = json.loads(text)
    except json.JSONDecodeError as exc:
        raise UnitySetupError(
            f"'{manifest_path}' is not valid JSON (line {exc.lineno}): {exc.msg}"
        ) from exc

    if not isinstance(data, dict):
        raise UnitySetupError(f"'{manifest_path}' must contain a JSON object.")
    return data


def validate_version(version: str) -> str:
    """Validate a package version string such as ``0.17.4``."""
    cleaned = version.strip()
    if not _VERSION_RE.match(cleaned):
        raise UnitySetupError(
            f"'{version}' is not a valid package version (expected e.g. 0.17.4)."
        )
    return cleaned


def apply_manifest_changes(
    manifest: dict[str, Any], version: str
) -> tuple[dict[str, Any], list[str]]:
    """Return *manifest* updated for NetSync *version*, plus a change log.

    The manifest is modified in place and also returned. An empty change log
    means the project was already set up for that exact version.
    """
    version = validate_version(version)
    changes: list[str] = []

    registries = manifest.setdefault("scopedRegistries", [])
    if not isinstance(registries, list):
        raise UnitySetupError("'scopedRegistries' in manifest.json must be a list.")

    registry = _find_openupm_registry(registries)
    if registry is None:
        registries.append(
            {
                "name": OPENUPM_REGISTRY_NAME,
                "url": OPENUPM_REGISTRY_URL,
                "scopes": list(REQUIRED_SCOPES),
            }
        )
        changes.append(f"Added scoped registry '{OPENUPM_REGISTRY_NAME}'")
    else:
        scopes = registry.get("scopes")
        if not isinstance(scopes, list):
            scopes = []
        existing = [s for s in scopes if isinstance(s, str)]
        missing = [s for s in REQUIRED_SCOPES if s not in existing]
        if missing:
            registry["scopes"] = sorted(existing + missing)
            changes.append(
                f"Added {len(missing)} scope(s) to '{OPENUPM_REGISTRY_NAME}': "
                + ", ".join(missing)
            )

    dependencies = manifest.setdefault("dependencies", {})
    if not isinstance(dependencies, dict):
        raise UnitySetupError("'dependencies' in manifest.json must be an object.")

    current = dependencies.get(PACKAGE_NAME)
    if current != version:
        _set_dependency(dependencies, PACKAGE_NAME, version)
        if isinstance(current, str):
            changes.append(f"Updated {PACKAGE_NAME} {current} -> {version}")
        else:
            changes.append(f"Added {PACKAGE_NAME} {version}")

    return manifest, changes


def _find_openupm_registry(registries: list[Any]) -> dict[str, Any] | None:
    for entry in registries:
        if not isinstance(entry, dict):
            continue
        url = entry.get("url")
        if isinstance(url, str) and url.rstrip("/") == OPENUPM_REGISTRY_URL:
            return entry
    for entry in registries:
        if isinstance(entry, dict) and entry.get("name") == OPENUPM_REGISTRY_NAME:
            return entry
    return None


def _set_dependency(dependencies: dict[str, Any], name: str, version: str) -> None:
    """Set a dependency, keeping alphabetical order when the file already is."""
    if name in dependencies:
        dependencies[name] = version
        return

    keys = list(dependencies)
    if keys and keys != sorted(keys):
        dependencies[name] = version
        return

    merged = dict(dependencies)
    merged[name] = version
    reordered = {key: merged[key] for key in sorted(merged)}
    dependencies.clear()
    dependencies.update(reordered)


def install_package(
    project_dir: Path | str, version: str, *, backup: bool = True
) -> tuple[UnityProject, list[str]]:
    """Add or update STYLY NetSync in a Unity project.

    Returns the inspected project and the list of applied changes; an empty list
    means the project was already up to date.

    Raises:
        UnitySetupError: if the project or manifest cannot be used.
    """
    version = validate_version(version)
    project = inspect_project(project_dir)

    if project.is_embedded:
        raise UnitySetupError(
            f"This project embeds NetSync at Packages/{PACKAGE_NAME}.\n\n"
            "An embedded package always wins over the registry, so there is "
            "nothing to install - edit the embedded copy instead."
        )

    manifest = read_manifest(project.manifest_path)
    manifest, changes = apply_manifest_changes(manifest, version)
    if not changes:
        return project, changes

    if backup:
        _write_backup(project.manifest_path)

    _write_manifest(project.manifest_path, manifest)
    return project, changes


def _write_backup(manifest_path: Path) -> None:
    backup_path = manifest_path.with_name(manifest_path.name + BACKUP_SUFFIX)
    try:
        shutil.copyfile(manifest_path, backup_path)
    except OSError as exc:
        raise UnitySetupError(f"Cannot write backup '{backup_path}': {exc}") from exc


def _write_manifest(manifest_path: Path, manifest: dict[str, Any]) -> None:
    payload = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    try:
        manifest_path.write_text(payload, encoding="utf-8", newline="\n")
    except OSError as exc:
        raise UnitySetupError(f"Cannot write '{manifest_path}': {exc}") from exc
