using System.Globalization;

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
        var outDir = Env.Get("OUT_DIR", "out").Trim();
        if (outDir.Length == 0) outDir = "out";

        var importantTickers = Env.Get("IMPORTANT_TICKERS_OUT", Path.Combine(outDir, "important_tickers.json")).Trim();
        var financialOverview = Env.Get("FINANCIAL_OVERVIEW_OUT", Path.Combine(outDir, "financial_overview.jsonl")).Trim();
        var dataromaCtx = Env.Get("DATAROMA_OUT_CTX", "dataroma_context.txt").Trim();
        var vicCtx = Env.Get("VIC_OUT_CTX", Path.Combine(outDir, "vic_context.txt")).Trim();
        var foxlandCtx = Env.Get("OUT_CTX", "foxland_context.txt").Trim();
        var agentState = Env.Get("AGENT_STATE_PATH", Path.Combine(outDir, "agent_state.json")).Trim();

        return new AgentConfig(
            TelegramBotToken: Env.Get("TELEGRAM_BOT_TOKEN", ""),
            TelegramChatId: Env.Get("TELEGRAM_CHAT_ID", ""),
            OpenAiApiKey: Env.Get("OPENAI_API_KEY", ""),
            OpenAiBaseUrl: Env.Get("OPENAI_BASE_URL", "https://api.openai.com/v1").TrimEnd('/'),
            OpenAiModel: Env.Get("OPENAI_MODEL", "gpt-4o"),
            OpenAiMaxTokens: ClampInt(Env.GetInt("OPENAI_MAX_TOKENS", 500), 64, 16384),
            OpenAiTemperature: ClampDouble(Env.GetDouble("OPENAI_TEMPERATURE", 0.2), 0.0, 2.0),
            AgentHeartbeatMinutes: ClampInt(Env.GetInt("AGENT_HEARTBEAT_MINUTES", 30), 1, 24 * 60),
            AgentMinMinutesBetweenCycleAnalysis: ClampInt(Env.GetInt("AGENT_MIN_MINUTES_BETWEEN_CYCLE_ANALYSIS", 720), 0, 7 * 24 * 60),
            AgentMaxContextChars: ClampInt(Env.GetInt("AGENT_MAX_CONTEXT_CHARS", 40000), 1000, 1_000_000),
            AgentMaxConversationTurns: ClampInt(Env.GetInt("AGENT_MAX_CONVERSATION_TURNS", 12), 1, 200),
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
}
