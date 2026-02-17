using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Collections.Concurrent;
using ValueInvestorCrawler.Lib;

namespace ValueInvestorCrawler.Lib.Agent;

internal enum RoutedIntent
{
    Run,
    Status,
    Analyze,
    Task,
    Chat
}

public sealed class AgentOrchestrator
{
    private readonly AgentConfig _config;
    private readonly ITelegramClient _telegram;
    private readonly IOpenAiClient _openAi;
    private readonly JobManager _jobManager;
    private readonly ConcurrentDictionary<long, string> _activeTasks = new();
    private long _taskRunCounter;

    public AgentOrchestrator(
        AgentConfig config,
        ITelegramClient telegram,
        IOpenAiClient openAi,
        JobManager? jobManager = null)
    {
        _config = config;
        _telegram = telegram;
        _openAi = openAi;
        _jobManager = jobManager ?? new JobManager(
            JobRegistry.All(),
            JobRegistry.Pipelines(),
            (text, ct) => TrySendAsync(_config.TelegramChatId, text, ct));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var state = AgentState.Load(_config.AgentStatePath);
        var nextCycleAt = DateTime.UtcNow; // first cycle immediately

        Console.Error.WriteLine($"[agent] Starting. First cycle at: {nextCycleAt:O}");

        while (!ct.IsCancellationRequested)
        {
            var secondsUntilCycle = (int)Math.Ceiling((nextCycleAt - DateTime.UtcNow).TotalSeconds);
            var pollTimeout = Math.Max(1, Math.Min(30, secondsUntilCycle));

            List<TelegramMessage> messages = [];
            try
            {
                messages = await _telegram.PollUpdatesAsync(pollTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[agent] Poll error: {ex.Message}"); }

            foreach (var msg in messages)
                await HandleMessageAsync(msg, state, ct);

            if (DateTime.UtcNow >= nextCycleAt && !ct.IsCancellationRequested)
            {
                await RunPipelineCycleAsync(state, ct);
                nextCycleAt = DateTime.UtcNow + TimeSpan.FromMinutes(_config.AgentHeartbeatMinutes);
                Console.Error.WriteLine($"[agent] Next cycle at: {nextCycleAt:O}");
            }
        }

        Console.Error.WriteLine("[agent] Stopped.");
    }

    // ── Message routing ────────────────────────────────────────────────────────

    private async Task HandleMessageAsync(TelegramMessage msg, AgentState state, CancellationToken ct)
    {
        var text = (msg.Text ?? "").Trim();
        if (text.Length == 0) return;
        Console.Error.WriteLine($"[agent] Received message #{msg.UpdateId}: {text}");

        if (!text.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
            await TrySendAsync(msg.ChatId, "Received. Processing...", ct);

        if (!text.StartsWith("/", StringComparison.Ordinal))
        {
            var (intent, arg) = await RouteIntentAsync(text, ct);
            switch (intent)
            {
                case RoutedIntent.Run:
                    await HandleRunAsync(msg.ChatId, arg.Length == 0 ? "/run" : $"/run {arg}", ct);
                    return;
                case RoutedIntent.Status:
                    await HandleStatusAsync(msg.ChatId, state, ct);
                    return;
                case RoutedIntent.Analyze:
                    await HandleAnalyzeAsync(msg.ChatId, arg.Length == 0 ? "/analyze" : $"/analyze {arg}", state, ct);
                    return;
                case RoutedIntent.Task:
                    await HandleTaskAsync(msg.ChatId, arg.Length == 0 ? "/task help" : $"/task {arg}", ct);
                    return;
                case RoutedIntent.Chat:
                default:
                    await HandleFreeFormAsync(msg.ChatId, text, state, ct);
                    return;
            }
        }

        if (text.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase))
        {
            await HandleJobsAsync(msg.ChatId, ct);
        }
        else if (text.StartsWith("/run", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("run pipeline", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRunAsync(msg.ChatId, text, ct);
        }
        else if (text.StartsWith("/stop", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStopAsync(msg.ChatId, text, ct);
        }
        else if (text.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStatusAsync(msg.ChatId, state, ct);
        }
        else if (text.StartsWith("/analyze", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAnalyzeAsync(msg.ChatId, text, state, ct);
        }
        else if (text.StartsWith("/task", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTaskAsync(msg.ChatId, text, ct);
        }
        else
        {
            await HandleFreeFormAsync(msg.ChatId, text, state, ct);
        }
    }

    private async Task<(RoutedIntent Intent, string Argument)> RouteIntentAsync(string text, CancellationToken ct)
    {
        var system = """
You route Telegram user messages to one intent.
Return exactly one line:
INTENT=<run|status|analyze|task|chat>;ARG=<text>
Rules:
- status: asking status/health/what is running
- run: asking to run/start pipeline/job
- analyze: explicit ticker/company analysis request
- task: asking to list/start background tasks/jobs/pipelines
- chat: everything else
For status/chat use empty ARG.
For analyze ARG should be ticker if clear (e.g. AAPL), otherwise empty.
For run/task keep ARG concise.
""";
        var user = $"Message: {text}";

        try
        {
            var completion = await _openAi.ChatAsync(
                [new ChatMessage("system", system), new ChatMessage("user", user)],
                ct);

            var raw = (completion.Content ?? "").Trim();
            if (raw.Length == 0) return (RoutedIntent.Chat, "");

            var intent = RoutedIntent.Chat;
            var argument = "";
            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim().ToLowerInvariant();
                var value = part[(eq + 1)..].Trim();
                if (key == "intent")
                    intent = value.ToLowerInvariant() switch
                    {
                        "run" => RoutedIntent.Run,
                        "status" => RoutedIntent.Status,
                        "analyze" => RoutedIntent.Analyze,
                        "task" => RoutedIntent.Task,
                        _ => RoutedIntent.Chat
                    };
                else if (key == "arg")
                    argument = value;
            }

            return (intent, argument);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] Intent routing fallback: {ex.Message}");
            return (RoutedIntent.Chat, "");
        }
    }

    private async Task HandleJobsAsync(string chatId, CancellationToken ct)
    {
        try
        {
            var jobs = _jobManager.AvailableJobNames;
            var pipelines = _jobManager.AvailablePipelineNames;
            var running = _jobManager.GetRunningJobs();

            var lines = new List<string>
            {
                "Jobs",
                $"Available jobs ({jobs.Count}): {string.Join(", ", jobs)}",
                $"Available pipelines ({pipelines.Count}): {string.Join(", ", pipelines)}"
            };

            if (running.Count == 0)
            {
                lines.Add("Running: none");
            }
            else
            {
                lines.Add("Running:");
                lines.AddRange(running.Select(r => $"- {r.Name} ({r.Status})"));
            }

            await _telegram.SendMessageAsync(chatId, string.Join("\n", lines), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /jobs error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleRunAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var command = text.Trim();
            if (!command.StartsWith("/run", StringComparison.OrdinalIgnoreCase))
            {
                var startedNatural = _jobManager.TryStartPipeline("default", ct);
                await _telegram.SendMessageAsync(
                    chatId,
                    startedNatural ? "Started pipeline: default" : "Could not start pipeline: default (already running?)",
                    ct);
                return;
            }
            if (command.Equals("/run", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("run pipeline", StringComparison.OrdinalIgnoreCase))
            {
                var started = _jobManager.TryStartPipeline("default", ct);
                await _telegram.SendMessageAsync(
                    chatId,
                    started ? "Started pipeline: default" : "Could not start pipeline: default (already running?)",
                    ct);
                return;
            }

            var remainder = command["/run".Length..].Trim();
            if (remainder.StartsWith("pipeline", StringComparison.OrdinalIgnoreCase))
            {
                var pipelineName = remainder["pipeline".Length..].Trim();
                if (pipelineName.Length == 0) pipelineName = "default";

                var started = _jobManager.TryStartPipeline(pipelineName, ct);
                await _telegram.SendMessageAsync(
                    chatId,
                    started
                        ? $"Started pipeline: {pipelineName}"
                        : $"Could not start pipeline: {pipelineName} (unknown or already running)",
                    ct);
                return;
            }

            if (remainder.Length == 0)
            {
                await _telegram.SendMessageAsync(
                    chatId,
                    "Usage: /run | /run <job-name> | /run pipeline [name]",
                    ct);
                return;
            }

            var startedJob = _jobManager.TryStartJob(remainder, ct);
            await _telegram.SendMessageAsync(
                chatId,
                startedJob
                    ? $"Started job: {remainder}"
                    : $"Could not start job: {remainder} (unknown or already running)",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /run error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleStopAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var target = text.Length > "/stop".Length
                ? text["/stop".Length..].Trim()
                : "";

            if (target.Length == 0)
            {
                await _telegram.SendMessageAsync(chatId, "Usage: /stop <job-name|pipeline:name>", ct);
                return;
            }

            var cancelled = _jobManager.TryCancel(target);
            await _telegram.SendMessageAsync(
                chatId,
                cancelled ? $"Cancellation requested: {target}" : $"Not running: {target}",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /stop error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleStatusAsync(string chatId, AgentState state, CancellationToken ct)
    {
        try
        {
            var lastCycle = state.LastCycleUtc == default
                ? "never"
                : state.LastCycleUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            var topTickers = BuildTopTickersSummary(_config.ImportantTickersPath, top: 5);

            var lastAnalysisPreview = state.LastAnalysis.Length > 0
                ? state.LastAnalysis[..Math.Min(200, state.LastAnalysis.Length)] + "..."
                : "(none yet)";

            var reply = string.Join("\n",
                "Status",
                $"Last cycle: {lastCycle}",
                $"Cycle count: {state.CycleCount}",
                $"Conversation turns: {state.ConversationHistory.Count}",
                $"Top tickers: {topTickers}",
                $"Last analysis: {lastAnalysisPreview}",
                BuildRunningJobsSummary());

            await _telegram.SendMessageAsync(chatId, reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /status error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleAnalyzeAsync(string chatId, string text, AgentState state, CancellationToken ct)
    {
        try
        {
            var ticker = text.Length > "/analyze".Length
                ? text["/analyze".Length..].Trim().ToUpperInvariant()
                : "";

            if (ticker.Length == 0)
            {
                await _telegram.SendMessageAsync(chatId, "Usage: /analyze TICKER (e.g. /analyze AAPL)", ct);
                return;
            }

            await _telegram.SendTypingAsync(chatId, ct);

            var systemPrompt = ContextBuilder.Build(_config);
            var userPrompt =
                $"Provide a thorough, actionable analysis of {ticker} based on all available data. " +
                "Include: recent investor activity, key financial metrics, valuation, risks, and a clear recommendation.";

            var completion = await _openAi.ChatAsync(
                [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)], ct);

            state.AddMessage(new ChatMessage("user", userPrompt), _config.AgentMaxConversationTurns);
            state.AddMessage(new ChatMessage("assistant", completion.Content), _config.AgentMaxConversationTurns);
            state.Save(_config.AgentStatePath);

            await _telegram.SendMessageAsync(chatId, completion.Content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /analyze error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleFreeFormAsync(string chatId, string text, AgentState state, CancellationToken ct)
    {
        try
        {
            await _telegram.SendTypingAsync(chatId, ct);

            var systemPrompt = ContextBuilder.Build(_config);
            var messages = new List<ChatMessage>(capacity: 1 + state.ConversationHistory.Count + 1)
            {
                new("system", systemPrompt)
            };
            messages.AddRange(state.ConversationHistory);
            messages.Add(new ChatMessage("user", text));

            var completion = await _openAi.ChatAsync(messages, ct);

            state.AddMessage(new ChatMessage("user", text), _config.AgentMaxConversationTurns);
            state.AddMessage(new ChatMessage("assistant", completion.Content), _config.AgentMaxConversationTurns);
            state.Save(_config.AgentStatePath);

            await _telegram.SendMessageAsync(chatId, completion.Content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] Chat error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleTaskAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var args = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (args.Length == 1 || string.Equals(args[1], "help", StringComparison.OrdinalIgnoreCase))
            {
                await _telegram.SendMessageAsync(chatId, BuildTaskHelp(), ct);
                return;
            }

            if (string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
            {
                await _telegram.SendMessageAsync(chatId, BuildTaskList(), ct);
                return;
            }

            if (args.Length >= 3 && string.Equals(args[1], "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                var pipelineName = args[2];
                StartBackgroundPipeline(chatId, pipelineName, ct);
                return;
            }

            var jobName = args.Length >= 3 && string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase)
                ? args[2]
                : args[1];
            StartBackgroundJob(chatId, jobName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /task error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private string BuildTaskHelp()
    {
        return string.Join(
            "\n",
            "Task commands",
            "/task list",
            "/task <job>",
            "/task run <job>",
            "/task pipeline <name>",
            "Use /task list to see available jobs and pipelines.");
    }

    private string BuildTaskList()
    {
        var jobs = JobRegistry.All().Values.OrderBy(j => j.Name).ToList();
        var pipelines = JobRegistry.Pipelines().OrderBy(p => p.Key).ToList();
        var active = _activeTasks.OrderBy(p => p.Key).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Available jobs:");
        foreach (var job in jobs)
            sb.AppendLine($"- {job.Name}: {job.Description}");

        sb.AppendLine();
        sb.AppendLine("Available pipelines:");
        foreach (var pipeline in pipelines)
            sb.AppendLine($"- {pipeline.Key}: {string.Join(" -> ", pipeline.Value)}");

        sb.AppendLine();
        sb.AppendLine(active.Count == 0 ? "Active tasks: none" : "Active tasks:");
        foreach (var entry in active)
            sb.AppendLine($"- #{entry.Key}: {entry.Value}");

        return sb.ToString().TrimEnd();
    }

    private void StartBackgroundJob(string chatId, string jobName, CancellationToken ct)
    {
        var jobs = JobRegistry.All();
        if (!jobs.TryGetValue(jobName, out var job))
        {
            _ = TrySendAsync(chatId, $"Unknown job '{jobName}'. Use /task list.", ct);
            return;
        }

        StartBackgroundTask(
            chatId,
            $"job {job.Name}",
            ct,
            async token =>
            {
                var exitCode = await job.Execute(token);
                if (exitCode != 0)
                    throw new Exception($"Job '{job.Name}' failed with exit code {exitCode}.");
            });
    }

    private void StartBackgroundPipeline(string chatId, string pipelineName, CancellationToken ct)
    {
        var pipelines = JobRegistry.Pipelines();
        if (!pipelines.TryGetValue(pipelineName, out var stages))
        {
            _ = TrySendAsync(chatId, $"Unknown pipeline '{pipelineName}'. Use /task list.", ct);
            return;
        }

        var jobs = JobRegistry.All();
        StartBackgroundTask(
            chatId,
            $"pipeline {pipelineName}",
            ct,
            async token =>
            {
                foreach (var stage in stages)
                {
                    if (!jobs.TryGetValue(stage, out var job))
                        throw new Exception($"Pipeline '{pipelineName}' references unknown job '{stage}'.");

                    await TrySendAsync(chatId, $"Pipeline {pipelineName}: running {stage}...", token);
                    var exitCode = await job.Execute(token);
                    if (exitCode != 0)
                        throw new Exception($"Pipeline '{pipelineName}' failed at '{stage}' with exit code {exitCode}.");
                }
            });
    }

    private void StartBackgroundTask(
        string chatId,
        string taskName,
        CancellationToken shutdownToken,
        Func<CancellationToken, Task> run)
    {
        var taskId = Interlocked.Increment(ref _taskRunCounter);
        _activeTasks[taskId] = taskName;

        _ = Task.Run(async () =>
        {
            await TrySendAsync(chatId, $"Started {taskName} (#{taskId}) in background.", shutdownToken);
            try
            {
                await run(shutdownToken);
                await TrySendAsync(chatId, $"{taskName} (#{taskId}) completed.", shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                await TrySendAsync(chatId, $"{taskName} (#{taskId}) cancelled.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[agent] Background task {taskName} (#{taskId}) error: {ex.Message}");
                await TrySendAsync(chatId, $"{taskName} (#{taskId}) failed: {ex.Message}", CancellationToken.None);
            }
            finally
            {
                _activeTasks.TryRemove(taskId, out _);
            }
        }, CancellationToken.None);
    }

    // ── Pipeline cycle ─────────────────────────────────────────────────────────

    private async Task RunPipelineCycleAsync(AgentState state, CancellationToken ct)
    {
        Console.Error.WriteLine("[agent] Pipeline cycle starting.");

        try { await _telegram.SendTypingAsync(_config.TelegramChatId, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] Typing error (non-fatal): {ex.Message}"); }

        var pipelineOk = await _jobManager.StartPipelineAndAwaitAsync("default", ct);
        Console.Error.WriteLine($"[agent] Default pipeline finished. success={pipelineOk}");

        var systemPrompt = "You are a proactive investment research assistant.";
        try { systemPrompt = ContextBuilder.Build(_config); }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] ContextBuilder error: {ex.Message}"); }

        var newDataromaDocs = LoadUnseenDocs(_config.DataromaContextPath, state.SeenDataromaIds, maxDocs: 10);
        var newVicDocs = LoadUnseenDocs(_config.VicContextPath, state.SeenVicIds, maxDocs: 10);
        var hasNewDocs = newDataromaDocs.Count > 0 || newVicDocs.Count > 0;
        var isCycleAnalysisDue = IsCycleAnalysisDue(state, _config.AgentMinMinutesBetweenCycleAnalysis);

        var analysis = state.LastAnalysis;
        var analysisSucceeded = false;
        if (hasNewDocs || isCycleAnalysisDue)
        {
            var analysisRequest = BuildAnalysisRequest(newDataromaDocs, newVicDocs);
            state.MarkOpenAiCycleAttempt();
            try
            {
                var completion = await _openAi.ChatAsync(
                    [new ChatMessage("system", systemPrompt), new ChatMessage("user", analysisRequest)], ct);
                analysis = completion.Content;
                analysisSucceeded = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[agent] OpenAI error: {ex.Message}");
                analysis = $"Analysis unavailable: {ex.Message}";
            }

            await TrySendAsync(_config.TelegramChatId, analysis, ct);
        }
        else
        {
            Console.Error.WriteLine(
                $"[agent] Skipping OpenAI analysis (no new docs; cooldown {_config.AgentMinMinutesBetweenCycleAnalysis}m).");
        }

        try
        {
            if (analysisSucceeded)
            {
                MarkSeen(state.SeenDataromaIds, newDataromaDocs);
                MarkSeen(state.SeenVicIds, newVicDocs);
            }
            state.MarkCycleComplete(analysis);
            state.Save(_config.AgentStatePath);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] State save error: {ex.Message}"); }

        Console.Error.WriteLine($"[agent] Cycle #{state.CycleCount} complete.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task TrySendAsync(string chatId, string text, CancellationToken ct)
    {
        try { await _telegram.SendMessageAsync(chatId, text, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] TrySend error: {ex.Message}"); }
    }

    private string BuildRunningJobsSummary()
    {
        var running = _jobManager.GetRunningJobs();
        if (running.Count == 0)
            return "Running jobs: none";

        return "Running jobs: " + string.Join(", ", running.Select(r => $"{r.Name} ({r.Status})"));
    }

    private static string BuildTopTickersSummary(string path, int top)
    {
        if (top <= 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "(none)";

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "(none)";

            var tickers = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ticker", out var t) || t.ValueKind != JsonValueKind.String)
                    continue;

                var ticker = (t.GetString() ?? "").Trim();
                if (ticker.Length == 0) continue;

                tickers.Add(ticker);
                if (tickers.Count >= top) break;
            }

            return tickers.Count == 0 ? "(none)" : string.Join(", ", tickers);
        }
        catch
        {
            return "(none)";
        }
    }

    private static List<ContextDoc> LoadUnseenDocs(string path, HashSet<string> seenIds, int maxDocs)
    {
        if (maxDocs <= 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];

        try
        {
            var docs = ContextDocs.Load(path);
            var unseen = new List<ContextDoc>(capacity: Math.Min(maxDocs, docs.Count));
            foreach (var doc in docs)
            {
                if (string.IsNullOrWhiteSpace(doc.DocId)) continue;
                if (seenIds.Contains(doc.DocId)) continue;

                unseen.Add(doc);
                if (unseen.Count >= maxDocs) break;
            }
            return unseen;
        }
        catch
        {
            return [];
        }
    }

    private static void MarkSeen(HashSet<string> seenIds, IEnumerable<ContextDoc> docs)
    {
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc.DocId)) continue;
            seenIds.Add(doc.DocId);
        }
    }

    private static string BuildAnalysisRequest(List<ContextDoc> newDataromaDocs, List<ContextDoc> newVicDocs)
    {
        var sb = new StringBuilder();
        sb.Append(
            "Analyze the latest data and surface the top 3-5 most actionable investment insights. " +
            "Focus on recent changes and avoid repeating previously reported points.");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"New Dataroma docs this cycle: {newDataromaDocs.Count}");
        sb.AppendLine($"New VIC docs this cycle: {newVicDocs.Count}");

        if (newDataromaDocs.Count == 0 && newVicDocs.Count == 0)
        {
            sb.AppendLine("No newly-seen docs were detected. Provide only genuinely new or changed conclusions.");
            return sb.ToString();
        }

        if (newDataromaDocs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== NEW DATAROMA DOCS ===");
            AppendDocsForPrompt(sb, newDataromaDocs);
        }

        if (newVicDocs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== NEW VIC DOCS ===");
            AppendDocsForPrompt(sb, newVicDocs);
        }

        return sb.ToString();
    }

    private static void AppendDocsForPrompt(StringBuilder sb, List<ContextDoc> docs)
    {
        foreach (var doc in docs)
        {
            sb.AppendLine($"[{doc.DocId}]");
            foreach (var (k, v) in doc.Headers)
                sb.AppendLine($"{k}: {v}");
            if (!string.IsNullOrWhiteSpace(doc.Body))
                sb.AppendLine(doc.Body.Trim());
            sb.AppendLine();
        }
    }

    private static bool IsCycleAnalysisDue(AgentState state, int minMinutesBetweenAnalysis)
    {
        if (minMinutesBetweenAnalysis <= 0)
            return true;

        if (state.LastOpenAiCycleUtc == DateTime.MinValue)
            return true;

        var elapsed = DateTime.UtcNow - state.LastOpenAiCycleUtc;
        return elapsed >= TimeSpan.FromMinutes(minMinutesBetweenAnalysis);
    }
}
