# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Tech Stack

- **.NET 10.0 / C#** — no external NuGet dependencies; uses only BCL (HttpClient, System.Text.Json, System.Xml.Linq, Regex)
- Target SDK: `net10.0` with nullable reference types and implicit usings enabled

## Commands

```bash
make build            # dotnet build EmailExtractor.sln
make test             # dotnet test EmailExtractor.sln
make run CMD="--help" # dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- <args>
make clean
```

There is no lint/format target yet. Use `dotnet format` if needed.

Env vars are loaded from `.env` at runtime (see `.env.example`). Copy it and fill in values before running commands that hit external APIs.

## Architecture

The app is a CLI financial data pipeline. `Program.cs` dispatches on the first positional argument to one of six static `Commands/*.Run()` methods. There are no frameworks — just switch expressions and raw BCL.

**Data flow (typical pipeline):**
```
foxland-format  →  foxland_context.txt   ┐
dataroma-rss    →  dataroma_moves.jsonl  ├─► extract-tickers → important_tickers.json → fetch-overview
vic-crawl       →  vic_context.txt       ┘
```

### Key library files (`src/EmailExtractor/Lib/`)

| File | Role |
|------|------|
| `Args.cs` | Parses `--key value` CLI flags into a typed accessor |
| `Env.cs` | Same API as `Args` but reads `Environment.GetEnvironmentVariable` |
| `TextUtil.cs` | HTML→text stripping, whitespace normalization, atomic file writes, text chunking |
| `ContextDocs.cs` | Reads/writes the custom `=== DOC {id} ===` / `---` / body format used by all context files |
| `Tickers.cs` | Multi-regex ticker extraction + weighted ranking (Dataroma recency weights, Foxland mention scores) |
| `Overview/SecEdgarClient.cs` | SEC EDGAR REST API — CIK lookup, company facts, margin calculations, 250 ms rate throttle, local cache |
| `Overview/StockAnalysisClient.cs` | stockanalysis.com HTML scraper — table + dl parsing, local cache |

### Context document format

All `*_context.txt` files use:
```
=== DOC {id} ===
key: value
---
body text
```
Parsed/written via `ContextDocs.cs`.

### Ticker ranking

`Tickers.cs` scores each ticker across two sources then merges:
- **Dataroma**: Bought +3, Added +2, Reduced −1, Sold −2; recency multipliers ×1.35/<7d, ×1.20/<30d, ×1.10/<90d
- **Foxland**: mention count + subject-line boost
- 116-entry Romanian/financial stopword list prevents false positives

### Web scraping conventions

VIC commands use a hand-rolled HTTP client (cookie jar, Brotli/Gzip decompression, User-Agent spoofing, configurable delay via `VIC_DELAY_MS`). No HTML parser library — all extraction is Regex against raw HTML. Keep this pattern consistent with existing code.

## Task workflow

For every non-trivial task:

1. **Create a git worktree** on a new branch before touching any code:
   ```bash
   git worktree add ../email_extractor-<branch-name> -b <branch-name>
   ```
   Work entirely inside that worktree directory.

2. **Sync with main** before starting any iteration:
   ```bash
   git fetch origin
   git merge origin/main
   ```
   Resolve any conflicts before proceeding.

3. **Implement** the task, following the coding conventions below.

4. **Review & clean up**: re-read all changed files, look for DRY violations, dead code, missing error handling, and refactor before committing.

5. **Verify**: run `make build` and `make test` — both must pass with 0 errors before proceeding.

6. **Open a PR** targeting `main`:
   ```bash
   git push -u origin <branch-name>
   gh pr create --base main --title "..." --body "..."
   ```
   Return the PR URL to the user.

7. **Clean up merged worktrees** after PRs are merged:
   ```bash
   git worktree remove <path>
   git branch -d <branch-name>
   ```

## Coding conventions

- All C# naming: `PascalCase` for types/methods/properties, `camelCase` for locals
- Each command is a static class with a single `public static async Task Run(Args args)` entry point; it owns its own argument/env-var reading and file I/O
- Use `TextUtil.WriteFileAtomic` for all file output (writes to `.tmp` then `File.Move`)
- Commits: Conventional Commits — `feat:`, `fix:`, `chore:`, `test:`, `docs:`

## Code Review Protocol (`/code-check`)

When invoked, follow this structured review process before making any code changes.

**BEFORE STARTING:** Ask whether the user wants:
1. **Full review mode** — interactive, one section at a time (Architecture → Code Quality → Tests → Performance), up to 4 top issues per section.
2. **Small change** — one question per review section only.

**Engineering preferences to apply:**
- Flag DRY violations aggressively
- Well-tested code is non-negotiable; more tests > fewer tests
- "Engineered enough" — avoid both fragile/hacky and over-abstracted/premature solutions
- Handle more edge cases, not fewer; thoughtfulness > speed
- Bias toward explicit over clever

**For each section, review the following and output explanations + pros/cons + opinionated recommendation, then use `AskUserQuestion` before proceeding:**

### 1. Architecture Review
- Overall system design and component boundaries
- Dependency graph and coupling concerns
- Data flow patterns and potential bottlenecks
- Scaling characteristics and single points of failure
- Security architecture (auth, access, API boundaries)

### 2. Code Quality Review
- Code organization and module structure
- DRY violations (be aggressive)
- Error handling patterns and missing edge cases (call out explicitly)
- Technical debt hotspots
- Over- or under-engineered areas

### 3. Test Review
- Test coverage gaps (unit, integration, end-to-end)
- Test quality and assertion strength
- Missing edge case coverage
- Untested failure modes and error paths

### 4. Performance Review
- N+1 queries and data access patterns
- Memory-usage concerns
- Caching opportunities
- Slow or high-complexity code paths

**For each issue found:**
- Describe the problem concretely with file and line references
- Present 2–3 options, including "do nothing" where reasonable
- For each option: implementation effort, risk, impact on other code, maintenance burden
- State your recommended option and why (mapped to preferences above)
- Number all questions; label options as `1A`, `1B`, `1C` etc. so the user can respond clearly
- Put the recommended option first
- Pause after each section and ask for feedback before moving on
