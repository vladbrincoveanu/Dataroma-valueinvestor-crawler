namespace EmailExtractor.Lib;

public static class Env
{
    public static string Get(string key, string? defaultValue = null)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        if (defaultValue is not null) return defaultValue;
        return "";
    }

    public static int GetInt(string key, int defaultValue)
    {
        var s = Get(key, defaultValue.ToString());
        return int.TryParse(s, out var v) ? v : defaultValue;
    }

    public static double GetDouble(string key, double defaultValue)
    {
        var s = Get(key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultValue;
    }

    public static bool GetBool(string key, bool defaultValue)
    {
        var s = Get(key, defaultValue ? "1" : "0").ToLowerInvariant();
        return s is "1" or "true" or "yes" or "y" or "on";
    }
}

