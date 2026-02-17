using System.Net;
using System.Text.RegularExpressions;

namespace ValueInvestorCrawler.Lib.Overview;

public sealed record StockAnalysisOverview(string Ticker, string Url, Dictionary<string, string> Stats);

public sealed class StockAnalysisClient
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private static readonly Regex TableRe = new(@"<table\b[^>]*>(.*?)</table\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TrRe = new(@"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellRe = new(@"<(td|th)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DlRe = new(@"<dl\b[^>]*>(.*?)</dl\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DtRe = new(@"<dt\b[^>]*>(.*?)</dt\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DdRe = new(@"<dd\b[^>]*>(.*?)</dd\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    public StockAnalysisClient(string cacheDir = "out/cache/stockanalysis", string userAgent = "Mozilla/5.0 (compatible; ValueInvestorCrawler/1.0)")
    {
        _cacheDir = cacheDir;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public async Task<StockAnalysisOverview> FetchOverview(string ticker)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (ticker.Length == 0) throw new ArgumentException("ticker is required", nameof(ticker));

        var url = $"https://stockanalysis.com/stocks/{ticker.ToLowerInvariant()}/";
        var cachePath = Path.Combine(_cacheDir, $"{ticker}.html");
        var html = await GetHtml(url, cachePath);

        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match tm in TableRe.Matches(html))
        {
            var tableHtml = tm.Groups[1].Value;
            foreach (Match trm in TrRe.Matches(tableHtml))
            {
                var trHtml = trm.Groups[1].Value;
                var cells = CellRe.Matches(trHtml).Cast<Match>().Select(m => m.Groups[2].Value).ToList();
                if (cells.Count < 2) continue;
                var k = Norm(StripTags(cells[0]));
                var v = Norm(StripTags(cells[1]));
                if (k.Length == 0 || v.Length == 0) continue;
                if (k.Length > 60) continue;
                stats.TryAdd(k, v);
            }
        }

        foreach (Match dm in DlRe.Matches(html))
        {
            var dlHtml = dm.Groups[1].Value;
            var dts = DtRe.Matches(dlHtml).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var dds = DdRe.Matches(dlHtml).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            if (dts.Count == 0 || dts.Count != dds.Count) continue;
            for (var i = 0; i < dts.Count; i++)
            {
                var k = Norm(StripTags(dts[i]));
                var v = Norm(StripTags(dds[i]));
                if (k.Length == 0 || v.Length == 0) continue;
                stats.TryAdd(k, v);
            }
        }

        return new StockAnalysisOverview(ticker, url, stats);
    }

    private async Task<string> GetHtml(string url, string cachePath)
    {
        if (File.Exists(cachePath))
            return await File.ReadAllTextAsync(cachePath);

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(cachePath, html);
        return html;
    }

    private static string Norm(string s)
    {
        s = WebUtility.HtmlDecode(s ?? "");
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Trim();
    }

    private static string StripTags(string s) => TagRe.Replace(s ?? "", " ");
}
