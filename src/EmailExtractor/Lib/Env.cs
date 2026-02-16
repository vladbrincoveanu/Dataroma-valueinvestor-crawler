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
        return ParseUtil.ToBool(Get(key, defaultValue ? "1" : "0"), defaultValue);
    }
}
