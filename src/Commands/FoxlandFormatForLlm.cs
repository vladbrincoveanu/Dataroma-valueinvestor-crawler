using System.Text.RegularExpressions;
using ValueInvestorCrawler.Lib;

namespace ValueInvestorCrawler.Commands;

public static class FoxlandFormatForLlm
{
    private sealed record EmailDoc(int Idx, string Subject, string From, string Date, string Hosted, string Body);

    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        var inTxt = a.Get("in", Env.Get("IN_TXT", "foxland_dump.txt"));
        var outCtx = a.Get("out", Env.Get("OUT_CTX", "foxland_context.txt"));
        var maxChars = a.GetInt("max-chars", Env.GetInt("MAX_CHARS", 6000));

        if (!File.Exists(inTxt)) throw new Exception($"Missing input file: {inTxt}");
        var text = File.ReadAllText(inTxt);
        var docs = ParseDump(text);
        if (docs.Count == 0) throw new Exception($"No docs parsed from: {inTxt}");

        using var f = new StreamWriter(outCtx, append: false, encoding: new System.Text.UTF8Encoding(false));
        foreach (var d in docs)
        {
            var parts = TextUtil.ChunkText(d.Body, maxChars);
            for (var pi = 0; pi < parts.Count; pi++)
            {
                var docId = $"foxland/{d.Idx:0000}";
                var header = parts.Count > 1
                    ? $"=== DOC {docId} part {pi + 1}/{parts.Count} ==="
                    : $"=== DOC {docId} ===";
                f.WriteLine(header);
                f.WriteLine($"subject: {d.Subject}");
                if (!string.IsNullOrWhiteSpace(d.From)) f.WriteLine($"from: {d.From}");
                if (!string.IsNullOrWhiteSpace(d.Date)) f.WriteLine($"date: {d.Date}");
                if (!string.IsNullOrWhiteSpace(d.Hosted)) f.WriteLine($"hosted: {d.Hosted}");
                f.WriteLine("---");
                f.WriteLine(TextUtil.NormWs(parts[pi]));
                f.WriteLine();
            }
        }

        Console.WriteLine($"Wrote {outCtx} ({new FileInfo(outCtx).Length} bytes), docs={docs.Count}, MAX_CHARS={maxChars}");
        return 0;
    }

    private static List<EmailDoc> ParseDump(string text)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        var blocks = text.Split("\n\n---\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var docs = new List<EmailDoc>();

        foreach (var b in blocks)
        {
            var lines = b.Split('\n');
            if (lines.Length == 0) continue;

            var first = lines[0].Trim();
            var m = Regex.Match(first, @"^##\s*(\d+)\.\s*(.*)\s*$");
            int idx;
            string subject;
            var i = 1;
            if (m.Success)
            {
                idx = int.Parse(m.Groups[1].Value);
                subject = (m.Groups[2].Value ?? "").Trim();
                if (subject.Length == 0) subject = "(no subject)";
            }
            else
            {
                idx = docs.Count + 1;
                subject = first.Length == 0 ? "(no subject)" : first;
            }

            var frm = "";
            var date = "";
            var hosted = "";

            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                var s = lines[i].Trim();
                if (s.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
                    frm = s.Split(':', 2)[1].Trim();
                else if (s.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                    date = s.Split(':', 2)[1].Trim();
                else if (s.StartsWith("Hosted:", StringComparison.OrdinalIgnoreCase))
                    hosted = s.Split(':', 2)[1].Trim();
                i++;
            }

            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            var body = TextUtil.NormWs(string.Join("\n", lines.Skip(i)));
            docs.Add(new EmailDoc(idx, subject, frm, date, hosted, body));
        }

        return docs;
    }
}

