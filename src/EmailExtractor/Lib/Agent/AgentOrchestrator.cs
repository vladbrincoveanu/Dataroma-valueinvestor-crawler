using System.Globalization;
using System.Text.Json;
using System.Text;
using EmailExtractor.Lib;

namespace EmailExtractor.Lib.Agent;

public sealed class AgentOrchestrator
{
    private readonly AgentConfig _config;
    private readonly ITelegramClient _telegram;
    private readonly IOpenAiClient _openAi;
    private readonly Func<AgentConfig, CancellationToken, Task<PipelineResult>> _runPipeline;

    public AgentOrchestrator(
        AgentConfig config,
        ITelegramClient telegram,
        IOpenAiClient openAi,
        Func<AgentConfig, CancellationToken, Task<PipelineResult>>? runPipeline = null)
    {
        _config = config;
        _telegram = telegram;
        _openAi = openAi;
        _runPipeline = runPipeline ?? PipelineRunner.RunFullPipelineAsync;
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

        if (text.StartsWith("/run", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run pipeline", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRunAsync(msg.ChatId, state, ct);
        }
        else if (text.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStatusAsync(msg.ChatId, state, ct);
        }
        else if (text.StartsWith("/analyze", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAnalyzeAsync(msg.ChatId, text, state, ct);
        }
        else
        {
            await HandleFreeFormAsync(msg.ChatId, text, state, ct);
        }
    }

    private async Task HandleRunAsync(string chatId, AgentState state, CancellationToken ct)
    {
        try
        {
            await _telegram.SendMessageAsync(chatId, "Triggering pipeline cycle now...", ct);
            await RunPipelineCycleAsync(state, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] /run error: {ex.Message}");
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

    // ── Pipeline cycle ─────────────────────────────────────────────────────────

    private async Task RunPipelineCycleAsync(AgentState state, CancellationToken ct)
    {
        Console.Error.WriteLine("[agent] Pipeline cycle starting.");

        try { await _telegram.SendTypingAsync(_config.TelegramChatId, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"[agent] Typing error (non-fatal): {ex.Message}"); }

        await TrySendAsync(_config.TelegramChatId, "Running pipeline...", ct);

        try
        {
            var result = await _runPipeline(_config, ct);
            Console.Error.WriteLine($"[agent] Pipeline done. {result.Summary}");
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
