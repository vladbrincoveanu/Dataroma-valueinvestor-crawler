using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EmailExtractor.Lib.Agent;

public static class ContextBuilder
{
    private const string Preamble =
        "You are a proactive investment research assistant. You have access to the user's portfolio and investment research data.\n" +
        "Analyze the data and provide actionable insights. Be concise, specific, and data-driven.";

    public static string Build(AgentConfig config)
    {
        var total = config.AgentMaxContextChars;
        var slice = total / 5;

        var sb = new StringBuilder();
        sb.AppendLine(Preamble);
        sb.AppendLine();

        sb.AppendLine("=== IMPORTANT TICKERS ===");
        sb.AppendLine(BuildTickersSection(config.ImportantTickersPath, slice));
        sb.AppendLine();

        sb.AppendLine("=== FINANCIAL OVERVIEW ===");
        sb.AppendLine(BuildFinancialOverviewSection(config.FinancialOverviewPath, slice));
        sb.AppendLine();

        sb.AppendLine("=== DATAROMA INVESTOR MOVES ===");
        sb.AppendLine(BuildContextDocsSection(config.DataromaContextPath, slice));
        sb.AppendLine();

        sb.AppendLine("=== VIC IDEAS ===");
        sb.AppendLine(BuildContextDocsSection(config.VicContextPath, slice));
        sb.AppendLine();

        sb.AppendLine("=== FOXLAND CONTEXT ===");
        sb.AppendLine(BuildContextDocsSection(config.FoxlandContextPath, slice));

        return sb.ToString();
    }

    private static string BuildTickersSection(string path, int budget)
    {
        if (!TryReadFile(path, out var content)) return "(no data available)";

        List<JsonElement> items;
        try { items = JsonSerializer.Deserialize<List<JsonElement>>(content) ?? []; }
        catch (JsonException) { return "(no data available)"; }

        if (items.Count == 0) return "(no data available)";

        var sb = new StringBuilder();
        sb.AppendLine($"{"Ticker",-10} {"Score",10}");
        sb.AppendLine(new string('-', 22));

        foreach (var item in items)
        {
            var ticker = item.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "";
            var score = item.TryGetProperty("score", out var s) ? FormatScore(s) : "?";
            if (ticker.Length == 0) continue;
            sb.AppendLine($"{ticker,-10} {score,10}");
        }

        return Truncate(sb.ToString().TrimEnd(), budget);
    }

    private static string BuildFinancialOverviewSection(string path, int budget)
    {
        if (!TryReadFile(path, out var content)) return "(no data available)";

        var sb = new StringBuilder();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            JsonElement doc;
            try { doc = JsonSerializer.Deserialize<JsonElement>(trimmed); }
            catch (JsonException) { continue; }

            var ticker = doc.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "";
            if (ticker.Length > 0) sb.AppendLine($"[{ticker}]");

            foreach (var prop in doc.EnumerateObject())
            {
                if (string.Equals(prop.Name, "ticker", StringComparison.OrdinalIgnoreCase)) continue;
                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "null",
                    _ => prop.Value.GetRawText(),
                };
                sb.AppendLine($"  {prop.Name}: {val}");
            }
            sb.AppendLine();
        }

        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? "(no data available)" : Truncate(result, budget);
    }

    private static string BuildContextDocsSection(string path, int budget)
    {
        if (!TryReadFile(path, out var content)) return "(no data available)";

        List<ContextDoc> docs;
        try { docs = ContextDocs.Parse(content); }
        catch { return "(no data available)"; }

        if (docs.Count == 0) return "(no data available)";

        var sb = new StringBuilder();
        foreach (var doc in docs)
        {
            sb.AppendLine($"[{doc.DocId}]");
            foreach (var (key, value) in doc.Headers)
                sb.AppendLine($"{key}: {value}");
            if (!string.IsNullOrWhiteSpace(doc.Body))
                sb.AppendLine(doc.Body.Trim());
            sb.AppendLine();
        }

        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? "(no data available)" : Truncate(result, budget);
    }

    private static bool TryReadFile(string path, out string content)
    {
        content = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { content = File.ReadAllText(path); return true; }
        catch { return false; }
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        const string suffix = "\n[... truncated ...]";
        return text.Substring(0, Math.Max(0, maxChars - suffix.Length)) + suffix;
    }

    private static string FormatScore(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d))
            return d.ToString("0.##", CultureInfo.InvariantCulture);
        return element.GetRawText();
    }
}
