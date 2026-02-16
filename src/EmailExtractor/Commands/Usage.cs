namespace EmailExtractor.Commands;

public static class Usage
{
    public const string Text = """
EmailExtractor (.NET)

Commands:
  foxland-format    Convert foxland_dump.txt -> foxland_context.txt format
  dataroma-rss      Fetch Dataroma RSS and export dataroma_moves.jsonl + dataroma_context.txt
  extract-tickers   Rank important tickers from Dataroma + Foxland outputs
  fetch-overview    Fetch overview for tickers (SEC includes up to 5 FY history by default)
  vic-collect-links Collect VIC /idea/... links from ideas pages into a deduped links file
  vic-crawl         Crawl VIC idea pages from links file; optional login; export JSONL + context docs
  agent-loop        Run continuous agent: pipeline heartbeat + Telegram bot + OpenAI insights

Notes:
  - Running with no command starts `agent-loop` by default.
  - Agent config uses appsettings + user-secrets (env vars still supported as fallback).
  - Use Telegram `/task` commands to run background jobs and pipelines.
  - For SEC EDGAR calls, set SEC_USER_AGENT="AppName you@domain.com".
  - Use --history-years N (default 5) to control SEC fiscal-year history depth.
  - StockAnalysis fallback returns summary stats; history array stays empty.
  - VIC crawling can require login and may still return teaser-only content.
""";
}
