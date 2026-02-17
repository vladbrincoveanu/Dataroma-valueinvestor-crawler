using System.Text.Json;
using System.Text.Json.Nodes;
using ValueInvestorCrawler.Lib;
using ValueInvestorCrawler.Lib.Overview;

namespace ValueInvestorCrawler.Commands;

public static class FetchFinancialOverview
{
    public static async Task<int> Run(string[] argv)
    {
        var a = Args.Parse(argv);
        var inPath = a.Get("in", "out/important_tickers.json");
        var outPath = a.Get("out", "out/financial_overview.jsonl");
        var provider = a.Get("provider", "sec_then_stockanalysis").Trim().ToLowerInvariant();
        var historyYears = Math.Max(1, a.GetInt("history-years", 5));

        if (!File.Exists(inPath)) throw new Exception($"Missing input file: {inPath}");
        var tickers = LoadTickers(inPath);
        if (tickers.Count == 0) throw new Exception($"No tickers found in: {inPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var sw = new StreamWriter(outPath, append: false, encoding: new System.Text.UTF8Encoding(false));

        var secUa = Env.Get("SEC_USER_AGENT");
        SecEdgarClient? sec = !string.IsNullOrWhiteSpace(secUa) ? new SecEdgarClient(secUa) : null;
        var sa = new StockAnalysisClient();

        foreach (var t0 in tickers)
        {
            var t = (t0 ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) continue;

            try
            {
                JsonNode rec = provider switch
                {
                    "sec" => await FetchSec(sec, t, historyYears),
                    "stockanalysis" => await FetchStockAnalysis(sa, t, historyYears),
                    "sec_then_stockanalysis" => await FetchSecThenStockAnalysis(sec, sa, t, historyYears),
                    _ => throw new Exception($"Unknown provider: {provider}")
                };

                await sw.WriteLineAsync(rec.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                Console.WriteLine($"{t}: ok ({rec?["provider"]})");
            }
            catch (Exception ex)
            {
                var err = new JsonObject
                {
                    ["ticker"] = t,
                    ["provider"] = "error",
                    ["error"] = ex.Message
                };
                await sw.WriteLineAsync(err.ToJsonString());
                Console.WriteLine($"{t}: error: {ex.Message}");
            }
        }

        Console.WriteLine($"Wrote {outPath} (rows={tickers.Count})");
        return 0;
    }

    private static List<string> LoadTickers(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        var outList = new List<string>();
        var arr = node?["tickers"] as JsonArray;
        if (arr is null) return outList;
        foreach (var it in arr)
        {
            var t = it?["ticker"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "";
            if (t.Length == 0) continue;
            if (!outList.Contains(t, StringComparer.OrdinalIgnoreCase))
                outList.Add(t);
        }
        return outList;
    }

    private static async Task<JsonNode> FetchSec(SecEdgarClient? sec, string ticker, int historyYears)
    {
        if (sec is null) throw new Exception("SEC_USER_AGENT not set; cannot use SEC provider.");
        var cik = await sec.CikForTicker(ticker);
        if (string.IsNullOrWhiteSpace(cik)) throw new Exception($"No CIK found for ticker {ticker}");
        var facts = await sec.CompanyFacts(cik);
        if (facts is null) throw new Exception($"No company facts for CIK {cik}");
        var ov = SecEdgarClient.BuildOverview(ticker, facts, cik, historyYears);
        var node = JsonSerializer.SerializeToNode(ov)!;
        node["provider"] = "sec";
        return node;
    }

    private static async Task<JsonNode> FetchStockAnalysis(StockAnalysisClient sa, string ticker, int historyYears)
    {
        var ov = await sa.FetchOverview(ticker);
        return new JsonObject
        {
            ["ticker"] = ov.Ticker,
            ["provider"] = "stockanalysis",
            ["url"] = ov.Url,
            ["stats"] = JsonSerializer.SerializeToNode(ov.Stats)!,
            ["history_years_requested"] = historyYears,
            ["history"] = new JsonArray()
        };
    }

    private static async Task<JsonNode> FetchSecThenStockAnalysis(SecEdgarClient? sec, StockAnalysisClient sa, string ticker, int historyYears)
    {
        try
        {
            if (sec is not null)
                return await FetchSec(sec, ticker, historyYears);
        }
        catch
        {
            // fall through
        }
        return await FetchStockAnalysis(sa, ticker, historyYears);
    }
}
