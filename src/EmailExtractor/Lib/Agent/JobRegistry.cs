using EmailExtractor.Commands;

namespace EmailExtractor.Lib.Agent;

public sealed record JobDefinition(string Name, string Description, Func<CancellationToken, Task<int>> Execute);

public static class JobRegistry
{
    public static Dictionary<string, JobDefinition> All() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["foxland-format"] = new("foxland-format", "Convert foxland dump → context",
            _ => Task.FromResult(FoxlandFormatForLlm.Run([]))),
        ["dataroma-rss"] = new("dataroma-rss", "Fetch Dataroma RSS feed",
            _ => DataromaRssExport.Run([])),
        ["extract-tickers"] = new("extract-tickers", "Rank tickers from all sources",
            _ => Task.FromResult(ExtractImportantTickers.Run([]))),
        ["fetch-overview"] = new("fetch-overview", "Fetch financial data for top tickers",
            _ => FetchFinancialOverview.Run([])),
        ["vic-collect-links"] = new("vic-collect-links", "Collect VIC idea links",
            _ => VicCollectLinks.Run([])),
        ["vic-crawl"] = new("vic-crawl", "Crawl VIC ideas",
            _ => VicCrawlIdeas.Run([])),
    };

    public static Dictionary<string, string[]> Pipelines() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = ["dataroma-rss", "extract-tickers", "fetch-overview"],
        ["full"]    = ["foxland-format", "dataroma-rss", "vic-collect-links", "vic-crawl", "extract-tickers", "fetch-overview"],
    };
}
