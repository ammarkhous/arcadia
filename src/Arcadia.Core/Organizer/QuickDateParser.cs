using System.Globalization;
using System.Text.RegularExpressions;

namespace Arcadia.Core.Organizer;

/// <summary>What the quick-entry parser understood.</summary>
/// <param name="Title">The text without the recognised date/time words.</param>
/// <param name="Ambiguous">A date-like fragment was seen but not interpreted (e.g. "10/12"); ask the user.</param>
public sealed record QuickDate(string Title, DateOnly? Date, TimeOnly? Time, bool Ambiguous)
{
    public bool HasDate => Date != null;
}

/// <summary>
/// Deterministic date/time shortcuts for quick entry — no AI, no guessing. Understands (English, German, Arabic):
/// yesterday / today / tomorrow / the day after tomorrow; a weekday ("Friday" = the next Friday after today); "next Friday" = Friday
/// of next week (weeks start Monday); "in 3 days", "in 2 weeks"; ISO dates (2026-10-12); "12 Oct", "Oct 12", "12.10." / "12.10.2026";
/// times "18:00", "at 9", "9am", "6:30pm", "um 18 Uhr", "الساعة 9". A time without a date means today, or tomorrow when that
/// time has already passed. Slash dates like 10/12 are ambiguous (day or month first) and are left in the text.
/// Reusable outside tasks (a future command engine).
/// </summary>
public static class QuickDateParser
{
    private static readonly string[][] WeekdayWords =
    [
        ["sunday", "sun", "sonntag", "so", "الأحد", "الاحد"],
        ["monday", "mon", "montag", "mo", "الاثنين", "الإثنين"],
        ["tuesday", "tue", "tues", "dienstag", "di", "الثلاثاء"],
        ["wednesday", "wed", "mittwoch", "mi", "الأربعاء", "الاربعاء"],
        ["thursday", "thu", "thurs", "donnerstag", "do", "الخميس"],
        ["friday", "fri", "freitag", "fr", "الجمعة"],
        ["saturday", "sat", "samstag", "sa", "السبت"],
    ];

    private static readonly string[][] MonthWords =
    [
        ["january", "jan", "januar", "jänner"], ["february", "feb", "februar"], ["march", "mar", "märz", "maerz"], ["april", "apr"],
        ["may", "mai"], ["june", "jun", "juni"], ["july", "jul", "juli"], ["august", "aug"], ["september", "sep", "sept"],
        ["october", "oct", "oktober", "okt"], ["november", "nov"], ["december", "dec", "dezember", "dez"],
    ];

    // two-letter German weekday abbreviations are too common as words ("do", "so", "mi") to stand alone
    private static readonly HashSet<string> RiskyAbbreviations = ["so", "mo", "di", "mi", "do", "fr", "sa", "sun", "sat", "mar", "may", "mai"];

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string Wd = string.Join("|", WeekdayWords.SelectMany(w => w).Where(w => !RiskyAbbreviations.Contains(w)).OrderByDescending(w => w.Length).Select(Regex.Escape));
    private static readonly string Mo = string.Join("|", MonthWords.SelectMany(w => w).OrderByDescending(w => w.Length).Select(Regex.Escape));

