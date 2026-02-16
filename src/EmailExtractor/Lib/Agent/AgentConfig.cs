using System.Globalization;
using System.Text.Json;

namespace EmailExtractor.Lib.Agent;

public sealed record AgentConfig(
    string TelegramBotToken,
    string TelegramChatId,
    string OpenAiApiKey,
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
        var appSettings = AgentAppSettings.Load();
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
            OpenAiModel: appSettings.OpenAiModel,
            OpenAiMaxTokens: ClampInt(appSettings.OpenAiMaxTokens, 64, 16384),
            OpenAiTemperature: ClampDouble(appSettings.OpenAiTemperature, 0.0, 2.0),
            AgentHeartbeatMinutes: ClampInt(appSettings.AgentHeartbeatMinutes, 1, 24 * 60),
            AgentMinMinutesBetweenCycleAnalysis: ClampInt(appSettings.AgentMinMinutesBetweenCycleAnalysis, 0, 7 * 24 * 60),
            AgentMaxContextChars: ClampInt(appSettings.AgentMaxContextChars, 1000, 1_000_000),
            AgentMaxConversationTurns: ClampInt(appSettings.AgentMaxConversationTurns, 1, 200),
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
        if (string.IsNullOrWhiteSpace(OpenAiApiKey)) missing.Add("OPENAI_API_KEY");
        return missing;
    }

    public bool HasRequiredSecrets => ValidateRequired().Count == 0;

    public string ToSafeSummary()
    {
        return string.Join(
            Environment.NewLine,
            [
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

internal sealed record AgentAppSettings(
    string OpenAiModel,
    int OpenAiMaxTokens,
    double OpenAiTemperature,
    int AgentHeartbeatMinutes,
    int AgentMinMinutesBetweenCycleAnalysis,
    int AgentMaxContextChars,
    int AgentMaxConversationTurns)
{
    public static AgentAppSettings Load()
    {
        var path = Env.Get("APPSETTINGS_PATH", "src/EmailExtractor/appsettings.json");
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(path);

        if (!File.Exists(path))
            return Defaults();

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Agent", out var agent))
                return Defaults();

            return new AgentAppSettings(
                OpenAiModel: ReadString(agent, "OpenAiModel", "gpt-4o"),
                OpenAiMaxTokens: ReadInt(agent, "OpenAiMaxTokens", 1500),
                OpenAiTemperature: ReadDouble(agent, "OpenAiTemperature", 0.2),
                AgentHeartbeatMinutes: ReadInt(agent, "HeartbeatMinutes", 30),
                AgentMinMinutesBetweenCycleAnalysis: ReadInt(agent, "MinMinutesBetweenCycleAnalysis", 180),
                AgentMaxContextChars: ReadInt(agent, "MaxContextChars", 40000),
                AgentMaxConversationTurns: ReadInt(agent, "MaxConversationTurns", 12)
            );
        }
        catch
        {
            return Defaults();
        }
    }

    private static AgentAppSettings Defaults()
    {
        return new AgentAppSettings(
            OpenAiModel: "gpt-4o",
            OpenAiMaxTokens: 1500,
            OpenAiTemperature: 0.2,
            AgentHeartbeatMinutes: 30,
            AgentMinMinutesBetweenCycleAnalysis: 180,
            AgentMaxContextChars: 40000,
            AgentMaxConversationTurns: 12
        );
    }

    private static string ReadString(JsonElement parent, string prop, string fallback)
    {
        if (!parent.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String)
            return fallback;

        var parsed = v.GetString()?.Trim() ?? "";
        return parsed.Length == 0 ? fallback : parsed;
    }

    private static int ReadInt(JsonElement parent, string prop, int fallback)
    {
        if (!parent.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            return fallback;

        return v.TryGetInt32(out var parsed) ? parsed : fallback;
    }

    private static double ReadDouble(JsonElement parent, string prop, double fallback)
    {
        if (!parent.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            return fallback;

        return v.TryGetDouble(out var parsed) ? parsed : fallback;
    }
}
