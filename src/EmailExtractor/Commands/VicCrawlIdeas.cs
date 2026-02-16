using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmailExtractor.Lib;

namespace EmailExtractor.Commands;

public static class VicCrawlIdeas
{
    private static readonly Regex TokenRe = new(
        @"name\s*=\s*[""']_token[""'][^>]*value\s*=\s*[""'](?<v>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex TitleNameTickerRe = new(
        @"<title[^>]*>[\s\S]*?/\s*(?<company>.*?)\s*\((?<ticker>[^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex IdeaIdRe = new(@"/idea/[^/]+/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateByRe = new(
        @"class\s*=\s*[""'][^""']*\bidea_by\b[^""']*[""'][\s\S]{0,2000}?(?<date>[A-Za-z]+\s+\d{1,2},\s+\d{4}(?:\s*-\s*[^<\n]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex UserRe = new(
        @"class\s*=\s*[""'][^""']*\bidea_by\b[^""']*[""'][\s\S]{0,2500}?<a[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<user>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex SectionRe = new(
        @"<h4[^>]*>\s*(?<hdr>Description|Catalyst|Variant View|Comments?)\s*</h4>(?<body>[\s\S]*?)(?=<h4\b|</div>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex MetaDescRe = new(
        @"<meta[^>]*name\s*=\s*[""']description[""'][^>]*content\s*=\s*[""'](?<v>[^""']*)[""'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static async Task<int> Run(string[] argv)
    {
        var a = Args.Parse(argv);

        var linkFile = a.Get("links-file", Env.Get("IDEA_LINKS_FILE", "idea_links_no_duplicates.txt")).Trim();
        var outJsonl = a.Get("out", Env.Get("VIC_OUT_JSONL", "out/vic_ideas.jsonl")).Trim();
        var outCtx = a.Get("out-ctx", Env.Get("VIC_OUT_CTX", "out/vic_context.txt")).Trim();
        var start = Math.Max(0, a.GetInt("start", Env.GetInt("IDEA_LINKS_START", 0)));
        var limit = Math.Max(0, a.GetInt("limit", Env.GetInt("IDEA_LINKS_LIMIT", 0)));
        var mode = a.Get("mode", Env.Get("IDEA_LINKS_MODE", "tail")).Trim().ToLowerInvariant();
        var delayMs = Math.Max(0, a.GetInt("delay-ms", Env.GetInt("VIC_DELAY_MS", 1500)));
        var append = a.GetBool("append", Env.GetBool("VIC_APPEND", false));
        var enableLogin = a.GetBool("login", Env.GetBool("VIC_ENABLE_LOGIN", false));
        var loginUser = a.Get("user", Env.Get("VIC_USERNAME", "")).Trim();
        var loginPass = a.Get("pass", Env.Get("VIC_PASSWORD", "")).Trim();
        var testUrl = a.Get("login-test-url", Env.Get("LOGIN_TEST_URL", "https://www.valueinvestorsclub.com/idea/InPost/5698302853")).Trim();
        var ua = a.Get(
            "user-agent",
            Env.Get(
                "USER_AGENT",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            )
        );
        var maxChars = Math.Max(500, a.GetInt("max-chars", Env.GetInt("VIC_MAX_CHARS", 6000)));

        var links = SelectLinks(LoadLinks(linkFile), start, limit, mode);
        Console.WriteLine($"Loaded {links.Count} links from {linkFile} (start={start}, limit={limit}, mode={mode}).");
        if (links.Count == 0) return 0;

        var cookieJar = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookieJar,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        if (enableLogin)
        {
            if (loginUser.Length == 0 || loginPass.Length == 0)
                throw new Exception("VIC login enabled but VIC_USERNAME/VIC_PASSWORD (or --user/--pass) are missing.");
            await LoginAsync(http, loginUser, loginPass, testUrl);
        }

        var fsMode = append && File.Exists(outJsonl) ? FileMode.Append : FileMode.Create;
        var ctxAppend = append && File.Exists(outCtx);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outJsonl)) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCtx)) ?? ".");

        using var jsonFs = new FileStream(outJsonl, fsMode, FileAccess.Write, FileShare.Read);
        using var jsonSw = new StreamWriter(jsonFs, new UTF8Encoding(false));
        using var ctxSw = new StreamWriter(outCtx, append: ctxAppend, encoding: new UTF8Encoding(false));

        var saved = 0;
        for (var i = 0; i < links.Count; i++)
        {
            var url = links[i];
            Console.WriteLine($"[{i + 1}/{links.Count}] {url}");
            string html;
            try
            {
                html = await http.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn: fetch failed for {url}: {ex.Message}");
                continue;
            }

            var item = ParseIdea(html, url);
            if (item is null)
            {
                Console.Error.WriteLine($"warn: skipped (missing core fields): {url}");
                continue;
            }

            var line = JsonSerializer.Serialize(item);
            await jsonSw.WriteLineAsync(line);

            var body = BuildBody(item);
            var chunks = TextUtil.ChunkText(body, maxChars);
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                var head = chunks.Count > 1
                    ? $"=== DOC vic/{saved + 1:00000} part {ci + 1}/{chunks.Count} ==="
                    : $"=== DOC vic/{saved + 1:00000} ===";
                await ctxSw.WriteLineAsync(head);
                await ctxSw.WriteLineAsync($"idea_id: {item.IdeaId}");
                await ctxSw.WriteLineAsync($"ticker: {item.Ticker}");
                await ctxSw.WriteLineAsync($"company: {item.CompanyName}");
                await ctxSw.WriteLineAsync($"date: {item.Date}");
                if (!string.IsNullOrWhiteSpace(item.DateIso)) await ctxSw.WriteLineAsync($"date_iso: {item.DateIso}");
                await ctxSw.WriteLineAsync($"username: {item.Username}");
                if (!string.IsNullOrWhiteSpace(item.UserLink)) await ctxSw.WriteLineAsync($"user_link: {item.UserLink}");
                await ctxSw.WriteLineAsync($"link: {item.Link}");
                await ctxSw.WriteLineAsync($"is_short: {item.IsShort}");
                await ctxSw.WriteLineAsync($"is_contest_winner: {item.IsContestWinner}");
                await ctxSw.WriteLineAsync("---");
                await ctxSw.WriteLineAsync(chunks[ci]);
                await ctxSw.WriteLineAsync();
            }

            saved++;
            if (delayMs > 0 && i + 1 < links.Count)
                await Task.Delay(delayMs);
        }

        Console.WriteLine($"Saved {saved} ideas -> {outJsonl}");
        return 0;
    }

    private static List<string> LoadLinks(string path)
    {
        if (!File.Exists(path))
            throw new Exception($"Links file not found: {path}");
        var outLinks = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) continue;
            s = s.Split("/messages", 2, StringSplitOptions.None)[0];
            if (seen.Add(s)) outLinks.Add(s);
        }
        return outLinks;
    }

    private static List<string> SelectLinks(List<string> links, int start, int limit, string mode)
    {
        if (start < 0) start = 0;
        if (start > links.Count) start = 0;

        var selected = links.Skip(start).ToList();
        if (limit > 0)
        {
            if (mode == "tail" && start == 0)
                selected = selected.TakeLast(limit).ToList();
            else
                selected = selected.Take(limit).ToList();
        }
        return selected;
    }

    private static async Task LoginAsync(HttpClient http, string user, string pass, string testUrl)
    {
        var loginUrl = "https://www.valueinvestorsclub.com/login";
        Console.WriteLine($"Login: GET {loginUrl}");
        var loginHtml = await http.GetStringAsync(loginUrl);
        var token = TokenRe.Match(loginHtml).Groups["v"].Value.Trim();
        if (token.Length == 0)
            throw new Exception("VIC login token (_token) not found.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_token"] = token,
            ["login[login_name]"] = user,
            ["login[password]"] = pass,
            ["login[remember_me]"] = "1",
        });

        Console.WriteLine("Login: POST credentials");
        using var resp = await http.PostAsync(loginUrl, form);
        var body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode >= 400)
            throw new Exception($"VIC login failed with status {(int)resp.StatusCode}.");
        if ((resp.RequestMessage?.RequestUri?.AbsolutePath ?? "").Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            var lowered = (body ?? "").ToLowerInvariant();
            if (lowered.Contains("invalid", StringComparison.Ordinal)
                || lowered.Contains("incorrect", StringComparison.Ordinal)
                || lowered.Contains("captcha", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("warn: login response suggests failure; continuing with best-effort crawl.");
            }
        }

        Console.WriteLine($"Login: verifying with {testUrl}");
        var testHtml = await http.GetStringAsync(testUrl);
        if (IsGated(testHtml))
            Console.Error.WriteLine("warn: login appears ineffective; continuing with teaser-mode crawl.");
    }

    private static bool IsGated(string html)
    {
        var t = (html ?? "").ToLowerInvariant();
        return t.Contains("sign up or log in", StringComparison.Ordinal)
               || t.Contains("already have an account", StringComparison.Ordinal)
               || t.Contains("log in to read the full report", StringComparison.Ordinal);
    }

    private static VicIdea? ParseIdea(string html, string url)
    {
        var titleMatch = TitleNameTickerRe.Match(html ?? "");
        var company = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups["company"].Value).Trim() : "";
        var ticker = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups["ticker"].Value).Trim() : "";

        var date = "";
        var by = DateByRe.Match(html ?? "");
        if (by.Success) date = WebUtility.HtmlDecode(by.Groups["date"].Value).Trim();

        var user = "";
        var userLink = "";
        var um = UserRe.Match(html ?? "");
        if (um.Success)
        {
            user = WebUtility.HtmlDecode(um.Groups["user"].Value).Trim();
            userLink = WebUtility.HtmlDecode(um.Groups["href"].Value).Trim();
            if (userLink.StartsWith("/", StringComparison.Ordinal))
                userLink = "https://www.valueinvestorsclub.com" + userLink;
        }

        var description = "";
        var catalyst = "";
        var comments = "";
        foreach (Match m in SectionRe.Matches(html ?? ""))
        {
            var h = (m.Groups["hdr"].Value ?? "").Trim().ToLowerInvariant();
            var body = CleanVicText(TextUtil.HtmlToText(m.Groups["body"].Value ?? ""));
            if (h == "description" && description.Length == 0) description = body;
            if (h == "catalyst" && catalyst.Length == 0) catalyst = body;
            if ((h == "variant view" || h.StartsWith("comment", StringComparison.Ordinal)) && comments.Length == 0) comments = body;
        }

        if (description.Length == 0)
        {
            var meta = MetaDescRe.Match(html ?? "");
            if (meta.Success)
            {
                var d = WebUtility.HtmlDecode(meta.Groups["v"].Value).Trim();
                if (d.StartsWith("Investment thesis for", StringComparison.OrdinalIgnoreCase))
                    description = d;
            }
        }

        if (company.Length == 0 || ticker.Length == 0 || date.Length == 0)
            return null;

        var dateIso = ToIsoDate(date);
        var ideaId = IdeaIdFromUrl(url);
        var isShort = (html ?? "").Contains("label-short", StringComparison.OrdinalIgnoreCase);
        var isContestWinner = (html ?? "").Contains("label-success", StringComparison.OrdinalIgnoreCase);
        var gated = IsGated(html ?? "");

        return new VicIdea(
            ideaId,
            url,
            ticker,
            company,
            date,
            dateIso,
            user,
            userLink,
            isShort,
            isContestWinner,
            description,
            catalyst,
            comments,
            gated,
            StableId(url, dateIso, ticker)
        );
    }

    private static string IdeaIdFromUrl(string url)
    {
        var m = IdeaIdRe.Match(url ?? "");
        return m.Success ? m.Groups["id"].Value : "";
    }

    private static string ToIsoDate(string dateText)
    {
        var s = (dateText ?? "").Trim();
        var cut = s.IndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut].Trim();
        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return "";
    }

    private static string CleanVicText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var banned = new[]
        {
            "sign up or log in",
            "already have an account",
            "log in to read the full report",
            "to read the full report",
            "read the full report",
            "guest access",
            "45-day delay",
            "read full reports",
            "read full report",
            "log in",
            "signup",
            "sign up",
            "favorites",
            "report abuse",
        };

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant();
            if (banned.Any(p => lower.Contains(p, StringComparison.Ordinal))) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    private static string BuildBody(VicIdea x)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(x.Description))
        {
            sb.AppendLine("Description:");
            sb.AppendLine(x.Description.Trim());
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(x.Catalysts))
        {
            sb.AppendLine("Catalyst:");
            sb.AppendLine(x.Catalysts.Trim());
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(x.Comments))
        {
            sb.AppendLine("Comments:");
            sb.AppendLine(x.Comments.Trim());
            sb.AppendLine();
        }
        if (x.IsGated)
        {
            sb.AppendLine("Note:");
            sb.AppendLine("Page appears login-gated; content may be teaser-only.");
        }
        return sb.ToString().Trim();
    }

    private static string StableId(string url, string dateIso, string ticker)
    {
        var s = $"{url}\n{dateIso}\n{ticker}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record VicIdea(
        string IdeaId,
        string Link,
        string Ticker,
        string CompanyName,
        string Date,
        string DateIso,
        string Username,
        string UserLink,
        bool IsShort,
        bool IsContestWinner,
        string Description,
        string Catalysts,
        string Comments,
        bool IsGated,
        string Id
    );
}
