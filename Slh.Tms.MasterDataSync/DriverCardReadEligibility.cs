using System.Globalization;

namespace Slh.Tms.MasterDataSync;

public static class DriverCardReadEligibility
{
    public static bool IsEligible(SharePointItem item, DateOnly ukToday)
    {
        var value = new[] { "Card Last Read", "CardLastRead" }
            .Select(name => item.Fields.TryGetValue(name, out var candidate) ? candidate : null)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!TryParseDate(value, out var lastRead)) return false;
        return lastRead >= ukToday.AddMonths(-6) && lastRead <= ukToday;
    }

    public static DateOnly UkToday(DateTimeOffset? utcNow = null)
    {
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow ?? DateTimeOffset.UtcNow, LondonTimeZone()).DateTime);
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var formats = new[] { "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy", "yyyy-MM-dd" };
        if (DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out date))
            return true;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, LondonTimeZone()).DateTime);
            return true;
        }

        return DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out date);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
