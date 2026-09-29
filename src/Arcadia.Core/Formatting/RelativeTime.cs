using Arcadia.Core.Localization;

namespace Arcadia.Core.Formatting;

/// <summary>Human friendly "2 h ago" style timestamps, localized.</summary>
public static class RelativeTime
{
    public static string Format(DateTime utc, DateTime nowUtc, ILocalizer loc)
    {
        var delta = nowUtc - utc;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1) return loc["Time_JustNow"];
        if (delta.TotalHours < 1) return loc.Format("Time_MinutesAgo", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24 && utc.Date == nowUtc.Date) return loc.Format("Time_HoursAgo", (int)delta.TotalHours);
        var days = (nowUtc.Date - utc.Date).Days;
        if (days <= 1) return loc["Time_Yesterday"];
        if (days < 7) return loc.Format("Time_DaysAgo", days);
        return utc.ToLocalTime().ToString("d MMM yyyy", loc.Culture);
    }

    public static string Hours(double hours, ILocalizer loc)
    {
        // Under an hour, minutes read better than "0.2 h" (and a 1-minute session is not "0.0 h").
        if (hours is > 0 and < 1) return loc.Format("Unit_Minutes", Math.Max(1, (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero)));
        int digits = hours is > 0 and < 10 ? 1 : 0;
        var rounded = Math.Round(hours, digits, MidpointRounding.AwayFromZero);
        return loc.Format("Unit_Hours", rounded.ToString("N" + digits, loc.Culture));
    }
}
