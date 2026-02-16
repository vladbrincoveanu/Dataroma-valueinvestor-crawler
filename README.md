# email_extractor

This repository contains a .NET CLI solution for generating context docs, ranking tickers, and fetching financial overviews.

## Prereqs

- .NET SDK 10+
- `.env` (see `.env.example`) for secrets; auto-loaded at startup if present
- `src/EmailExtractor/appsettings.json` for non-secret agent settings

## Build

```bash
dotnet build EmailExtractor.sln
```

## Test

```bash
dotnet test EmailExtractor.sln
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
SEC_USER_AGENT="EmailExtractor you@domain.com" dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- fetch-overview --in out/important_tickers.json --out out/financial_overview.jsonl
```

VIC crawl examples:

```bash
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-collect-links --url https://www.valueinvestorsclub.com/ideas/ --pages 1 --out idea_links_no_duplicates.txt
dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-crawl --links-file idea_links_no_duplicates.txt --limit 50 --out out/vic_ideas.jsonl --out-ctx out/vic_context.txt
VIC_ENABLE_LOGIN=1 VIC_USERNAME="your_user" VIC_PASSWORD="your_pass" dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- vic-crawl --limit 20
```

## Agent Config

Secrets stay in environment variables:

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_CHAT_ID`
- `OPENAI_API_KEY`

Non-secret agent runtime settings are loaded from `src/EmailExtractor/appsettings.json`:

- `Agent.OpenAiModel`
- `Agent.OpenAiMaxTokens`
- `Agent.OpenAiTemperature`
- `Agent.HeartbeatMinutes`
- `Agent.MaxContextChars`
- `Agent.MaxConversationTurns`

Optional override:

- `APPSETTINGS_PATH` to point to a different JSON file.
