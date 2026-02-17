namespace ValueInvestorCrawler.Lib;

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
}
