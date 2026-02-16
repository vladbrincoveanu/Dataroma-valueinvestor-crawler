using System.Text.RegularExpressions;
using System.Net;
using System.Text;

namespace EmailExtractor.Lib;

public static class TextUtil
{
    private static readonly Regex MultiBlank = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex ScriptStyleNoScript = new(@"<(script|style|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BrRepl = new(@"<(br|p|div|li|tr|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    public static string NormWs(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = MultiBlank.Replace(s, "\n\n").Trim();
        return s;
    }

    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        // Best-effort: strip scripts/styles, replace some block tags with newlines, strip remaining tags, decode entities.
        var s = ScriptStyleNoScript.Replace(html, "");
        s = BrRepl.Replace(s, "\n");
        s = TagRe.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = Ws.Replace(s, " ");
        s = MultiBlank.Replace(s, "\n\n").Trim();
        return s;
    }

    public static List<string> ChunkText(string body, int maxChars)
    {
        body = NormWs(body);
        if (body.Length <= maxChars) return [body];

        var paras = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var cur = new List<string>();
        var curLen = 0;

        void Flush()
        {
            if (cur.Count == 0) return;
            chunks.Add(string.Join("\n\n", cur).Trim());
            cur.Clear();
            curLen = 0;
        }

        foreach (var p0 in paras)
        {
            var p = p0.Trim();
            if (p.Length == 0) continue;

            if (p.Length > maxChars)
            {
                Flush();
                for (var off = 0; off < p.Length; off += maxChars)
                    chunks.Add(p.Substring(off, Math.Min(maxChars, p.Length - off)).Trim());
                continue;
            }

            var addLen = p.Length + (cur.Count > 0 ? 2 : 0);
            if (curLen + addLen > maxChars) Flush();
            cur.Add(p);
            curLen += addLen;
        }

        Flush();
        return chunks.Count > 0 ? chunks : [body.Substring(0, Math.Min(maxChars, body.Length)).Trim()];
    }

    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var targetDir = string.IsNullOrWhiteSpace(dir) ? "." : dir;
        var tempPath = Path.Combine(targetDir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content ?? string.Empty, Encoding.UTF8);

        if (File.Exists(path))
        {
            try
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch
            {
                // Fall back to move if replace is unsupported by the filesystem.
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
