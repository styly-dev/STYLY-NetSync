# Repository Guidelines

## Project Structure & Module Organization
- Unity client (Unity 6): `STYLY-NetSync-Unity`
  Runtime: `Packages/com.styly.styly-netsync/Runtime/`; Editor: `Packages/com.styly.styly-netsync/Editor/`; Samples: `Assets/Samples_Dev/` (e.g., `Demo-01.unity`, `Debug Scene.unity`).
- Python server (this repo): `STYLY-NetSync-Server`
  Source: `src/styly_netsync/`; Tests: `tests/`; Config: `pyproject.toml`.

## Build, Test, and Development Commands
- Python version: `python -V` (>=3.11).
- Install (dev): `pip install -e .` or `uv pip install -e .`.
- Lint/format: `ruff check .`; `black .`.
- Type check: `mypy src`.
- Test: `pytest -q` or `pytest --cov=src`.
- Run server: `styly-netsync-server`.
- Simulate clients: `styly-netsync-simulator --server localhost --room demo --clients 10`.
- Additional simulator options: `--transform-send-rate`, `--spawn-batch-size`, `--spawn-batch-interval`, `--no-sync-battery`.
- Unity: open `STYLY-NetSync-Unity` with Unity 6; use sample scenes for manual checks.

## Coding Style & Naming Conventions
- Python: 4 spaces; Black (line length 88), Ruff, MyPy (strict). Names — modules `snake_case`, classes `PascalCase`, functions `snake_case`.
- C# (Unity): 4 spaces; public members/types `PascalCase`, fields/locals `camelCase`; use `[SerializeField] private` for serialized fields.
- Unity rules: never use null-propagation with `UnityEngine.Object`; do not access Unity APIs from background threads; do not add `.meta` files manually.

## Testing Guidelines
- Python tests in `tests/` named `test_*.py`; keep deterministic and independent; prefer branch coverage. Run `pytest --cov=src`.
- Unity verification: open `Assets/Samples_Dev/` scenes while the Python server is running.

## Commit & Pull Request Guidelines
- Use Conventional Commits (e.g., `feat:`, `fix:`); keep subject ≤72 chars; reference issues.
- Target PRs to `develop`; `main` updates via `.github/workflows/release-workflow.yml`.
- Use GitHub CLI: `gh issue list`, `gh pr create`, `gh pr view`.
- PR checklist: clear description, linked issues, test evidence (logs/screenshots or short capture for Unity), and changelog notes.

## Security & Configuration Tips
- Never commit secrets; prefer environment variables or Unity ProjectSettings; use local `.env` only for development.

