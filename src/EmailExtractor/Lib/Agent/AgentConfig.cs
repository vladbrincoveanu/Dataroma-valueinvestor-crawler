using System.Globalization;
using Microsoft.Extensions.Configuration;
using EmailExtractor;

namespace EmailExtractor.Lib.Agent;

public sealed record AgentConfig(
    string TelegramBotToken,
    string TelegramChatId,
    string OpenAiApiKey,
    string OpenAiBaseUrl,
    string OpenAiModel,
    int OpenAiMaxTokens,
    double OpenAiTemperature,
    int AgentHeartbeatMinutes,
    int AgentMinMinutesBetweenCycleAnalysis,
    int AgentMaxContextChars,
    int AgentMaxConversationTurns,
    string OutDir,
    string ImportantTickersPath,
    string FinancialOverviewPath,
    string DataromaContextPath,
    string VicContextPath,
    string FoxlandContextPath,
    string AgentStatePath
)
{
    public static AgentConfig FromEnv()
    {
        var cfg = BuildConfiguration();

        var outDir = ReadString(cfg, "Agent:OutDir", Env.Get("OUT_DIR", "out")).Trim();
        if (outDir.Length == 0) outDir = "out";

        var importantTickers = ReadString(cfg, "Agent:ImportantTickersPath", Env.Get("IMPORTANT_TICKERS_OUT", Path.Combine(outDir, "important_tickers.json"))).Trim();
        var financialOverview = ReadString(cfg, "Agent:FinancialOverviewPath", Env.Get("FINANCIAL_OVERVIEW_OUT", Path.Combine(outDir, "financial_overview.jsonl"))).Trim();
        var dataromaCtx = ReadString(cfg, "Agent:DataromaContextPath", Env.Get("DATAROMA_OUT_CTX", "dataroma_context.txt")).Trim();
        var vicCtx = ReadString(cfg, "Agent:VicContextPath", Env.Get("VIC_OUT_CTX", Path.Combine(outDir, "vic_context.txt"))).Trim();
        var foxlandCtx = ReadString(cfg, "Agent:FoxlandContextPath", Env.Get("OUT_CTX", "foxland_context.txt")).Trim();
        var agentState = ReadString(cfg, "Agent:AgentStatePath", Env.Get("AGENT_STATE_PATH", Path.Combine(outDir, "agent_state.json"))).Trim();

        return new AgentConfig(
            TelegramBotToken: ReadString(cfg, "Agent:TelegramBotToken", Env.Get("TELEGRAM_BOT_TOKEN", "")),
            TelegramChatId: ReadString(cfg, "Agent:TelegramChatId", Env.Get("TELEGRAM_CHAT_ID", "")),
            OpenAiApiKey: ReadString(cfg, "Agent:OpenAiApiKey", Env.Get("OPENAI_API_KEY", "")),
            OpenAiBaseUrl: ReadString(cfg, "Agent:OpenAiBaseUrl", Env.Get("OPENAI_BASE_URL", "https://api.openai.com/v1")).TrimEnd('/'),
            OpenAiModel: ReadString(cfg, "Agent:OpenAiModel", Env.Get("OPENAI_MODEL", "gpt-4o")),
            OpenAiMaxTokens: ClampInt(ReadInt(cfg, "Agent:OpenAiMaxTokens", Env.GetInt("OPENAI_MAX_TOKENS", 500)), 64, 16384),
            OpenAiTemperature: ClampDouble(ReadDouble(cfg, "Agent:OpenAiTemperature", Env.GetDouble("OPENAI_TEMPERATURE", 0.2)), 0.0, 2.0),
            AgentHeartbeatMinutes: ClampInt(ReadInt(cfg, "Agent:HeartbeatMinutes", Env.GetInt("AGENT_HEARTBEAT_MINUTES", 30)), 1, 24 * 60),
            AgentMinMinutesBetweenCycleAnalysis: ClampInt(ReadInt(cfg, "Agent:MinMinutesBetweenCycleAnalysis", Env.GetInt("AGENT_MIN_MINUTES_BETWEEN_CYCLE_ANALYSIS", 720)), 0, 7 * 24 * 60),
            AgentMaxContextChars: ClampInt(ReadInt(cfg, "Agent:MaxContextChars", Env.GetInt("AGENT_MAX_CONTEXT_CHARS", 40000)), 1000, 1_000_000),
            AgentMaxConversationTurns: ClampInt(ReadInt(cfg, "Agent:MaxConversationTurns", Env.GetInt("AGENT_MAX_CONVERSATION_TURNS", 12)), 1, 200),
            OutDir: outDir,
            ImportantTickersPath: importantTickers,
            FinancialOverviewPath: financialOverview,
            DataromaContextPath: dataromaCtx,
            VicContextPath: vicCtx,
            FoxlandContextPath: foxlandCtx,
            AgentStatePath: agentState
        );
    }

    public List<string> ValidateRequired()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(TelegramBotToken)) missing.Add("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(TelegramChatId)) missing.Add("TELEGRAM_CHAT_ID");
        var isDefaultOpenAi = OpenAiBaseUrl.StartsWith("https://api.openai.com", StringComparison.OrdinalIgnoreCase);
        if (isDefaultOpenAi && string.IsNullOrWhiteSpace(OpenAiApiKey)) missing.Add("OPENAI_API_KEY");
        return missing;
    }

    public bool HasRequiredSecrets => ValidateRequired().Count == 0;

    public string ToSafeSummary()
    {
        return string.Join(
            Environment.NewLine,
            [
                $"OpenAI base URL: {OpenAiBaseUrl}",
                $"OpenAI model: {OpenAiModel}",
                $"OpenAI max tokens: {OpenAiMaxTokens.ToString(CultureInfo.InvariantCulture)}",
                $"OpenAI temperature: {OpenAiTemperature.ToString("0.##", CultureInfo.InvariantCulture)}",
                $"Heartbeat minutes: {AgentHeartbeatMinutes.ToString(CultureInfo.InvariantCulture)}",
                $"Min minutes between cycle analysis: {AgentMinMinutesBetweenCycleAnalysis.ToString(CultureInfo.InvariantCulture)}",
                $"Max context chars: {AgentMaxContextChars.ToString(CultureInfo.InvariantCulture)}",
                $"Max conversation turns: {AgentMaxConversationTurns.ToString(CultureInfo.InvariantCulture)}",
                $"Out dir: {OutDir}",
                $"Important tickers path: {ImportantTickersPath}",
                $"Financial overview path: {FinancialOverviewPath}",
                $"Dataroma context path: {DataromaContextPath}",
                $"VIC context path: {VicContextPath}",
                $"Foxland context path: {FoxlandContextPath}",
                $"Agent state path: {AgentStatePath}",
            ]
        );
    }

    private static int ClampInt(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static double ClampDouble(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
        TryAddJson(builder, "appsettings.json");
        TryAddJson(builder, Path.Combine("src", "EmailExtractor", "appsettings.json"));
        TryAddJson(builder, Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        builder.AddUserSecrets(typeof(Program).Assembly, optional: true);
        builder.AddEnvironmentVariables();
        return builder.Build();
    }

    private static void TryAddJson(IConfigurationBuilder builder, string path)
    {
        if (File.Exists(path))
            builder.AddJsonFile(path, optional: true, reloadOnChange: false);
    }

    private static string ReadString(IConfiguration cfg, string key, string fallback)
    {
        var value = cfg[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(IConfiguration cfg, string key, int fallback)
    {
        var value = cfg[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ReadDouble(IConfiguration cfg, string key, double fallback)
    {
        var value = cfg[key];
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
