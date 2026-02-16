using EmailExtractor.Commands;

namespace EmailExtractor.Lib.Agent;

public sealed record JobDefinition(
    string Name,
    string Description,
    Func<CancellationToken, Task<int>> Execute);

public static class JobRegistry
{
    public static Dictionary<string, JobDefinition> All()
    {
        return new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["foxland-format"] = new(
                "foxland-format",
                "Convert foxland dump into context-doc format.",
                _ => Task.FromResult(FoxlandFormatForLlm.Run([]))),
            ["dataroma-rss"] = new(
                "dataroma-rss",
                "Fetch Dataroma RSS and export moves/context.",
                _ => DataromaRssExport.Run([])),
            ["extract-tickers"] = new(
                "extract-tickers",
                "Rank important tickers from context sources.",
                _ => Task.FromResult(ExtractImportantTickers.Run([]))),
            ["fetch-overview"] = new(
                "fetch-overview",
                "Fetch SEC/StockAnalysis overview for important tickers.",
                _ => FetchFinancialOverview.Run([])),
            ["vic-collect-links"] = new(
                "vic-collect-links",
                "Collect and dedupe VIC idea links.",
                _ => VicCollectLinks.Run([])),
            ["vic-crawl"] = new(
                "vic-crawl",
                "Crawl VIC ideas and export JSONL/context docs.",
                _ => VicCrawlIdeas.Run([]))
        };
    }

    public static Dictionary<string, string[]> Pipelines()
    {
        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = ["dataroma-rss", "extract-tickers", "fetch-overview"],
            ["full"] =
            [
                "foxland-format",
                "dataroma-rss",
                "vic-collect-links",
                "vic-crawl",
                "extract-tickers",
                "fetch-overview"
            ]
        };
    }
}
