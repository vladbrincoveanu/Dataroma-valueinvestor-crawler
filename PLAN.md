# Plan: Background Job System for Agent Loop

## Context

The agent-loop currently runs 3 hardcoded pipeline stages **synchronously**, blocking Telegram polling while the pipeline runs. VIC and Foxland commands are not integrated into the agent at all — they must be run manually from the CLI. The goal is to:

- Make ALL 6 commands available as background jobs triggered from Telegram
- Keep the bot responsive while any job runs (non-blocking)
- Support composing jobs into named pipelines (`default`, `full`)
- Let the user trigger, monitor, and cancel jobs via Telegram messages

## New Files

### 1. `src/EmailExtractor/Lib/Agent/JobRegistry.cs`

Pure data — no side effects, no deps other than command namespaces.

```
JobDefinition record:
  Name        string
  Description string
  Execute     Func<CancellationToken, Task<int>>

JobRegistry.All() → Dictionary<string, JobDefinition>
  "foxland-format"   → FoxlandFormatForLlm.Run([])
  "dataroma-rss"     → DataromaRssExport.Run([])
  "extract-tickers"  → ExtractImportantTickers.Run([])
  "fetch-overview"   → FetchFinancialOverview.Run([])
  "vic-collect-links"→ VicCollectLinks.Run([])
  "vic-crawl"        → VicCrawlIdeas.Run([])

JobRegistry.Pipelines() → Dictionary<string, string[]>
  "default" → ["dataroma-rss", "extract-tickers", "fetch-overview"]
  "full"    → ["foxland-format", "dataroma-rss", "vic-collect-links",
                "vic-crawl", "extract-tickers", "fetch-overview"]
```

### 2. `src/EmailExtractor/Lib/Agent/JobManager.cs`

Runs jobs on background Tasks, tracks state, fires Telegram notifications.

```
JobStatus enum: Pending | Running | Completed | Failed | Cancelled

RunningJob class:
  Name, Status, StartedAt, CompletedAt, ExitCode, Error
  CancellationTokenSource Cts
  Task Task

JobManager class (ConcurrentDictionary<string, RunningJob>):
  constructor(registry, pipelines, Func<string, CancellationToken, Task> notify)

  TryStartJob(name, parentCt) → bool
    - Returns false if already running (prevents file conflicts)
    - Task.Run() with linked CTS
    - Notifies Telegram on start / complete / fail / cancel
    - Removes from dict on finish

  TryStartPipeline(name, parentCt) → bool
    - Keyed as "pipeline:<name>" in dict
    - Runs steps sequentially, stops on first failure
    - Reports each step start + final result

  StartPipelineAndAwaitAsync(name, ct) → Task
    - Awaitable version for the heartbeat cycle (needs result before analysis)

  TryCancel(name) → bool
  GetRunningJobs() → IReadOnlyList<RunningJob>
  AvailableJobNames, AvailablePipelineNames properties
```

Design: each job gets `CancellationTokenSource.CreateLinkedTokenSource(parentCt)` so global Ctrl+C cancels everything; individual `/stop` cancels just one job.

## Modified Files

### 3. `src/EmailExtractor/Lib/Agent/AgentOrchestrator.cs`

- Remove `Func<AgentConfig, CancellationToken, Task<PipelineResult>> _runPipeline` field
- Add `JobManager _jobManager` field, initialized in constructor from `JobRegistry`
- New Telegram commands:

| Command | Action |
|---------|--------|
| `/jobs` | List all available jobs + pipelines + currently running |
| `/run` | Run "default" pipeline (backward compat) |
| `/run <job-name>` | Start single job in background |
| `/run pipeline [name]` | Run named pipeline in background |
| `/stop <name>` | Cancel a running job or pipeline |
| `/status` | Existing status + running jobs appended |

- Heartbeat cycle: `await _jobManager.StartPipelineAndAwaitAsync("default", ct)` then OpenAI analysis as before (blocking the heartbeat timer but not message handling)

### 4. `src/EmailExtractor/Commands/AgentLoop.cs`

- Remove pipeline delegate from AgentOrchestrator constructor call (no longer needed)

### 5. `src/EmailExtractor/Lib/Agent/PipelineRunner.cs` — DELETE

Becomes dead code. JobManager + JobRegistry replace it entirely.

## Implementation Order

1. `JobRegistry.cs` (pure data, implement first)
2. `JobManager.cs` (depends on JobRegistry types)
3. `AgentOrchestrator.cs` (rewire routing + heartbeat)
4. `AgentLoop.cs` (update constructor)
5. Delete `PipelineRunner.cs`
6. `make build` + `make test` — must pass clean

## Verification

1. `make build` — 0 errors, 0 warnings
2. `make test` — all pass
3. `make run CMD="agent-loop"` with env vars set:
   - Send `/jobs` → lists all 6 jobs + 2 pipelines
   - Send `/run dataroma-rss` → starts in background, bot stays responsive, completion reported to Telegram
   - Send `/run pipeline full` → runs all 6 steps sequentially, each step reported
   - Send `/stop pipeline:full` mid-run → cancels cleanly
   - Wait for heartbeat → default pipeline + analysis still fires on schedule
4. `gh pr create --base main` → open PR
