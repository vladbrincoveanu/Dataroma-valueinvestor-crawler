using System.Text.Json;
using ValueInvestorCrawler.Lib;

namespace ValueInvestorCrawler.Commands;

public static class ExtractImportantTickers
{
    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        var dataroma = a.Get("dataroma", "dataroma_moves.jsonl");
        var foxland = a.Get("foxland", "foxland_context.txt");
        var top = a.GetInt("top", 50);
        var minScore = a.GetDouble("min-score", 3.0);
        var outPath = a.Get("out", "out/important_tickers.json");

        var dataromaScores = Tickers.RankFromDataromaMovesJsonl(dataroma);
        var whitelist = new HashSet<string>(dataromaScores.Keys, StringComparer.OrdinalIgnoreCase);
        var foxlandScores = Tickers.RankFromFoxlandContext(foxland, whitelist);

        var merged = Tickers.MergeRankings(new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dataroma"] = dataromaScores,
            ["foxland"] = foxlandScores,
        });

        var filtered = merged.Where(r => r.Score >= minScore).Take(top).ToList();

        var obj = new
        {
            inputs = new { dataroma, foxland },
            top,
            min_score = minScore,
            tickers = filtered.Select(r => new { ticker = r.Ticker, score = r.Score, sources = r.Sources }).ToList()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        File.WriteAllText(outPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Wrote {outPath} (tickers={filtered.Count})");
        return 0;
    }
}

