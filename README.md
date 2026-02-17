# value_investor_crawler

This repository contains a .NET CLI solution for generating context docs, ranking tickers, and fetching financial overviews.

## Architecture

- `src/Program.cs` is a thin command router.
- Each command in `src/Commands/` is a focused runner.
- `Lib/` contains shared helpers (`Args`, `TextUtil`, `Tickers`) and provider clients (`SecEdgarClient`, `StockAnalysisClient`).
- `SecEdgarClient` fetches structured SEC company facts and returns latest FY metrics plus fiscal-year history (default 5 years).
- `StockAnalysisClient` is a fallback scraper for summary stats.

## Prereqs

- .NET SDK 10+
- `src/EmailExtractor/appsettings.json` for non-secret agent settings
- .NET user-secrets for agent secrets (or environment variables as fallback)

## Build

```bash
dotnet build ValueInvestorCrawler.sln
```

## Test

```bash
dotnet test ValueInvestorCrawler.sln
```

Live VIC smoke test (opt-in, network):

```bash
RUN_LIVE_VIC_TESTS=1 dotnet test ValueInvestorCrawler.sln
```

## Run

List commands:

```bash
dotnet run --project src/ValueInvestorCrawler.csproj --
```

Examples:

```bash
dotnet run --project src/ValueInvestorCrawler.csproj -- dataroma-rss
dotnet run --project src/ValueInvestorCrawler.csproj -- extract-tickers --top 50 --min-score 3 --out out/important_tickers.json
SEC_USER_AGENT="ValueInvestorCrawler you@domain.com" dotnet run --project src/ValueInvestorCrawler.csproj -- fetch-overview --in out/important_tickers.json --out out/financial_overview.jsonl --history-years 5
```

VIC crawl examples:

```bash
dotnet run --project src/ValueInvestorCrawler.csproj -- vic-collect-links --url https://www.valueinvestorsclub.com/ideas/ --pages 1 --out idea_links_no_duplicates.txt
dotnet run --project src/ValueInvestorCrawler.csproj -- vic-crawl --links-file idea_links_no_duplicates.txt --limit 50 --out out/vic_ideas.jsonl --out-ctx out/vic_context.txt
VIC_ENABLE_LOGIN=1 VIC_USERNAME="your_user" VIC_PASSWORD="your_pass" dotnet run --project src/ValueInvestorCrawler.csproj -- vic-crawl --limit 20
```

## Agent Config

Non-secrets are read from `src/EmailExtractor/appsettings.json` (`Agent` section), for example:

- `Agent.OpenAiBaseUrl`
- `Agent.OpenAiModel`
- `Agent.OpenAiMaxTokens`
- `Agent.OpenAiTemperature`
- `Agent.HeartbeatMinutes`
- `Agent.MinMinutesBetweenCycleAnalysis`
- `Agent.MaxContextChars`
- `Agent.MaxConversationTurns`

Secrets should be stored in user-secrets:

```bash
dotnet user-secrets --project src/EmailExtractor/EmailExtractor.csproj set "Agent:TelegramBotToken" "<token>"
dotnet user-secrets --project src/EmailExtractor/EmailExtractor.csproj set "Agent:TelegramChatId" "<chat-id>"
dotnet user-secrets --project src/EmailExtractor/EmailExtractor.csproj set "Agent:OpenAiApiKey" "<api-key>"
```

Environment variables remain supported as fallback for compatibility.
