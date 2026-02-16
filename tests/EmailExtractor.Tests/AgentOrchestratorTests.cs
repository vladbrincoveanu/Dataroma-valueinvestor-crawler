using EmailExtractor.Lib.Agent;

namespace EmailExtractor.Tests;

public sealed class AgentOrchestratorTests
{
    [Fact]
    public async Task RunAsync_FullLoop_ProcessesMessage_RunsPipeline_AndPersistsState()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls:
            [
                [new TelegramMessage(1, "chat-1", "hello", DateTime.UtcNow)],
                []
            ],
            onPoll: p => { if (p == 2) cts.Cancel(); });
        var openAi = new FakeOpenAiClient(["free-form-reply", "cycle-analysis"]);
        var jobManager = CreateJobManager(telegram, ("dataroma-rss", 0), ("extract-tickers", 0), ("fetch-overview", 0));

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        var state = AgentState.Load(statePath);
        Assert.Equal(1, state.CycleCount);
        Assert.Equal("cycle-analysis", state.LastAnalysis);
        Assert.Equal(2, state.ConversationHistory.Count);
        Assert.Contains(telegram.SentMessages, m => m.Text == "free-form-reply");
        Assert.Contains(telegram.SentMessages, m => m.Text == "Completed pipeline: default");
        Assert.Contains(telegram.SentMessages, m => m.Text == "cycle-analysis");
    }

    [Fact]
    public async Task RunAsync_Restart_LoadsExistingState_AndContinuesFromSavedCycle()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var seeded = new AgentState();
        seeded.SeenDataromaIds.Add("seed-doc");
        seeded.MarkCycleComplete("seed-analysis");
        seeded.Save(statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls:
            [
                [],
                []
            ],
            onPoll: p => { if (p == 2) cts.Cancel(); });
        var openAi = new FakeOpenAiClient(["restart-cycle-analysis"]);
        var jobManager = CreateJobManager(telegram, ("dataroma-rss", 0), ("extract-tickers", 0), ("fetch-overview", 0));

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);

        await sut.RunAsync(cts.Token);

        var state = AgentState.Load(statePath);
        Assert.Equal(2, state.CycleCount);
        Assert.Equal("restart-cycle-analysis", state.LastAnalysis);
        Assert.Contains("seed-doc", state.SeenDataromaIds);
    }

    [Fact]
    public async Task RunAsync_JobsCommand_ListsAvailableJobsAndPipelines()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls:
            [
                [new TelegramMessage(1, "chat-1", "/jobs", DateTime.UtcNow)],
                []
            ],
            onPoll: p => { if (p == 2) cts.Cancel(); });
        var openAi = new FakeOpenAiClient(["cycle-analysis"]);
        var jobManager = CreateJobManager(
            telegram,
            ("dataroma-rss", 0),
            ("extract-tickers", 0),
            ("fetch-overview", 0),
            ("vic-crawl", 0),
            ("vic-collect-links", 0),
            ("foxland-format", 0));

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        var jobsReply = telegram.SentMessages.FirstOrDefault(m => m.Text.StartsWith("Jobs", StringComparison.Ordinal));
        Assert.NotEqual(default, jobsReply);
        Assert.Contains("Available jobs (6)", jobsReply.Text);
        Assert.Contains("Available pipelines (2)", jobsReply.Text);
    }

    [Fact]
    public async Task RunAsync_RunCommand_StartsBackgroundJobAndReportsCompletion()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls:
            [
                [new TelegramMessage(1, "chat-1", "/run dataroma-rss", DateTime.UtcNow)],
                [],
                []
            ],
            onPoll: p => { if (p == 3) cts.Cancel(); });
        var openAi = new FakeOpenAiClient(["cycle-analysis"]);
        var jobManager = CreateJobManager(
            telegram,
            ("dataroma-rss", 20),
            ("extract-tickers", 0),
            ("fetch-overview", 0));

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        Assert.Contains(telegram.SentMessages, m => m.Text == "Started job: dataroma-rss");
        Assert.Contains(telegram.SentMessages, m => m.Text == "Completed job: dataroma-rss");
    }

    private static AgentConfig CreateConfig(string outDir, string statePath)
    {
        return new AgentConfig(
            TelegramBotToken: "token",
            TelegramChatId: "chat-1",
            OpenAiApiKey: "key",
            OpenAiBaseUrl: "https://api.openai.com/v1",
            OpenAiModel: "gpt-test",
            OpenAiMaxTokens: 256,
            OpenAiTemperature: 0.1,
            AgentHeartbeatMinutes: 30,
            AgentMinMinutesBetweenCycleAnalysis: 180,
            AgentMaxContextChars: 10000,
            AgentMaxConversationTurns: 12,
            OutDir: outDir,
            ImportantTickersPath: Path.Combine(outDir, "important_tickers.json"),
            FinancialOverviewPath: Path.Combine(outDir, "financial_overview.jsonl"),
            DataromaContextPath: Path.Combine(outDir, "dataroma_context.txt"),
            VicContextPath: Path.Combine(outDir, "vic_context.txt"),
            FoxlandContextPath: Path.Combine(outDir, "foxland_context.txt"),
            AgentStatePath: statePath);
    }

    private static JobManager CreateJobManager(FakeTelegramClient telegram, params (string Name, int DelayMs)[] jobs)
    {
        var registry = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, delayMs) in jobs)
        {
            registry[name] = new JobDefinition(
                name,
                $"Test job {name}",
                async ct =>
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs, ct);
                    return 0;
                });
        }

        var pipelines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = ["dataroma-rss", "extract-tickers", "fetch-overview"],
            ["full"] = ["foxland-format", "dataroma-rss", "vic-collect-links", "vic-crawl", "extract-tickers", "fetch-overview"]
        };

        return new JobManager(
            registry,
            pipelines,
            (text, ct) => telegram.SendMessageAsync("chat-1", text, ct));
    }

    private sealed class FakeTelegramClient : ITelegramClient
    {
        private readonly List<List<TelegramMessage>> _polls;
        private readonly Action<int>? _onPoll;
        private int _pollCount;

        public List<(string ChatId, string Text)> SentMessages { get; } = [];

        public FakeTelegramClient(List<TelegramMessage>[] polls, Action<int>? onPoll = null)
        {
            _polls = polls.ToList();
            _onPoll = onPoll;
        }

        public Task<List<TelegramMessage>> PollUpdatesAsync(int timeoutSec, CancellationToken ct)
        {
            _ = timeoutSec;
            _pollCount++;

            _onPoll?.Invoke(_pollCount);

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var index = _pollCount - 1;
            if (index < 0 || index >= _polls.Count)
                return Task.FromResult(new List<TelegramMessage>());

            return Task.FromResult(_polls[index]);
        }

        public Task SendMessageAsync(string chatId, string text, CancellationToken ct)
        {
            _ = ct;
            SentMessages.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(string chatId, CancellationToken ct)
        {
            _ = chatId;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOpenAiClient : IOpenAiClient
    {
        private readonly Queue<string> _responses;

        public FakeOpenAiClient(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        public Task<ChatCompletion> ChatAsync(List<ChatMessage> messages, CancellationToken ct = default)
        {
            _ = messages;
            _ = ct;

            var content = _responses.Count > 0 ? _responses.Dequeue() : "default-analysis";
            return Task.FromResult(new ChatCompletion(content, 0, 0));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"email_extractor_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
