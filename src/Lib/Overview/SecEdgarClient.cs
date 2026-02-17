using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace ValueInvestorCrawler.Lib.Overview;

public sealed record SecYearMetrics(
    int FiscalYear,
    double? Revenue,
    double? GrossProfit,
    double? OperatingIncome,
    double? NetIncome,
    double? GrossMargin,
    double? OperatingMargin,
    double? NetMargin
);

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
    double? NetMargin,
    IReadOnlyList<SecYearMetrics> History
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

    public static SecOverview BuildOverview(string ticker, JsonNode facts, string cik10, int historyYears = 5)
    {
        var revenueSeries = CollectFySeries(facts, "Revenues");
        var grossProfitSeries = CollectFySeries(facts, "GrossProfit");
        var operatingIncomeSeries = CollectFySeries(facts, "OperatingIncomeLoss");
        var netIncomeSeries = CollectFySeries(facts, "NetIncomeLoss");

        var years = revenueSeries.Values.Keys
            .Concat(grossProfitSeries.Values.Keys)
            .Concat(operatingIncomeSeries.Values.Keys)
            .Concat(netIncomeSeries.Values.Keys)
            .Distinct()
            .OrderByDescending(y => y)
            .Take(Math.Max(1, historyYears))
            .ToList();

        double? Margin(double? n, double? d) => (n is null || d is null || d == 0) ? null : (n / d);

        var history = years
            .Select(y =>
            {
                var revenue = revenueSeries.Values.TryGetValue(y, out var r) ? r : null;
                var grossProfit = grossProfitSeries.Values.TryGetValue(y, out var gp) ? gp : null;
                var operatingIncome = operatingIncomeSeries.Values.TryGetValue(y, out var oi) ? oi : null;
                var netIncome = netIncomeSeries.Values.TryGetValue(y, out var ni) ? ni : null;
                return new SecYearMetrics(
                    FiscalYear: y,
                    Revenue: revenue,
                    GrossProfit: grossProfit,
                    OperatingIncome: operatingIncome,
                    NetIncome: netIncome,
                    GrossMargin: Margin(grossProfit, revenue),
                    OperatingMargin: Margin(operatingIncome, revenue),
                    NetMargin: Margin(netIncome, revenue)
                );
            })
            .ToList();

        var latest = history.FirstOrDefault();
        int? fy = latest?.FiscalYear;
        var unitR = revenueSeries.Unit;
        var unitGp = grossProfitSeries.Unit;
        var unitOi = operatingIncomeSeries.Unit;
        var unitNi = netIncomeSeries.Unit;
        var currency = unitR ?? unitGp ?? unitOi ?? unitNi;
        var name = facts?["entityName"]?.GetValue<string>();

        return new SecOverview(
            Ticker: ticker.Trim().ToUpperInvariant(),
            Cik: cik10,
            Name: name,
            FiscalYear: fy,
            Currency: currency,
            Revenue: latest?.Revenue,
            GrossProfit: latest?.GrossProfit,
            OperatingIncome: latest?.OperatingIncome,
            NetIncome: latest?.NetIncome,
            GrossMargin: latest?.GrossMargin,
            OperatingMargin: latest?.OperatingMargin,
            NetMargin: latest?.NetMargin,
            History: history
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

    private sealed record FySeries(Dictionary<int, double?> Values, string? Unit);

    private static FySeries CollectFySeries(JsonNode facts, string tag)
    {
        var units = facts?["facts"]?["us-gaap"]?[tag]?["units"] as JsonObject;
        if (units is null) return new FySeries([], null);

        Dictionary<int, double?> bestValues = [];
        string? bestUnit = null;
        var bestCount = -1;

        foreach (var unitK in units)
        {
            var unit = unitK.Key;
            if (unitK.Value is not JsonArray arr) continue;
            Dictionary<int, (string end, double val)> byYear = [];
            foreach (var itNode in arr)
            {
                if (itNode is not JsonObject it) continue;
                var fp = it["fp"]?.GetValue<string>() ?? "";
                var form = it["form"]?.GetValue<string>() ?? "";
                if (!fp.Equals("FY", StringComparison.OrdinalIgnoreCase)) continue;
                if (!form.Equals("10-K", StringComparison.OrdinalIgnoreCase)) continue;

                if (!int.TryParse(it["fy"]?.ToString(), out var fy)) continue;
                var end = it["end"]?.GetValue<string>() ?? "";
                if (!double.TryParse(it["val"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) continue;

                if (!byYear.TryGetValue(fy, out var current) || string.CompareOrdinal(end, current.end) > 0)
                    byYear[fy] = (end, val);
            }

            if (byYear.Count <= bestCount) continue;

            bestCount = byYear.Count;
            bestUnit = unit;
            bestValues = byYear.ToDictionary(x => x.Key, x => (double?)x.Value.val);
        }

        return new FySeries(bestValues, bestUnit);
    }
}
