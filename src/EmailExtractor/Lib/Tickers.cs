using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace EmailExtractor.Lib;

public sealed record RankedTicker(string Ticker, double Score, Dictionary<string, Dictionary<string, double>> Sources);

public static class Tickers
{
    private static readonly Regex TickerRe = new(@"\b[A-Z]{1,5}(?:\.[A-Z])?\b", RegexOptions.Compiled);
    private static readonly Regex ExchangePrefixRe = new(@"\b(?:NYSE|NASDAQ|AMEX|LSE|TSX|ASX|FWB|XETRA)\s*:\s*([A-Z]{1,5}(?:\.[A-Z])?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DollarRe = new(@"\$([A-Z]{1,5}(?:\.[A-Z])?)\b", RegexOptions.Compiled);
    private static readonly Regex SimbolParenRe = new(@"\(\s*simbol\s+([A-Z]{1,5}(?:\.[A-Z])?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TickerAfterKeywordRe = new(@"\b(?:ticker|simbol)\b[^A-Z$]{0,16}(\$?[A-Z]{1,5}(?:\.[A-Z])?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> FoxStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "A","I",
        "AI","AGI","API","CPU","GPU","HBM","DRAM","SSD",
        "SEC","GAAP","IFRS","EPS","PE","P","EV","FCF","ROIC","EBIT","EBITDA",
        "USD","EUR","RON",
        "USA","SUA","UE","UK",
        "BVB","NYSE","NASDAQ","AMEX","ETF","IPO",
        "ALL","NOW","NEW",
        "FOX","LAND","SRL","SRL.","SC","SA","RO",
        "AL","ALE","LA","DE","DIN","CU","SI","SAU","NU","DA","IN","UN","O","CE","CA",
        "MACRO","BURSA","LISTE","ALPHA","SUNT","MAI","ANUL","SCURT","ESTE","CI"
    };

    public static string Normalize(string t) => (t ?? "").Trim().ToUpperInvariant();

    public static List<string> ExtractTickersFoxland(string text, HashSet<string>? whitelist)
    {
        whitelist ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        text = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        var cand = new List<(string t, string src)>();
        foreach (Match m in ExchangePrefixRe.Matches(text)) cand.Add((m.Groups[1].Value, "exchange"));
        foreach (Match m in DollarRe.Matches(text)) cand.Add((m.Groups[1].Value, "dollar"));
        foreach (Match m in SimbolParenRe.Matches(text)) cand.Add((m.Groups[1].Value, "simbol_paren"));
        foreach (Match m in TickerAfterKeywordRe.Matches(text)) cand.Add((m.Groups[1].Value.TrimStart('$'), "keyword"));

        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t0, src) in cand)
        {
            var t = Normalize(t0);
            if (t.Length == 0) continue;
            if (FoxStopwords.Contains(t)) continue;
            if (t.Length == 1) continue;
            if (whitelist.Count > 0 && src is not ("exchange" or "dollar" or "simbol_paren" or "keyword") && !whitelist.Contains(t))
                continue;
            if (seen.Add(t)) outList.Add(t);
        }
        return outList;
    }

    public static Dictionary<string, double> RankFromDataromaMovesJsonl(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["bought"] = 3.0,
            ["added_to"] = 2.0,
            ["reduced"] = -1.0,
            ["sold_out"] = -2.0,
        };

        foreach (var line0 in File.ReadLines(path))
        {
            var line = line0.Trim();
            if (line.Length == 0) continue;
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var published = root.TryGetProperty("published_utc", out var p) ? p.GetString() : null;
                var dt = TryParseIsoUtc(published) ?? now;
                var ageDays = Math.Max(0.0, (now - dt).TotalDays);
                var recency = ageDays <= 7 ? 1.35 : ageDays <= 30 ? 1.20 : ageDays <= 90 ? 1.10 : 1.0;

                foreach (var (k, w) in weights)
                {
                    if (!root.TryGetProperty(k, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var it in arr.EnumerateArray())
                    {
                        var t = Normalize(it.GetString() ?? "");
                        if (t.Length == 0) continue;
                        scores[t] = scores.TryGetValue(t, out var cur) ? cur + (w * recency) : (w * recency);
                    }
                }
            }
        }

        return scores;
    }

    public static Dictionary<string, double> RankFromFoxlandContext(string path, HashSet<string>? whitelist)
    {
        if (!File.Exists(path)) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var docs = ContextDocs.Load(path);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in docs)
        {
            d.Headers.TryGetValue("subject", out var subj);
            var body = d.Body ?? "";
            var text = (subj ?? "") + "\n" + body;
            var tickers = ExtractTickersFoxland(text, whitelist);
            if (tickers.Count == 0) continue;

            var up = text.ToUpperInvariant();
            foreach (var t in tickers)
            {
                var mentions = Regex.Matches(up, $@"\b{Regex.Escape(t)}\b").Count;
                var s = Math.Max(1, mentions);
                if (!string.IsNullOrEmpty(subj) && subj.ToUpperInvariant().Contains(t)) s += 2;
                scores[t] = scores.TryGetValue(t, out var cur) ? cur + s : s;
            }
        }

        return scores;
    }

    public static List<RankedTicker> MergeRankings(Dictionary<string, Dictionary<string, double>> parts)
    {
        var total = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sourceName, sc) in parts)
        {
            foreach (var (t, v) in sc)
            {
                total[t] = total.TryGetValue(t, out var cur) ? cur + v : v;
                if (!sources.TryGetValue(t, out var srcs))
                {
                    srcs = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                    sources[t] = srcs;
                }
                srcs[sourceName] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["score"] = v };
            }
        }

        var outList = total
            .Select(kv => new RankedTicker(kv.Key, kv.Value, sources[kv.Key]))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return outList;
    }

    private static DateTimeOffset? TryParseIsoUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToUniversalTime();
        return null;
    }
}

