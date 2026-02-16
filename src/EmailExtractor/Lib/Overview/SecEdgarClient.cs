using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmailExtractor.Lib.Overview;

public sealed record SecOverview(
    string Ticker,
    string Cik,
    string? Name,
    int? FiscalYear,
    string? Currency,
    double? Revenue,
    double? GrossProfit,
    double? OperatingIncome,
    double? NetIncome,
    double? GrossMargin,
    double? OperatingMargin,
    double? NetMargin
);

public sealed class SecEdgarClient
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly TimeSpan _throttle;
    private DateTimeOffset _lastReq = DateTimeOffset.MinValue;

    public SecEdgarClient(string userAgent, string cacheDir = "out/cache/sec", double throttleSec = 0.25)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            throw new ArgumentException("SEC_USER_AGENT is required (example: 'YourApp you@email.com').", nameof(userAgent));

        _cacheDir = cacheDir;
        _throttle = TimeSpan.FromSeconds(throttleSec);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
    }

    public async Task<string?> CikForTicker(string ticker)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (ticker.Length == 0) return null;

        var map = await LoadTickerMap();
        return map.TryGetValue(ticker, out var cik) ? cik : null;
    }

    public async Task<JsonNode?> CompanyFacts(string cik10)
    {
        cik10 = (cik10 ?? "").Trim();
        if (cik10.Length != 10 || !cik10.All(char.IsDigit))
            throw new ArgumentException($"Expected 10-digit CIK, got: {cik10}", nameof(cik10));

        var url = $"https://data.sec.gov/api/xbrl/companyfacts/CIK{cik10}.json";
        var cachePath = Path.Combine(_cacheDir, $"companyfacts_{cik10}.json");
        return await GetJson(url, cachePath);
    }

    public static SecOverview BuildOverview(string ticker, JsonNode facts, string cik10)
    {
        (int? fyR, double? revenue, string? unitR) = LatestFyValue(facts, "Revenues");
        (int? fyGp, double? grossProfit, string? unitGp) = LatestFyValue(facts, "GrossProfit");
        (int? fyOi, double? operatingIncome, string? unitOi) = LatestFyValue(facts, "OperatingIncomeLoss");
        (int? fyNi, double? netIncome, string? unitNi) = LatestFyValue(facts, "NetIncomeLoss");

        int? fy = fyR ?? fyGp ?? fyOi ?? fyNi;
        var currency = unitR ?? unitGp ?? unitOi ?? unitNi;
        var name = facts?["entityName"]?.GetValue<string>();

        double? Margin(double? n, double? d) => (n is null || d is null || d == 0) ? null : (n / d);

        return new SecOverview(
            Ticker: ticker.Trim().ToUpperInvariant(),
            Cik: cik10,
            Name: name,
            FiscalYear: fy,
            Currency: currency,
            Revenue: revenue,
            GrossProfit: grossProfit,
            OperatingIncome: operatingIncome,
            NetIncome: netIncome,
            GrossMargin: Margin(grossProfit, revenue),
            OperatingMargin: Margin(operatingIncome, revenue),
            NetMargin: Margin(netIncome, revenue)
        );
    }

    private async Task<Dictionary<string, string>> LoadTickerMap()
    {
        var url = "https://www.sec.gov/files/company_tickers.json";
        var cachePath = Path.Combine(_cacheDir, "company_tickers.json");
        var json = await GetJson(url, cachePath);
        var outMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (json is not JsonObject root) return outMap;
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject rec) continue;
            var t = (rec["ticker"]?.GetValue<string>() ?? "").Trim().ToUpperInvariant();
            var cikStr = rec["cik_str"]?.ToString()?.Trim() ?? "";
            if (t.Length == 0 || cikStr.Length == 0) continue;
            var cik10 = cikStr.PadLeft(10, '0');
            outMap[t] = cik10;
        }
        return outMap;
    }

    private async Task<JsonNode?> GetJson(string url, string cachePath)
    {
        if (File.Exists(cachePath))
        {
            var cached = await File.ReadAllTextAsync(cachePath);
            return JsonNode.Parse(cached);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await Throttle();
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(cachePath, text);
        return JsonNode.Parse(text);
    }

    private async Task Throttle()
    {
        var now = DateTimeOffset.UtcNow;
        var dt = now - _lastReq;
        if (dt < _throttle)
            await Task.Delay(_throttle - dt);
        _lastReq = DateTimeOffset.UtcNow;
    }

    private static (int? fy, double? val, string? unit) LatestFyValue(JsonNode facts, string tag)
    {
        // facts.facts["us-gaap"][tag].units[unit] is an array of objects.
        var units = facts?["facts"]?["us-gaap"]?[tag]?["units"] as JsonObject;
        if (units is null) return (null, null, null);

        (int fy, string end, double val, string unit)? best = null;
        foreach (var unitK in units)
        {
            var unit = unitK.Key;
            if (unitK.Value is not JsonArray arr) continue;
            foreach (var itNode in arr)
            {
                if (itNode is not JsonObject it) continue;
                var fp = it["fp"]?.GetValue<string>() ?? "";
                var form = it["form"]?.GetValue<string>() ?? "";
                if (!fp.Equals("FY", StringComparison.OrdinalIgnoreCase)) continue;
                if (!form.Equals("10-K", StringComparison.OrdinalIgnoreCase)) continue;

                if (!int.TryParse(it["fy"]?.ToString(), out var fy)) continue;
                var end = it["end"]?.GetValue<string>() ?? "";
                if (!double.TryParse(it["val"]?.ToString(), out var val)) continue;

                var cand = (fy, end, val, unit);
                if (best is null || cand.fy > best.Value.fy || (cand.fy == best.Value.fy && string.CompareOrdinal(cand.end, best.Value.end) > 0))
                    best = cand;
            }
        }
        return best is null ? (null, null, null) : (best.Value.fy, best.Value.val, best.Value.unit);
    }
}