    private static readonly Regex Relative = new(@"(?<![\p{L}\d])(?:(?<dat>day after tomorrow|the day after tomorrow|übermorgen|uebermorgen|بعد غد|بعد الغد)|(?<tom>tomorrow|tmrw|morgen|غدا|غداً|بكرة)|(?<tod>today|tonight|heute|heute abend|اليوم|الليلة)|(?<yes>yesterday|gestern|أمس|البارحة))(?![\p{L}\d])", Opts);
    private static readonly Regex NextWeekday = new($@"(?<![\p{{L}}\d])(?:(?:next|nächsten|naechsten|nächster|kommenden)\s+(?<d>{Wd})|(?<d2>{Wd})\s+(?:القادم|القادمة))(?![\p{{L}}\d])", Opts);
    private static readonly Regex Weekday = new($@"(?<![\p{{L}}\d])(?:(?:on|am)\s+)?(?<d>{Wd})(?![\p{{L}}\d])", Opts);
    private static readonly Regex InSpan = new(@"(?<![\p{L}\d])(?:in|بعد)\s+(?<n>\d{1,3})\s+(?<u>days?|weeks?|tagen?|wochen?|أيام|يوم|يوما|أسابيع|أسبوع)(?![\p{L}\d])", Opts);
    private static readonly Regex Iso = new(@"(?<!\d)(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})(?!\d)", Opts);
    private static readonly Regex Dotted = new(@"(?<![\d.])(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4}|\d{2})?(?![\d])", Opts);
    private static readonly Regex DayMonth = new($@"(?<![\p{{L}}\d])(?:(?<d>\d{{1,2}})\.?\s+(?<mo>{Mo})\.?|(?<mo2>{Mo})\.?\s+(?<d2>\d{{1,2}})(?:st|nd|rd|th)?)(?:,?\s+(?<y>\d{{4}}))?(?![\p{{L}}\d])", Opts);
    private static readonly Regex Slash = new(@"(?<!\d)\d{1,2}/\d{1,2}(?:/\d{2,4})?(?!\d)", Opts);
    private static readonly Regex Clock = new(@"(?<![\p{L}\d:])(?:(?:at|um|@|الساعة)\s*)?(?<h>[01]?\d|2[0-3])[:.](?<m>[0-5]\d)\s*(?<ap>am|pm|a\.m\.|p\.m\.)?(?:\s*uhr)?(?![\p{L}\d])", Opts);
    private static readonly Regex AmPm = new(@"(?<![\p{L}\d:])(?:(?:at|@)\s*)?(?<h>1[0-2]|0?[1-9])\s*(?<ap>am|pm|a\.m\.|p\.m\.)(?![\p{L}\d])", Opts);
    private static readonly Regex AtHour = new(@"(?<![\p{L}\d:])(?:at|um|@|الساعة)\s*(?<h>[01]?\d|2[0-3])(?:\s*uhr)?(?![\p{L}\d:])", Opts);

    public static QuickDate Parse(string text, DateTime now)
    {
        var s = text ?? string.Empty;
        var today = DateOnly.FromDateTime(now);
        DateOnly? date = null;
        TimeOnly? time = null;

        // time first (so "12.10" is not mistaken for a time when it is a dotted date: dates are matched on what remains)
        if (TakeTime(ref s, Clock, out var clock)) time = clock;
        else if (TakeTime(ref s, AmPm, out var ampm)) time = ampm;
        else if (TakeTime(ref s, AtHour, out var at)) time = at;

        if (TakeDate(ref s, Iso, m => SafeDate(Int(m, "y"), Int(m, "m"), Int(m, "d")), out var iso)) date = iso;
        else if (TakeDate(ref s, DayMonth, m => DayMonthDate(m, today), out var dm)) date = dm;
        else if (TakeDate(ref s, Dotted, m => DottedDate(m, today), out var dot)) date = dot;
        else if (TakeDate(ref s, Relative, m => (DateOnly?)(m.Groups["dat"].Success ? today.AddDays(2) : m.Groups["tom"].Success ? today.AddDays(1) : m.Groups["yes"].Success ? today.AddDays(-1) : today), out var rel)) date = rel;
        else if (TakeDate(ref s, InSpan, m => SpanDate(m, today), out var span)) date = span;
        else if (TakeDate(ref s, NextWeekday, m => NextWeekDate(DayIndex(m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["d2"].Value), today), out var nw)) date = nw;
        else if (TakeDate(ref s, Weekday, m => ComingDate(DayIndex(m.Groups["d"].Value), today), out var wd)) date = wd;

        var ambiguous = date == null && Slash.IsMatch(s);
        if (time != null && date == null && !ambiguous) date = time.Value > TimeOnly.FromDateTime(now) ? today : today.AddDays(1);
        return new QuickDate(Tidy(s), date, time, ambiguous);
    }

    private static bool TakeTime(ref string s, Regex rx, out TimeOnly value)
    {
        foreach (Match m in rx.Matches(s))
            if (Hour(m, out var t)) { value = t; s = Cut(s, m); return true; }
        value = default;
        return false;
    }

