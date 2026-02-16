using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EmailExtractor.Lib;

namespace EmailExtractor.Commands;

public static class DataromaRssExport
{
    private static readonly Regex TickerRe = new(@"\b[A-Z]{1,5}(?:\.[A-Z])?\b", RegexOptions.Compiled);

    public static async Task<int> Run(string[] argv)
    {
        _ = argv; // env-driven

        var rssUrl = Env.Get("DATAROMA_RSS_URL");
        if (string.IsNullOrWhiteSpace(rssUrl))
            throw new Exception("Set DATAROMA_RSS_URL in .env.");

        var outJsonl = Env.Get("DATAROMA_OUT_JSONL", "dataroma_moves.jsonl");
        var outCtx = Env.Get("DATAROMA_OUT_CTX", "dataroma_context.txt");
        var sinceDays = Env.GetInt("DATAROMA_SINCE_DAYS", 3650);
        var maxChars = Env.GetInt("DATAROMA_MAX_CHARS", 6000);
        var append = Env.GetBool("DATAROMA_APPEND", false);

        var sinceDt = DateTimeOffset.UtcNow - TimeSpan.FromDays(sinceDays);
        Console.WriteLine($"Fetching RSS: {rssUrl}");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; DataromaRssExport/1.0)");
        var xml = await http.GetStringAsync(rssUrl);
        var rssDoc = XDocument.Parse(xml);
        var items = rssDoc.Descendants().Where(x => x.Name.LocalName == "item").ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (append && File.Exists(outJsonl))
        {
            foreach (var line in File.ReadLines(outJsonl))
            {
                var s = line.Trim();
                if (s.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        var id = (idEl.GetString() ?? "").Trim();
                        if (id.Length > 0) seen.Add(id);
                    }
                }
                catch { }
            }
        }

        var moves = new List<Dictionary<string, object?>>();
        foreach (var it in items)
        {
            var title = ChildText(it, "title");
            var link = ChildText(it, "link");
            var guid = ChildText(it, "guid");
            var pubRaw = ChildText(it, "pubDate");
            var pubDt = ParsePubDate(pubRaw) ?? DateTimeOffset.UtcNow;
            if (pubDt < sinceDt) continue;

            var desc = ChildText(it, "description");

            var rawText = TextUtil.HtmlToText(desc);
            var mv = ExtractMoves(rawText);
            var publishedUtc = pubDt.ToString("o");
            var sid = StableId(guid, title, link, publishedUtc);
            if (append && seen.Contains(sid)) continue;
            if (append) seen.Add(sid);

            var bought = mv.TryGetValue("bought", out var b) ? b : new List<string>();
            var addedTo = mv.TryGetValue("added_to", out var a) ? a : new List<string>();
            var reduced = mv.TryGetValue("reduced", out var r) ? r : new List<string>();
            var soldOut = mv.TryGetValue("sold_out", out var so) ? so : new List<string>();

            moves.Add(new Dictionary<string, object?>
            {
                ["id"] = sid,
                ["investor"] = InferInvestor(title),
                ["title"] = title,
                ["link"] = link,
                ["published_utc"] = publishedUtc,
                ["bought"] = bought,
                ["added_to"] = addedTo,
                ["reduced"] = reduced,
                ["sold_out"] = soldOut,
                ["raw_text"] = rawText,
            });
        }

        moves = moves.OrderBy(m => (string)m["published_utc"]!).ToList();
        Console.WriteLine($"Parsed {moves.Count} items (since {sinceDt:yyyy-MM-dd}).");

        var jsonlMode = append && File.Exists(outJsonl) ? FileMode.Append : FileMode.Create;
        using (var fs = new FileStream(outJsonl, jsonlMode, FileAccess.Write, FileShare.Read))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            foreach (var obj in moves)
            {
                var line = JsonSerializer.Serialize(obj);
                sw.WriteLine(line);
            }
        }

        // Render context from the (new) moves only.
        using (var f = new StreamWriter(outCtx, append: append && File.Exists(outCtx), encoding: new UTF8Encoding(false)))
        {
            for (var i = 0; i < moves.Count; i++)
            {
                var obj = moves[i];
                var investor = (string?)obj["investor"] ?? "";
                var title = (string?)obj["title"] ?? "";
                var link = (string?)obj["link"] ?? "";
                var published = (string?)obj["published_utc"] ?? "";
                var raw = (string?)obj["raw_text"] ?? "";

                var body = BuildMoveBody(obj);
                var chunks = TextUtil.ChunkText(body, maxChars);
                for (var ci = 0; ci < chunks.Count; ci++)
                {
                    var header = chunks.Count > 1
                        ? $"=== DOC dataroma/{i + 1:00000} part {ci + 1}/{chunks.Count} ==="
                        : $"=== DOC dataroma/{i + 1:00000} ===";
                    f.WriteLine(header);
                    f.WriteLine($"investor: {investor}");
                    if (published.Length > 0) f.WriteLine($"date_utc: {published}");
                    if (title.Length > 0) f.WriteLine($"title: {title}");
                    if (link.Length > 0) f.WriteLine($"link: {link}");

                    // optional short metadata lines mirroring the python context
                    var bought = (List<string>)obj["bought"]!;
                    var addedTo = (List<string>)obj["added_to"]!;
                    if (bought.Count > 0) f.WriteLine($"bought: {string.Join(", ", bought)}");
                    if (addedTo.Count > 0) f.WriteLine($"added_to: {string.Join(", ", addedTo)}");

                    f.WriteLine("---");
                    f.WriteLine(TextUtil.NormWs(chunks[ci]));
                    f.WriteLine();
                }

                _ = raw; // keep parity with python JSONL; context uses derived body
            }
        }

        return 0;
    }

    private static string ChildText(XElement item, string localName)
    {
        var el = item.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        return (el?.Value ?? "").Trim();
    }

    private static DateTimeOffset? ParsePubDate(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        if (DateTimeOffset.TryParse(s, out var dt)) return dt.ToUniversalTime();
        return null;
    }

    private static string BuildMoveBody(Dictionary<string, object?> obj)
    {
        var sb = new StringBuilder();
        void Add(string label, string key)
        {
            if (obj[key] is not List<string> list || list.Count == 0) return;
            sb.AppendLine(label + ":");
            sb.AppendLine(string.Join(" ", list));
        }

        Add("Bought", "bought");
        Add("Added to", "added_to");
        Add("Reduced", "reduced");
        Add("Sold out", "sold_out");
        return sb.ToString().Trim();
    }

    private static string StableId(string guid, string title, string link, string publishedUtc)
    {
        guid = (guid ?? "").Trim();
        if (guid.Length > 0) return guid;
        var s = $"{title}\n{link}\n{publishedUtc}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string InferInvestor(string title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) return "";
        foreach (var sep in new[] { " - ", ": " })
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0) return title[..idx].Trim();
        }
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(4));
    }

    private static List<string> ParseTickers(string s)
    {
        var found = TickerRe.Matches((s ?? "").ToUpperInvariant()).Select(m => m.Value).ToList();
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in found)
            if (seen.Add(t)) outList.Add(t);
        return outList;
    }

    private static Dictionary<string, List<string>> ExtractMoves(string text)
    {
        var norm = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        var labels = new List<(string key, Regex re)>
        {
            ("bought", new Regex(@"\bBought:\s*", RegexOptions.IgnoreCase)),
            ("added_to", new Regex(@"\bAdded to:\s*", RegexOptions.IgnoreCase)),
            ("reduced", new Regex(@"\bReduced:\s*", RegexOptions.IgnoreCase)),
            ("sold_out", new Regex(@"\bSold out:\s*", RegexOptions.IgnoreCase)),
        };

        var hits = new List<(int start, string key, int len)>();
        foreach (var (key, re) in labels)
        {
            foreach (Match m in re.Matches(norm))
                hits.Add((m.Index, key, m.Length));
        }
        hits.Sort((a, b) => a.start.CompareTo(b.start));
        var moves = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (hits.Count == 0) return moves;

        for (var i = 0; i < hits.Count; i++)
        {
            var (pos, key, len) = hits[i];
            var start = pos + len;
            var end = i + 1 < hits.Count ? hits[i + 1].start : norm.Length;
            var seg = norm.Substring(start, end - start).Trim();
            seg = seg.Split("\n\n", 2)[0].Split('\n', 2)[0].Trim();
            var tickers = ParseTickers(seg);
            if (tickers.Count > 0) moves[key] = tickers;
        }

        return moves;
    }
}
