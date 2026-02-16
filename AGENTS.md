# Repository Guidelines

This repository is currently a minimal scaffold (no source files and no `.git/` history detected as of 2026-02-15). Use the conventions below as the baseline when you add code and automation.

## Project Structure & Module Organization

Preferred layout (create only what you need):

- `src/`: application/library code (keep modules small and single-purpose)
- `tests/`: automated tests mirroring `src/` (e.g., `tests/test_parser.py` for `src/parser.py`)
- `scripts/`: one-off utilities (CLI entry points, data backfills)
- `docs/`: design notes and usage examples
- `assets/`: fixtures, sample emails, or test corpora (no secrets)

## Build, Test, and Development Commands

Add a `Makefile` (or `package.json`/`pyproject.toml`) and keep common tasks discoverable:

- `make lint`: run formatting + linting
- `make test`: run the test suite locally
- `make run`: run the local app/CLI (document required env vars)

Example target naming: `make fmt`, `make typecheck`, `make ci`.

## Coding Style & Naming Conventions

- Indentation: 2 spaces for YAML/JSON/Markdown; 4 spaces for Python (if used).
- Filenames: `snake_case` for Python, `kebab-case` for scripts, `PascalCase` only when required by a framework.
- Keep I/O at the edges: parsing/validation in pure functions; network/filesystem behind small adapters.

## Testing Guidelines

- Tests live in `tests/` and are deterministic (no real network calls; use fixtures in `assets/`).
- Naming: `test_*.py` or `*.spec.*` depending on the language/tooling you adopt.
- Add regression tests for bugs before fixing them.

## Commit & Pull Request Guidelines

No commit history is available yet; use Conventional Commits:

- `feat: ...`, `fix: ...`, `chore: ...`, `docs: ...`, `test: ...`

PRs should include:

- What/why summary, how to test locally (exact commands)
- Linked issue/task (if applicable)
- Notes on config/env vars and any migrations

## Security & Configuration Tips

- Store secrets in `.env` (gitignored). Never commit API keys or email corpora with PII.
- Add `.env.example` showing required variables and safe defaults.
