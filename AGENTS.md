# Repository Guidelines

This repository is a .NET/C# CLI project. Keep changes aligned with the current stack and prefer simple, maintainable implementations.

## Project Structure & Module Organization

Preferred layout (create only what you need):

- `src/`: application/library code (keep modules small and single-purpose)
- `tests/`: automated tests mirroring `src/`
- `scripts/`: one-off utilities (CLI entry points, data backfills)
- `docs/`: design notes and usage examples
- `assets/`: fixtures, sample emails, or test corpora (no secrets)

## Build, Test, and Development Commands

Keep common tasks discoverable via `Makefile` and `.NET` commands:

- `make lint`: run formatting + linting
- `make test`: run the test suite locally
- `make run`: run the local app/CLI (document required env vars)

Example target naming: `make fmt`, `make typecheck`, `make ci`.

## Coding Style & Naming Conventions

- Indentation: 2 spaces for YAML/JSON/Markdown; 4 spaces for C#.
- Filenames: `PascalCase.cs` for C# types, `kebab-case` for scripts.
- Keep I/O at the edges: parsing/validation in pure functions; network/filesystem behind small adapters.
- Prefer typed C# models over loosely-typed dictionaries when schema is known.
- Prefer built-in .NET libraries before adding dependencies.

## Engineering Principles

- KISS: choose the simplest solution that satisfies the requirement.
- DRY: centralize shared parsing/utility logic; avoid copy-paste helpers.
- YAGNI: implement only what current commands/workflows require.
- .NET-first: do not add Python conversions or parallel Python implementations unless explicitly requested.

## Testing Guidelines

- Tests live in `tests/` and are deterministic (no real network calls; use fixtures in `assets/`).
- Naming: follow the selected .NET test framework conventions.
- Add regression tests for bugs before fixing them.

## Commit & Pull Request Guidelines

No commit history is available yet; use Conventional Commits:

- `feat: ...`, `fix: ...`, `chore: ...`, `docs: ...`, `test: ...`

PRs should include:

- What/why summary, how to test locally (exact commands)
- Linked issue/task (if applicable)
- Notes on config/env vars and any migrations

Required workflow for new tasks:

- Start every new task in a new git worktree (branch from `main`).
- Before each iteration, pull latest `main` and merge it into the local working branch.
- Before marking work complete, rerun relevant checks (`make test`, `dotnet test`, lint/build as applicable).
- When the task is fully complete and checks pass, open a PR targeting `main`.

## Security & Configuration Tips

- Store secrets in `.env` (gitignored). Never commit API keys or email corpora with PII.
- Add `.env.example` showing required variables and safe defaults.

## Code Check Prompt

Review this plan thoroughly before making any code changes.
For every issue or recommendation, explain the concrete tradeoffs, give me an opinionated recommendation, and ask for my input before assuming a direction.
My engineering preferences (use these to guide your recommendations):

- DRY is important-flag repetition aggressively.
- Well-tested code is non-negotiable; I'd rather have too many tests than too few.
- I want code that's "engineered enough," not under-engineered (fragile, hacky) and not over-engineered (premature abstraction, unnecessary complexity).
- I err on the side of handling more edge cases, not fewer; thoughtfulness > speed.
- Bias toward explicit over clever.

1. Architecture review
Evaluate:

- Overall system design and component boundaries.
- Dependency graph and coupling concerns.
- Data flow patterns and potential bottlenecks.
- Scaling characteristics and single points of failure.
- Security architecture (auth, access, API boundaries).

1. Code quality review
Evaluate:

- Code organization and module structure.
- DRY violations-be aggressive here.
- Error handling patterns and missing edge cases (call these out explicitly).
- Technical debt hotspots.
- Areas that are over-engineered or under-engineered relative to my preferences.

1. Test review
Evaluate:

- Test coverage gaps (unit, integration, end-to-end).
- Test quality and assertion strength.
- Missing edge case coverage-be thorough.
- Untested failure modes and error paths.

1. Performance review
Evaluate:

- N+1 queries and database access patterns.
- Memory-usage concerns.
- Caching opportunities.
- Slow or high-complexity code paths.

For each issue you find:

- Describe the problem concretely, with file and line references.
- Present 2-3 options, including "do nothing" where that's reasonable.
- For each option, specify: implementation effort, risk, impact on other code, and maintenance burden.
- Clearly note your recommended option and why, mapped to my preferences above.
- Only then explicitly ask whether I agree or want to choose a different direction before proceeding.

Workflow and interaction

- Assume we are iterating live on a codebase.
- After every section, pause and ask for my feedback before moving on.

BEFORE YOU START:
Ask if I want one of two options:

1. Full review mode: We go through this interactively, one section at a time (Architecture -> Code Quality -> Tests -> Performance) with at most 4 top issues in each section.
2. Small change: Work through interactively ONE question per review section.

FOR EACH STAGE OF REVIEW: output the explanation and pros and cons of each stage's questions AND your opinionated recommendation and why, and then use AskUserQuestion. Ask NUMBERED questions for options and when using AskUserQuestion make sure each option clearly labels the issue NUMBER and option LETTER so the user doesn't get confused. Make the recommended option always the 1st option.
