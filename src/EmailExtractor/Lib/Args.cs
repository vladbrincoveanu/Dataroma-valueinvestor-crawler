namespace EmailExtractor.Lib;

public sealed class Args
{
    private readonly Dictionary<string, List<string>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    public IReadOnlyList<string> Positional => _positional;

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (var i = 0; i < argv.Length; i++)
        {
            var s = argv[i] ?? "";
            if (!s.StartsWith("--", StringComparison.Ordinal))
            {
                a._positional.Add(s);
                continue;
            }
            var key = s[2..].Trim();
            var val = "1";
            var eq = key.IndexOf('=');
            if (eq > 0)
            {
                val = key[(eq + 1)..].Trim();
                key = key[..eq].Trim();
            }
            else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                val = argv[i + 1];
                i++;
            }
            if (!a._map.TryGetValue(key, out var list))
            {
                list = [];
                a._map[key] = list;
            }
            list.Add(val);
        }
        return a;
    }

    public string Get(string key, string defaultValue = "")
    {
        if (_map.TryGetValue(key, out var list) && list.Count > 0)
            return list[^1];
        return defaultValue;
    }

    public int GetInt(string key, int defaultValue)
    {
        return ParseUtil.ToInt(Get(key, defaultValue.ToString()), defaultValue);
    }

    public double GetDouble(string key, double defaultValue)
    {
        return ParseUtil.ToDoubleInvariant(
            Get(key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            defaultValue
        );
    }

    public bool GetBool(string key, bool defaultValue)
    {
        var normalized = Get(key, defaultValue ? "1" : "0").Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue,
        };
    }
}
