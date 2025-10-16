# Repository Guidelines

## Project Structure & Module Organization
- Python server lives in `src/styly_netsync/`; package config in `pyproject.toml`.
- Tests reside in `tests/`; add new suites as `test_<feature>.py`.
- Sample data and integration assets stay under `tests/data/` when needed.
- Unity client sources are in `../STYLY-NetSync-Unity/`, with core runtime under `Packages/com.styly.styly-netsync/Runtime/`.

## Build, Test, and Development Commands
- `python -V` confirms Python 3.11+ before installing dependencies.
- `uv pip install -e .` (or `pip install -e .`) sets up an editable dev environment.
- `ruff check .` runs linting; fix flagged issues before committing.
- `black .` formats Python code using the project’s 88-char line length.
- `mypy src` performs strict type checks; resolve warnings rather than suppressing.
- `pytest --cov=src` executes the test suite with branch coverage reporting.
- `styly-netsync-server` starts the local server; pair with the Unity sample scenes for manual validation.

## Coding Style & Naming Conventions
- Use 4-space indentation across Python and C# files.
- Follow Python naming: modules and functions in snake_case, classes in PascalCase.
- Run Ruff and Black before opening a PR; do not mix formatting styles.
- In Unity C#, keep public APIs PascalCase, private fields camelCase, and prefix serialized fields with `[SerializeField] private`.
- Avoid null-propagation on `UnityEngine.Object` types; perform explicit null checks instead.

## Testing Guidelines
- Write deterministic pytest cases; isolate side effects with fixtures.
- Name tests descriptively (`test_<function>_<scenario>`), and prefer branch coverage where possible.
- For manual Unity checks, open `Assets/Samples_Dev/Demo-01.unity` against a running local server to verify sync behavior.

## Commit & Pull Request Guidelines
- Use Conventional Commits like `feat: add room snapshot API`; limit subject lines to 72 chars.
- Reference GitHub issues in commit bodies when applicable.
- PRs target `develop`; include a summary, linked issues, and test evidence (logs or short capture for Unity flows).
- Keep changelog-ready notes handy for release automation in `.github/workflows/`.

## Security & Configuration Tips
- Store secrets via environment variables or Unity ProjectSettings; never commit them.
- Avoid ad-hoc `.env` files in git; prefer local-only copies.
- Use GitHub CLI (`gh issue list`, `gh pr view`) for issue tracking without exposing tokens.
