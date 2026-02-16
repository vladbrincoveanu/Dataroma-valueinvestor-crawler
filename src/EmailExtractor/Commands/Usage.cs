namespace EmailExtractor.Commands;

public static class Usage
{
    public const string Text = """
EmailExtractor (.NET)

Commands:
  foxland-format    Convert foxland_dump.txt -> foxland_context.txt format
  dataroma-rss      Fetch Dataroma RSS and export dataroma_moves.jsonl + dataroma_context.txt
  extract-tickers   Rank important tickers from Dataroma + Foxland outputs
  fetch-overview    Fetch financial overview for tickers (SEC EDGAR, optional StockAnalysis fallback)
  vic-collect-links Collect VIC /idea/... links from ideas pages into a deduped links file
  vic-crawl         Crawl VIC idea pages from links file; optional login; export JSONL + context docs

Notes:
  - Configuration is via env vars.
  - For SEC EDGAR calls, set SEC_USER_AGENT="AppName you@domain.com".
  - VIC crawling can require login and may still return teaser-only content.
""";
}
