using ValueInvestorCrawler.Commands;

namespace ValueInvestorCrawler;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return await AgentLoop.Run([]);

        if (args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage.Text);
            return 0;
        }

        var cmd = args[0].Trim().ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return cmd switch
            {
                "foxland-format" => FoxlandFormatForLlm.Run(rest),
                "dataroma-rss" => await DataromaRssExport.Run(rest),
                "extract-tickers" => ExtractImportantTickers.Run(rest),
                "fetch-overview" => await FetchFinancialOverview.Run(rest),
                "vic-collect-links" => await VicCollectLinks.Run(rest),
                "vic-crawl" => await VicCrawlIdeas.Run(rest),
                "agent-loop" => await AgentLoop.Run(rest),
                _ => Unknown(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage.Text);
        return 2;
    }
}
