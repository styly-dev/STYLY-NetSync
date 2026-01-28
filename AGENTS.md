# Repository Guidelines

## Project Structure & Modules
- **Unity client:** `STYLY-NetSync-Unity` (Unity 6). Core runtime under `Packages/com.styly.styly-netsync/Runtime/`; editor tools under `.../Editor/`; sample scenes in `Assets/Samples_Dev/` (e.g., `Demo-01.unity`, `Debug Scene.unity`); package samples in `Packages/com.styly.styly-netsync/Samples~/SimpleDemos/`.
- **Python server:** `STYLY-NetSync-Server` with `src/styly_netsync/`, tests in `tests/`, config in `pyproject.toml`.

## Build, Test, and Dev Commands
- Python env (local): `cd STYLY-NetSync-Server && python -V  # >=3.11`
  - Install (dev): `pip install -e .`  or `uv pip install -e .`
  - Lint/format: `ruff check .` · `black .`
  - Type check: `mypy src`
  - Test: `pytest -q` or `pytest --cov=src`
  - Run server: `styly-netsync-server`
  - Simulate clients: `styly-netsync-simulator --server localhost --room demo --clients 10`
- Unity client: Open `STYLY-NetSync-Unity` with Unity 6. Use sample scenes under `Assets/Samples_Dev/` for manual verification.

## Coding Style & Naming Conventions
- Python: Black line length 88; Ruff enabled; MyPy with strict options (see `pyproject.toml`). Use 4-space indentation; name modules `snake_case`, classes `PascalCase`, functions `snake_case`.
- C# (Unity): 4-space indentation; `PascalCase` for public members/types, `camelCase` for fields/locals; prefix serialized private fields with `[SerializeField]` and `private`.
- Unity-specific rules: Do not use null-propagation (`?.` / `??`) with `UnityEngine.Object` types; use explicit null checks. Do not access Unity APIs from background threads. Do not add `.meta` files manually.

## Testing Guidelines
- Python: PyTest under `STYLY-NetSync-Server/tests/` (unit + integration). Use coverage with branch mode; keep tests deterministic and independent. Name files `test_*.py`.
- Unity: No formal automated tests in this repo; verify via `Demo-01.unity` and `Debug Scene.unity` while the Python server is running.

## Commit & Pull Request Guidelines
- Use Conventional Commits (e.g., `feat: ...`, `fix: ...`, `refactor: ...`). Keep subject ≤72 chars; reference issues (`gh issue view <id>`), and let PR titles mirror the intent.
- Target PRs to `develop`. Manual PRs to `main` are blocked; use the release workflow: `.github/workflows/release-workflow.yml`.
- PR checklist: clear description, linked issues, test evidence (logs/screenshots or short screen capture for Unity), and changelog-worthy notes.

## Security & Configuration Tips
- Avoid committing secrets; use environment variables or Unity ProjectSettings where appropriate.
- Use GitHub CLI for repository data (issues/PRs/releases): e.g., `gh pr create`, `gh issue list`.

