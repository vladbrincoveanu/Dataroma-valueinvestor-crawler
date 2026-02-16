# email_extractor

This repository contains a .NET CLI solution for generating context docs, ranking tickers, and fetching financial overviews.

## Architecture

- `Program.cs` is a thin command router.
- Each command in `src/EmailExtractor/Commands/` is a focused runner.
- `Lib/` contains shared helpers (`Args`, `TextUtil`, `Tickers`) and provider clients (`SecEdgarClient`, `StockAnalysisClient`).
- `SecEdgarClient` fetches structured SEC company facts and returns latest FY metrics plus fiscal-year history (default 5 years).
- `StockAnalysisClient` is a fallback scraper for summary stats.

## Prereqs

- .NET SDK 10+
- `.env` (see `.env.example`); auto-loaded at startup if present

## Build

```bash
dotnet build EmailExtractor.sln
```

## Test

```bash
dotnet test EmailExtractor.sln
```

Live VIC smoke test (opt-in, network):

```bash
RUN_LIVE_VIC_TESTS=1 dotnet test EmailExtractor.sln
```

## Run

List commands:

```bash
dotnet run --project src/EmailExtractor/EmailExtractor.csproj --
```

Examples:

```bash
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- dataroma-rss
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- extract-tickers --top 50 --min-score 3 --out out/important_tickers.json
SEC_USER_AGENT="EmailExtractor you@domain.com" dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- fetch-overview --in out/important_tickers.json --out out/financial_overview.jsonl --history-years 5
```

VIC crawl examples:

```bash
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-collect-links --url https://www.valueinvestorsclub.com/ideas/ --pages 1 --out idea_links_no_duplicates.txt
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-crawl --links-file idea_links_no_duplicates.txt --limit 50 --out out/vic_ideas.jsonl --out-ctx out/vic_context.txt
VIC_ENABLE_LOGIN=1 VIC_USERNAME="your_user" VIC_PASSWORD="your_pass" dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-crawl --limit 20
```

## Agent Config

All agent settings are env-driven (`.env` is auto-loaded):

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_CHAT_ID`
- `OPENAI_BASE_URL` (default: `https://api.openai.com/v1`)
- `OPENAI_API_KEY` (required only for OpenAI cloud endpoint)
- `OPENAI_MODEL`
- `OPENAI_MAX_TOKENS`
- `OPENAI_TEMPERATURE`
- `AGENT_HEARTBEAT_MINUTES`
- `AGENT_MIN_MINUTES_BETWEEN_CYCLE_ANALYSIS`
- `AGENT_MAX_CONTEXT_CHARS`
- `AGENT_MAX_CONVERSATION_TURNS`
