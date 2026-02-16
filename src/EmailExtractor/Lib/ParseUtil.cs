namespace EmailExtractor.Lib;

public static class ParseUtil
{
    public static int ToInt(string? value, int defaultValue)
    {
        return int.TryParse(value?.Trim(), out var parsed) ? parsed : defaultValue;
    }

    public static double ToDoubleInvariant(string? value, double defaultValue)
    {
        return double.TryParse(
            value?.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : defaultValue;
    }

    public static bool ToBool(string? value, bool defaultValue)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return defaultValue;

        return normalized switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue,
        };
    }
}
