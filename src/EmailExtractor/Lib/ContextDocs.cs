using System.Text.RegularExpressions;

namespace EmailExtractor.Lib;

public sealed record ContextDoc(string DocId, Dictionary<string, string> Headers, string Body);

public static class ContextDocs
{
    private static readonly Regex DocSplit = new(@"^=== DOC (.+?) ===\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<ContextDoc> Load(string path)
    {
        var txt = File.ReadAllText(path);
        return Parse(txt);
    }

    public static List<ContextDoc> Parse(string text)
    {
        var outDocs = new List<ContextDoc>();
        if (string.IsNullOrWhiteSpace(text)) return outDocs;

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var matches = DocSplit.Matches(text).Cast<Match>().ToList();
        if (matches.Count == 0) return outDocs;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var docId = (m.Groups[1].Value ?? "").Trim();
            var start = m.Index + m.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var block = text.Substring(start, end - start).Trim('\n');

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var body = "";

            var parts = block.Split("\n---\n", 2, StringSplitOptions.None);
            var headerPart = parts[0];
            if (parts.Length > 1) body = parts[1].Trim();

            foreach (var line0 in headerPart.Split('\n'))
            {
                var line = line0.Trim();
                if (line.Length == 0) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var k = line.Substring(0, colon).Trim().ToLowerInvariant();
                var v = line.Substring(colon + 1).Trim();
                if (k.Length > 0) headers[k] = v;
            }

            outDocs.Add(new ContextDoc(docId, headers, body));
        }

        return outDocs;
    }
}

