# Benchmark Project Configuration Policy

This benchmark project uses a **minimal pyproject.toml configuration** by design.

## Purpose
- Simple script collection for performance testing
- Not a distributable package
- No package building required

## pyproject.toml Policy

**Current configuration is intentionally complete and sufficient.**

**DO NOT ADD:**
- `[build-system]` section
- `[project.scripts]` section  
- `[project.urls]` section
- Additional metadata: `authors`, `license`, `classifiers`
- `[tool.uv]` dev-dependencies section

Keep the configuration minimal for script collection usage.