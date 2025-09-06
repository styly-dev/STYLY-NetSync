# Repository Guidelines

## Project Structure & Module Organization
- Unity client lives here: `STYLY-NetSync-Unity` (Unity 6.0, editor 6000.0.48f1).
- Runtime code: `Packages/com.styly.styly-netsync/Runtime/`.
- Editor tooling: `Packages/com.styly.styly-netsync/Editor/`.
- Sample scenes: `Assets/Samples_Dev/` (e.g., `Demo-01.unity`, `Debug Scene.unity`).
- Python server is in sibling repo `STYLY-NetSync-Server` (used for local testing).

## Build, Test, and Development Commands
- Unity client: open this folder with Unity 6000.0.48f1 and use the sample scenes for manual checks.
- Run server (from the Python repo): `cd STYLY-NetSync-Server && uv pip install -e . && styly-netsync-server`.
- Simulate clients: `styly-netsync-simulator --server tcp://localhost --room demo --clients 10`.

## Coding Style & Naming Conventions
- Indentation: 4 spaces. Public APIs use `PascalCase`; fields/locals `camelCase`. Serialized privates: `[SerializeField] private Type name;`.
- Unity rules: do not use null-propagation (`?.`/`??`) with `UnityEngine.Object` types; use explicit null checks. Do not call Unity APIs from background threads—access Unity objects only on the main thread. Do not add `.meta` files manually.

## Testing Guidelines
- No automated Unity tests in this repo. Verify with `Demo-01.unity` or `Debug Scene.unity` while the Python server is running.
- Capture short logs or a screen recording that demonstrates sync state and any fixes.

## Commit & Pull Request Guidelines
- Use Conventional Commits (e.g., `feat: ...`, `fix: ...`). Keep subject ≤ 72 chars.
- Target PRs to `develop`. PRs to `main` are blocked; releases go through `.github/workflows/release-workflow.yml`.
- PR checklist: clear description, linked issues, test evidence (logs/screens or short capture), and notes for changelog.

## GitHub Workflow (CLI)
- Use GitHub CLI for repo data: `gh issue list`, `gh issue view <id>`, `gh pr create`, `gh pr status`, `gh release list`.
- Reference issues in commits/PRs and keep titles descriptive and action-oriented.

## Security & Configuration Tips
- Do not commit secrets or tokens. Use environment variables or Unity ProjectSettings where appropriate.
- Large/binary assets: prefer project packages or external hosting over embedding in the package runtime.

