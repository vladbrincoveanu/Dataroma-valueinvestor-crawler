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
            onSecondPoll: () => cts.Cancel());
        var openAi = new FakeOpenAiClient(["free-form-reply", "cycle-analysis"]);

        var pipelineCalls = 0;
        var jobManager = CreateFakeJobManager(
            onDefaultPipeline: () => pipelineCalls++,
            notify: (_, _) => Task.CompletedTask);

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        var state = AgentState.Load(statePath);
        Assert.Equal(1, pipelineCalls);
        Assert.Equal(1, state.CycleCount);
        Assert.Equal("cycle-analysis", state.LastAnalysis);
        Assert.Equal(2, state.ConversationHistory.Count);
        Assert.Contains(telegram.SentMessages, m => m.Text == "free-form-reply");
        Assert.Contains(telegram.SentMessages, m => m.Text == "Running pipeline...");
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
            onSecondPoll: () => cts.Cancel());
        var openAi = new FakeOpenAiClient(["restart-cycle-analysis"]);

        var jobManager = CreateFakeJobManager(notify: (_, _) => Task.CompletedTask);
        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);

        await sut.RunAsync(cts.Token);

        var state = AgentState.Load(statePath);
        Assert.Equal(2, state.CycleCount);
        Assert.Equal("restart-cycle-analysis", state.LastAnalysis);
        Assert.Contains("seed-doc", state.SeenDataromaIds);
    }

    [Fact]
    public async Task RunAsync_RunCommand_StartsDefaultPipelineInBackground()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls: [[new TelegramMessage(1, "chat-1", "/run dataroma-rss", DateTime.UtcNow)], []],
            onSecondPoll: () => cts.Cancel());
        var openAi = new FakeOpenAiClient(["cycle-analysis"]);

        var jobStarted = false;
        var jobManager = CreateFakeJobManager(
            onJobRun: name => { if (name == "dataroma-rss") jobStarted = true; },
            notify: (_, _) => Task.CompletedTask);

        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        Assert.True(jobStarted);
        Assert.Contains(telegram.SentMessages, m => m.Text.Contains("started in background"));
    }

    [Fact]
    public async Task RunAsync_JobsCommand_ReturnsAvailableJobs()
    {
        using var temp = new TempDir();
        var statePath = Path.Combine(temp.Path, "agent_state.json");
        var config = CreateConfig(temp.Path, statePath);

        var cts = new CancellationTokenSource();
        var telegram = new FakeTelegramClient(
            polls: [[new TelegramMessage(1, "chat-1", "/jobs", DateTime.UtcNow)], []],
            onSecondPoll: () => cts.Cancel());
        var openAi = new FakeOpenAiClient(["cycle-analysis"]);

        var jobManager = CreateFakeJobManager(notify: (_, _) => Task.CompletedTask);
        var sut = new AgentOrchestrator(config, telegram, openAi, jobManager);
        await sut.RunAsync(cts.Token);

        Assert.Contains(telegram.SentMessages, m => m.Text.Contains("Available jobs:"));
        Assert.Contains(telegram.SentMessages, m => m.Text.Contains("dataroma-rss"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static JobManager CreateFakeJobManager(
        Func<string, CancellationToken, Task> notify,
        Action? onDefaultPipeline = null,
        Action<string>? onJobRun = null)
    {
        var registry = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["dataroma-rss"] = new("dataroma-rss", "Fetch Dataroma RSS", ct =>
            {
                onJobRun?.Invoke("dataroma-rss");
                onDefaultPipeline?.Invoke();
                return Task.FromResult(0);
            }),
            ["extract-tickers"] = new("extract-tickers", "Rank tickers", _ => Task.FromResult(0)),
            ["fetch-overview"]  = new("fetch-overview",  "Fetch overview", _ => Task.FromResult(0)),
            ["foxland-format"]  = new("foxland-format",  "Format foxland", _ => Task.FromResult(0)),
            ["vic-collect-links"] = new("vic-collect-links", "Collect VIC links", _ => Task.FromResult(0)),
            ["vic-crawl"]       = new("vic-crawl",       "Crawl VIC",      _ => Task.FromResult(0)),
        };
        var pipelines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = ["dataroma-rss", "extract-tickers", "fetch-overview"],
            ["full"]    = ["foxland-format", "dataroma-rss", "vic-collect-links", "vic-crawl", "extract-tickers", "fetch-overview"],
        };
        return new JobManager(registry, pipelines, notify);
    }

    private static AgentConfig CreateConfig(string outDir, string statePath)
    {
        return new AgentConfig(
            TelegramBotToken: "token",
            TelegramChatId: "chat-1",
            OpenAiApiKey: "key",
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

    private sealed class FakeTelegramClient : ITelegramClient
    {
        private readonly List<List<TelegramMessage>> _polls;
        private readonly Action? _onSecondPoll;
        private int _pollCount;

        public List<(string ChatId, string Text)> SentMessages { get; } = [];

        public FakeTelegramClient(List<TelegramMessage>[] polls, Action? onSecondPoll = null)
        {
            _polls = polls.ToList();
            _onSecondPoll = onSecondPoll;
        }

        public Task<List<TelegramMessage>> PollUpdatesAsync(int timeoutSec, CancellationToken ct)
        {
            _ = timeoutSec;
            _pollCount++;
            if (_pollCount == 2) _onSecondPoll?.Invoke();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var index = _pollCount - 1;
            return Task.FromResult(index < _polls.Count ? _polls[index] : []);
        }

        public Task SendMessageAsync(string chatId, string text, CancellationToken ct)
        {
            _ = ct;
            SentMessages.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(string chatId, CancellationToken ct)
        {
            _ = chatId; _ = ct;
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
            _ = messages; _ = ct;
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
