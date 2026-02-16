using System.Globalization;
using System.Text;
using System.Text.Json;
using EmailExtractor.Lib;

namespace EmailExtractor.Lib.Agent;

public sealed class AgentOrchestrator
{
    private readonly AgentConfig _config;
    private readonly ITelegramClient _telegram;
    private readonly IOpenAiClient _openAi;
    private readonly JobManager _jobManager;

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
            (msg, ct) => TrySendAsync(_config.TelegramChatId, msg, ct));
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

        if (text.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase))
            await HandleJobsAsync(msg.ChatId, ct);
        else if (text.StartsWith("/stop", StringComparison.OrdinalIgnoreCase))
            await HandleStopAsync(msg.ChatId, text, ct);
        else if (text.StartsWith("/run", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("run pipeline", StringComparison.OrdinalIgnoreCase))
            await HandleRunAsync(msg.ChatId, text, ct);
        else if (text.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
            await HandleStatusAsync(msg.ChatId, state, ct);
        else if (text.StartsWith("/analyze", StringComparison.OrdinalIgnoreCase))
            await HandleAnalyzeAsync(msg.ChatId, text, state, ct);
        else
            await HandleFreeFormAsync(msg.ChatId, text, state, ct);
    }

    private async Task HandleRunAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            // strip leading /run or "run pipeline"
            var arg = "";
            if (text.StartsWith("/run", StringComparison.OrdinalIgnoreCase))
                arg = text.Length > "/run".Length ? text["/run".Length..].Trim() : "";
            else if (text.Contains("run pipeline", StringComparison.OrdinalIgnoreCase))
                arg = "pipeline";

            // /run pipeline [name]
            if (arg.StartsWith("pipeline", StringComparison.OrdinalIgnoreCase))
            {
                var pipelineName = arg.Length > "pipeline".Length
                    ? arg["pipeline".Length..].Trim()
                    : "default";
                if (pipelineName.Length == 0) pipelineName = "default";

                if (!_jobManager.IsKnownPipeline(pipelineName))
                {
                    await TrySendAsync(chatId,
                        $"Unknown pipeline '{pipelineName}'. Available: {string.Join(", ", _jobManager.AvailablePipelineNames)}", ct);
                    return;
                }
                var started = _jobManager.TryStartPipeline(pipelineName, ct);
                await TrySendAsync(chatId, started
                    ? $"Pipeline '{pipelineName}' started in background."
                    : $"Pipeline '{pipelineName}' is already running.", ct);
                return;
            }

            // /run <job-name>
            if (arg.Length > 0)
            {
                if (!_jobManager.IsKnownJob(arg))
                {
                    await TrySendAsync(chatId,
                        $"Unknown job '{arg}'. Use /jobs to see available jobs.", ct);
                    return;
                }
                var started = _jobManager.TryStartJob(arg, ct);
                await TrySendAsync(chatId, started
                    ? $"Job '{arg}' started in background."
                    : $"Job '{arg}' is already running.", ct);
                return;
            }

            // /run (no args) → default pipeline in background
            var defaultStarted = _jobManager.TryStartPipeline("default", ct);
            await TrySendAsync(chatId, defaultStarted
                ? "Default pipeline started in background."
                : "Default pipeline is already running.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /run error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleJobsAsync(string chatId, CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Available jobs:");
            foreach (var (name, description) in _jobManager.GetJobDescriptions())
                sb.AppendLine($"  /run {name} — {description}");

            sb.AppendLine();
            sb.AppendLine("Available pipelines:");
            foreach (var name in _jobManager.AvailablePipelineNames)
                sb.AppendLine($"  /run pipeline {name}");

            var running = _jobManager.GetRunningJobs();
            sb.AppendLine();
            if (running.Count > 0)
            {
                sb.AppendLine("Running:");
                foreach (var job in running)
                    sb.AppendLine($"  {job.Name} (since {job.StartedAt:HH:mm:ss} UTC)");
            }
            else
            {
                sb.AppendLine("No jobs currently running.");
            }

            await _telegram.SendMessageAsync(chatId, sb.ToString().TrimEnd(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /jobs error: {ex.Message}");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleStopAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var name = text.Length > "/stop".Length ? text["/stop".Length..].Trim() : "";
            if (name.Length == 0)
            {
                await TrySendAsync(chatId, "Usage: /stop <job-name> or /stop pipeline:<name>", ct);
                return;
            }
            var cancelled = _jobManager.TryCancel(name);
            await TrySendAsync(chatId, cancelled
                ? $"Cancelling '{name}'..."
                : $"No running job named '{name}'.", ct);
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

            var running = _jobManager.GetRunningJobs();
            var runningInfo = running.Count > 0
                ? string.Join(", ", running.Select(j => j.Name))
                : "none";

            var reply = string.Join("\n",
                "Status",
                $"Last cycle: {lastCycle}",
                $"Cycle count: {state.CycleCount}",
                $"Conversation turns: {state.ConversationHistory.Count}",
                $"Top tickers: {topTickers}",
                $"Running jobs: {runningInfo}",
                $"Last analysis: {lastAnalysisPreview}");

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

    // ── Pipeline cycle (heartbeat) ─────────────────────────────────────────────

    private async Task RunPipelineCycleAsync(AgentState state, CancellationToken ct)
    {
        Console.Error.WriteLine("[agent] Pipeline cycle starting.");

        try { await _telegram.SendTypingAsync(_config.TelegramChatId, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] Typing error (non-fatal): {ex.Message}"); }

        await TrySendAsync(_config.TelegramChatId, "Running pipeline...", ct);

        try
        {
            await _jobManager.StartPipelineAndAwaitAsync("default", ct);
            Console.Error.WriteLine("[agent] Pipeline done.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] Pipeline error: {ex.Message}");
            await TrySendAsync(_config.TelegramChatId, $"Pipeline error: {ex.Message}", ct);
        }

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
        catch { return "(none)"; }
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
        catch { return []; }
    }

    private static void MarkSeen(HashSet<string> seenIds, IEnumerable<ContextDoc> docs)
    {
        foreach (var doc in docs)
            if (!string.IsNullOrWhiteSpace(doc.DocId))
                seenIds.Add(doc.DocId);
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
        if (minMinutesBetweenAnalysis <= 0) return true;
        if (state.LastOpenAiCycleUtc == DateTime.MinValue) return true;
        return (DateTime.UtcNow - state.LastOpenAiCycleUtc) >= TimeSpan.FromMinutes(minMinutesBetweenAnalysis);
    }
}