    private static bool TakeDate(ref string s, Regex rx, Func<Match, DateOnly?> read, out DateOnly value)
    {
        foreach (Match m in rx.Matches(s))
            if (read(m) is { } v) { value = v; s = Cut(s, m); return true; }
        value = default;
        return false;
    }

    private static string Cut(string s, Match m) => s.Remove(m.Index, m.Length).Insert(m.Index, " ");

    private static int Int(Match m, string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    private static bool Hour(Match m, out TimeOnly t)
    {
        t = default;
        var h = Int(m, "h");
        var min = m.Groups["m"].Success ? Int(m, "m") : 0;
        if (m.Groups["ap"].Success)
        {
            if (h is < 1 or > 12) return false;
            var pm = m.Groups["ap"].Value.StartsWith('p') || m.Groups["ap"].Value.StartsWith('P');
            h = h % 12 + (pm ? 12 : 0);
        }
        if (h > 23 || min > 59) return false;
        t = new TimeOnly(h, min);
        return true;
    }

    private static DateOnly? SafeDate(int y, int m, int d) =>
        y is >= 1900 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateOnly(y, m, d) : null;

    /// <summary>Without a year: this year, or next year if that date has passed.</summary>
    private static DateOnly? UpcomingDate(int m, int d, DateOnly today) =>
        SafeDate(today.Year, m, d) is { } thisYear && thisYear >= today ? thisYear : SafeDate(today.Year + 1, m, d);

    private static DateOnly? DayMonthDate(Match m, DateOnly today)
    {
        var d = int.Parse(m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["d2"].Value, CultureInfo.InvariantCulture);
        var monthText = (m.Groups["mo"].Success ? m.Groups["mo"].Value : m.Groups["mo2"].Value).ToLowerInvariant();
        var month = Array.FindIndex(MonthWords, w => w.Contains(monthText)) + 1;
        if (month == 0) return null;
        return m.Groups["y"].Success ? SafeDate(Int(m, "y"), month, d) : UpcomingDate(month, d, today);
    }

    private static DateOnly? DottedDate(Match m, DateOnly today)
    {
        var d = Int(m, "d");
        var mo = Int(m, "m");
        if (!m.Groups["y"].Success) return UpcomingDate(mo, d, today);
        var y = Int(m, "y");
        return SafeDate(y < 100 ? 2000 + y : y, mo, d);
    }

    private static DateOnly? SpanDate(Match m, DateOnly today)
    {
        var n = Int(m, "n");
        var unit = m.Groups["u"].Value.ToLowerInvariant();
        var weeks = unit.StartsWith('w') || unit.Contains("أسب", StringComparison.Ordinal);
        return n is > 0 and <= 400 ? today.AddDays(weeks ? n * 7 : n) : null;
    }

    private static int DayIndex(string word)
    {
        var w = word.ToLowerInvariant();
        return Array.FindIndex(WeekdayWords, list => list.Contains(w));
    }

    /// <summary>"Friday": the next Friday strictly after today.</summary>
    private static DateOnly? ComingDate(int day, DateOnly today)
    {
        if (day < 0) return null;
        var delta = (day - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(delta == 0 ? 7 : delta);
    }

    /// <summary>"next Friday": Friday of next week (weeks start Monday).</summary>
    private static DateOnly? NextWeekDate(int day, DateOnly today)
    {
        if (day < 0) return null;
        var mondayNextWeek = today.AddDays(7 - ((int)today.DayOfWeek + 6) % 7);
        return mondayNextWeek.AddDays((day + 6) % 7);
    }

    private static readonly Regex Dangling = new(@"(?:^|\s)(?:on|at|by|due|am|um|bis|في|يوم)\s*$", Opts);
    private static readonly Regex Spaces = new(@"\s{2,}", Opts);

    private static string Tidy(string s)
    {
        s = Spaces.Replace(s, " ").Trim().Trim(',', '-', '–', '·').Trim();
        for (var i = 0; i < 2; i++) s = Dangling.Replace(s, string.Empty).Trim();
        return s;
    }
}
