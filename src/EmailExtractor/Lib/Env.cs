namespace EmailExtractor.Lib;

public static class Env
{
    public static void LoadDotEnv(string path = ".env", bool overwrite = false)
    {
        var resolvedPath = ResolveDotEnvPath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            return;

        foreach (var rawLine in File.ReadAllLines(resolvedPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            var value = line[(eq + 1)..].Trim();
            value = StripInlineComment(value);
            value = Unquote(value);

            if (!overwrite && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                continue;

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static string Get(string key, string? defaultValue = null)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        if (defaultValue is not null) return defaultValue;
        return "";
    }

    public static int GetInt(string key, int defaultValue)
    {
        return ParseUtil.ToInt(Get(key, defaultValue.ToString()), defaultValue);
    }

    public static double GetDouble(string key, double defaultValue)
    {
        return ParseUtil.ToDoubleInvariant(
            Get(key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            defaultValue
        );
    }

    public static bool GetBool(string key, bool defaultValue)
    {
        var normalized = Get(key, defaultValue ? "1" : "0").Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue,
        };
    }

    private static string StripInlineComment(string value)
    {
        if (value.Length == 0) return value;

        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            if (ch == '"' && !inSingle) inDouble = !inDouble;
            if (ch == '#' && !inSingle && !inDouble)
                return value[..i].TrimEnd();
        }

        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2) return value;

        if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            return value[1..^1];

        return value;
    }

    private static string ResolveDotEnvPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        if (Path.IsPathRooted(path))
            return path;

        var hasDir = path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar);
        if (hasDir)
            return Path.GetFullPath(path);

        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, path);
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return Path.GetFullPath(path);
    }
}
