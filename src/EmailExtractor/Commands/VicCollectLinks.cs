using System.Text.RegularExpressions;
using EmailExtractor.Lib;

namespace EmailExtractor.Commands;

public static class VicCollectLinks
{
    private static readonly Regex IdeaHrefRe = new(
        @"href\s*=\s*[""'](?<u>(?:https?://www\.valueinvestorsclub\.com)?/idea/[^""'#\s]+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static async Task<int> Run(string[] argv)
    {
        var a = Args.Parse(argv);
        var baseUrl = a.Get("url", Env.Get("VIC_IDEAS_URL", "https://www.valueinvestorsclub.com/ideas/")).Trim();
        var outFile = a.Get("out", Env.Get("IDEA_LINKS_FILE", "idea_links_no_duplicates.txt")).Trim();
        var pages = Math.Max(1, a.GetInt("pages", Env.GetInt("VIC_IDEAS_PAGES", 1)));
        var delayMs = Math.Max(0, a.GetInt("delay-ms", Env.GetInt("VIC_DELAY_MS", 1500)));
        var ua = a.Get(
            "user-agent",
            Env.Get(
                "USER_AGENT",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            )
        );

        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
                | System.Net.DecompressionMethods.Brotli,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();

        for (var page = 1; page <= pages; page++)
        {
            var url = BuildIdeasUrl(baseUrl, page);
            Console.WriteLine($"Fetching ideas page {page}/{pages}: {url}");
            var html = await http.GetStringAsync(url);
            foreach (var link in ExtractIdeaLinks(html))
            {
                if (seen.Add(link))
                    links.Add(link);
            }

            if (delayMs > 0 && page < pages)
                await Task.Delay(delayMs);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)) ?? ".");
        await File.WriteAllLinesAsync(outFile, links);
        Console.WriteLine($"Saved {links.Count} unique idea links -> {outFile}");
        return 0;
    }

    private static string BuildIdeasUrl(string baseUrl, int page)
    {
        if (page <= 1) return baseUrl;
        if (baseUrl.Contains("{page}", StringComparison.OrdinalIgnoreCase))
            return baseUrl.Replace("{page}", page.ToString(), StringComparison.OrdinalIgnoreCase);

        var sep = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{sep}p={page}";
    }

    private static IEnumerable<string> ExtractIdeaLinks(string html)
    {
        var outLinks = new List<string>();
        foreach (Match m in IdeaHrefRe.Matches(html ?? ""))
        {
            var raw = (m.Groups["u"].Value ?? "").Trim();
            if (raw.Length == 0) continue;
            var abs = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "https://www.valueinvestorsclub.com" + raw;
            abs = abs.Split("/messages", 2, StringSplitOptions.None)[0];
            outLinks.Add(abs);
        }
        return outLinks;
    }
}
