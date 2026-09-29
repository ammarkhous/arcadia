using System.Globalization;
using Arcadia.Core.Finance;

namespace Arcadia.Core.Oracle;

/// <summary>A date range the user named ("this month", "next Friday", "within 7 days"). Both ends inclusive.</summary>
public sealed record OraclePeriod(DateOnly From, DateOnly To, string Key)
{
    public bool IsSingleDay => From == To;
}

/// <summary>
/// The structured reading of one command: concepts found (verb, entities, filters, module, tool, metric), a period,
/// money, host/port, and the words that were not vocabulary — split into subject, target ("for Home Server") and topic
/// ("about router"). Original spelling is kept for all free text and the payload.
/// </summary>
public sealed class OracleParse
{
    public required OracleInput Input { get; init; }
    public OracleLanguage Language { get; init; }
    public List<string> Concepts { get; } = [];
    public string? Verb { get; set; }
    public string? Question { get; set; }
    /// <summary>Entity words in the order they appear ("tasks for project X" → Task, Project).</summary>
    public List<OracleEntity> Entities { get; } = [];
    public HashSet<string> Filters { get; } = [];
    public string? Module { get; set; }
    public string? Tool { get; set; }
    public string? Metric { get; set; }
    public OraclePeriod? Period { get; set; }
    /// <summary>A slash date that reads differently by locale ("03/04"): both readings, for the user to choose.</summary>
    public (DateOnly DayFirst, DateOnly MonthFirst)? AmbiguousDate { get; set; }
    public TimeOnly? Time { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? AmountText { get; set; }
    public decimal? Percent { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public List<int> Numbers { get; } = [];
    public string? Payload => Input.Payload;
    /// <summary>Each run of free words between vocabulary ("for <b>coffee</b> from <b>Visa</b>" → coffee, Visa).</summary>
    public List<string> Segments { get; } = [];
    /// <summary>Free words not preceded by a preposition ("open <b>cyberpunk</b>").</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>Free words after for/to/in/related ("tasks for <b>Home Server</b>"), with the entity word that came with them.</summary>
    public string? Target { get; set; }
    public OracleEntity TargetKind { get; set; }
    /// <summary>Free words after about/tagged ("notes about <b>router</b>").</summary>
    public string? Topic { get; set; }
    public bool Tagged { get; set; }
    public bool RefersBack { get; set; }
    public bool AllRelated { get; set; }
    public bool IsFollowUp { get; set; }
    /// <summary>Free words that were not vocabulary (a measure of how much Oracle did not understand).</summary>
    public int FreeWordCount { get; set; }
    public int VocabularyHits { get; set; }
    public bool UsedFuzzyVocabulary { get; set; }

    public OracleEntity Entity => Entities.Count > 0 ? Entities[0] : OracleEntity.None;
    public bool Has(string concept) => Concepts.Contains(concept);
    public bool HasFilter(string f) => Filters.Contains(f);
    public string SubjectOrTarget => Subject.Length > 0 ? Subject : Target ?? string.Empty;

    /// <summary>Everything the user typed except vocabulary: used to read dates and titles for new items.</summary>
    public string FreeTextWithDates { get; set; } = string.Empty;

    public string Describe() =>
        string.Join("; ", new[]
        {
            Subject.Length > 0 ? $"subject=\"{Subject}\"" : null,
            Target != null ? $"target=\"{Target}\"({TargetKind})" : null,
            Topic != null ? $"topic=\"{Topic}\"" : null,
            Filters.Count > 0 ? "filters=" + string.Join(',', Filters) : null,
            Period != null ? $"period={Period.Key}:{Period.From:yyyy-MM-dd}..{Period.To:yyyy-MM-dd}" : null,
            Time != null ? $"time={Time:HH\\:mm}" : null,
            Amount != null ? $"amount={Amount} {Currency}" : null,
            Host != null ? $"host={Host}" : null,
            Port != null ? $"port={Port}" : null,
            Payload != null ? $"payload=\"{Payload}\"" : null,
            RefersBack ? "ref=it" : null,
        }.Where(s => s != null));
}

/// <summary>Stages 3–6: vocabulary, entities, parameters, dates. Pure and deterministic.</summary>
public static class OracleParser
{
    private static readonly HashSet<string> FileExtensions =
        ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "csv", "json", "xml", "jpg", "jpeg", "png", "gif", "zip", "7z", "rar", "exe", "msi", "iso", "mp3", "mp4", "mkv", "log", "ini", "cfg", "ps1", "bat", "odt", "rtf"];

    private static readonly string[] Weekdays = ["day:sunday", "day:monday", "day:tuesday", "day:wednesday", "day:thursday", "day:friday", "day:saturday"];

    public static OracleParse Parse(string text, DateOnly today, OracleLanguage uiLanguage, OracleLanguage? forced = null, OracleLexicon? lexicon = null)
    {
        lexicon ??= OracleLexicon.Default;
        var input = OracleText.Parse(text);
        var language = forced ?? lexicon.Detect(input.Tokens, uiLanguage);
        var p = new OracleParse { Input = input, Language = language };
        var hits = lexicon.Match(input.Tokens, language);
        var consumed = new bool[input.Tokens.Count];
        var roles = new string?[input.Tokens.Count]; // the concept that consumed each token (prep:…, entity:…)

        var covered = new bool[input.Tokens.Count];
        foreach (var hit in hits) for (var k = hit.Start; k < hit.End; k++) covered[k] = true;
        for (var h = 0; h < hits.Count; h++)
        {
            var hit = hits[h];
            var next = h + 1 < hits.Count && hits[h + 1].Start == hit.End && covered.Skip(hits[h + 1].End).All(x => x) ? hits[h + 1] : null;
            var concepts = Choose(hit, p, next);
            p.VocabularyHits++;
            if (hit.Fuzzy) p.UsedFuzzyVocabulary = true;
            for (var k = hit.Start; k < hit.End; k++) { consumed[k] = true; roles[k] = concepts.FirstOrDefault(); }
            foreach (var c in concepts) Apply(p, c, hit, input.Tokens, consumed, roles, today);
        }

        ReadPatterns(p, input.Tokens, consumed, roles, today);
        SplitFreeText(p, input.Tokens, consumed, roles);
        return p;
    }

    /// <summary>Picks the meaning of an ambiguous phrase: a verb at the start ("open games"), otherwise the other meaning ("open tasks").</summary>
    private static IReadOnlyList<string> Choose(OracleLexHit hit, OracleParse p, OracleLexHit? next)
    {
        if (hit.Alternatives.Count == 1) return hit.Alternatives[0];
        var verbAlt = hit.Alternatives.FirstOrDefault(a => a.Any(c => c.StartsWith("verb:", StringComparison.Ordinal)));
        var other = hit.Alternatives.FirstOrDefault(a => !a.Any(c => c.StartsWith("verb:", StringComparison.Ordinal)));
        // "favorite games", "done tasks": a word that could be a verb but directly qualifies an entity is a filter ("open" stays a verb)
        var qualifiesEntity = next != null && next.Alternatives.Any(a => a.Any(c => c.StartsWith("entity:", StringComparison.Ordinal)))
                              && verbAlt != null && !verbAlt.Contains("verb:open") && other != null && other.Any(c => c.StartsWith("filter:", StringComparison.Ordinal));
        if (verbAlt != null && p.Verb == null && p.Entities.Count == 0 && p.Filters.Count == 0 && !qualifiesEntity) return verbAlt;
        return other ?? hit.Alternatives[0];
    }

    private static void Apply(OracleParse p, string concept, OracleLexHit hit, IReadOnlyList<OracleToken> tokens, bool[] consumed, string?[] roles, DateOnly today)
    {
        p.Concepts.Add(concept);
        var colon = concept.IndexOf(':');
        var kind = colon > 0 ? concept[..colon] : concept;
        var value = colon > 0 ? concept[(colon + 1)..] : string.Empty;
        switch (kind)
        {
            case "verb": p.Verb ??= value; break;
            case "q": p.Question ??= value; break;
            case "entity":
                var e = EntityOf(value);
                if (e != OracleEntity.None && !p.Entities.Contains(e)) p.Entities.Add(e);
                break;
            case "filter": p.Filters.Add(value); break;
            case "module": p.Module ??= value; break;
            case "tool": p.Tool ??= value; break;
            case "metric": p.Metric ??= value; break;
            case "period": p.Period ??= PeriodOf(value, today); break;
            case "ref" when value == "it": p.RefersBack = true; break;
            case "ref" when value == "all": p.AllRelated = true; break;
            case "prep" when value == "related": p.AllRelated = true; break;
            case "prep" when value == "tagged": p.Tagged = true; break;
            case "followup": p.IsFollowUp = true; break;
            case "cur": p.Currency ??= value; break;
        }
    }

    public static OracleEntity EntityOf(string value) => value switch
    {
        "game" => OracleEntity.Game, "project" => OracleEntity.Project, "task" => OracleEntity.Task, "note" => OracleEntity.Note,
        "document" => OracleEntity.Document, "trip" => OracleEntity.Trip, "place" => OracleEntity.Place, "event" => OracleEntity.Event,
        "account" => OracleEntity.Account, "transaction" => OracleEntity.Transaction, "budget" => OracleEntity.Budget,
        "subscription" => OracleEntity.Subscription, "bill" => OracleEntity.Bill, "goal" => OracleEntity.Goal, "session" => OracleEntity.Session,
        "screenshot" => OracleEntity.Screenshot, "journal" => OracleEntity.Journal, "bucket" => OracleEntity.Bucket, "activity" => OracleEntity.Activity,
        "category" => OracleEntity.Category, "automation" => OracleEntity.Automation,
        _ => OracleEntity.None,
    };

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static OraclePeriod PeriodOf(string key, DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        return key switch
        {
            "today" => new(today, today, key),
            "tomorrow" => new(today.AddDays(1), today.AddDays(1), key),
            "yesterday" => new(today.AddDays(-1), today.AddDays(-1), key),
            "thisweek" => new(MondayOf(today), MondayOf(today).AddDays(6), key),
            "nextweek" => new(MondayOf(today).AddDays(7), MondayOf(today).AddDays(13), key),
            "lastweek" => new(MondayOf(today).AddDays(-7), MondayOf(today).AddDays(-1), key),
            "weekend" => new(MondayOf(today).AddDays(5), MondayOf(today).AddDays(6), key),
            "thismonth" => new(month, month.AddMonths(1).AddDays(-1), key),
            "nextmonth" => new(month.AddMonths(1), month.AddMonths(2).AddDays(-1), key),
            "lastmonth" => new(month.AddMonths(-1), month.AddDays(-1), key),
            "thisyear" => new(new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31), key),
            "nextyear" => new(new DateOnly(today.Year + 1, 1, 1), new DateOnly(today.Year + 1, 12, 31), key),
            "lastyear" => new(new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31), key),
            _ => new(today, today, "today"),
        };
    }

    /// <summary>Weekdays with next/last, "in/within N days|weeks|months", explicit dates, times, money, hosts and ports.</summary>
    private static void ReadPatterns(OracleParse p, IReadOnlyList<OracleToken> tokens, bool[] consumed, string?[] roles, DateOnly today)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var role = roles[i];
            // weekday, optionally with next / last before it
            if (role != null && Array.IndexOf(Weekdays, role) is var wd and >= 0 && p.Period == null)
            {
                var mod = i > 0 ? roles[i - 1] : null;
                DateOnly date;
                if (mod is "mod:next" or "filter:upcoming")
                {
                    var mondayNext = MondayOf(today).AddDays(7);
                    date = mondayNext.AddDays((wd + 6) % 7);
                    p.Filters.Remove("upcoming");
                }
                else if (mod == "mod:last")
                {
                    var delta = ((int)today.DayOfWeek - wd + 7) % 7;
                    date = today.AddDays(-(delta == 0 ? 7 : delta));
                }
                else
                {
                    var delta = (wd - (int)today.DayOfWeek + 7) % 7;
                    date = today.AddDays(delta); // "Friday" = this coming Friday (today when it is Friday)
                }
                p.Period = new OraclePeriod(date, date, "day");
                continue;
            }
            // an explicit year after a time word: "in 2024", "during 2025", "im Jahr 2024", "في 2024", "عام 2023"
            if (tokens[i].Kind == OracleTokenKind.Number && tokens[i].Text.Length == 4 && p.Period == null
                && int.TryParse(tokens[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1950 and <= 2100 && i > 0
                && tokens[i - 1].Norm is "in" or "during" or "im" or "jahr" or "في" or "عام" or "سنه" or "خلال")
            {
                p.Period = new OraclePeriod(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), "year");
                consumed[i] = consumed[i - 1] = true;
                if (i > 1 && tokens[i - 2].Norm is "im" or "في") consumed[i - 2] = true;
                continue;
            }
            // in / within N days|weeks|months, or a bare "7 days"
            if (tokens[i].Kind == OracleTokenKind.Number && int.TryParse(tokens[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 400
                && i + 1 < tokens.Count && roles[i + 1] is "unit:day" or "unit:week" or "unit:month")
            {
                var unit = roles[i + 1];
                var before = i > 0 ? roles[i - 1] : null;
                var end = unit switch { "unit:week" => today.AddDays(7 * n), "unit:month" => today.AddMonths(n), _ => today.AddDays(n) };
                p.Period ??= before is "time:in" or "prep:for" && unit == "unit:day" && tokens[i - 1].Norm is "in"
                    ? new OraclePeriod(end, end, "in")
                    : new OraclePeriod(today, end, "within");
                consumed[i] = true;
                if (i > 0 && before is "time:in" or "time:within" or "prep:for") consumed[i - 1] = true;
                continue;
            }
            if (consumed[i]) continue;
            var t = tokens[i];
            // "at 10", "um 18 (Uhr)", "الساعة 9": a bare hour after a time word
            if (t.Kind == OracleTokenKind.Word && t.Norm is "at" or "um" or "الساعه" or "@" && i + 1 < tokens.Count && !consumed[i + 1]
                && tokens[i + 1].Kind == OracleTokenKind.Number && int.TryParse(tokens[i + 1].Text, out var hour) && hour is >= 0 and <= 23)
            {
                p.Time ??= new TimeOnly(hour, 0);
                consumed[i] = consumed[i + 1] = true;
                if (i + 2 < tokens.Count && tokens[i + 2].Norm == "uhr") consumed[i + 2] = true;
                i++;
                continue;
            }
            if (t.Kind == OracleTokenKind.Word && t.Norm is "at" or "um" && i + 1 < tokens.Count && tokens[i + 1].Kind == OracleTokenKind.Time)
            {
                consumed[i] = true;
                continue;
            }
            switch (t.Kind)
            {
                case OracleTokenKind.Date:
                    ReadDate(p, t.Text, today);
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Time when t.Text.Contains('.') && !t.Text.Contains(':')
                                               && ((i + 1 < tokens.Count && roles[i + 1]?.StartsWith("cur:", StringComparison.Ordinal) == true) || p.Metric == "spending" || p.Verb == "create" && p.Entity == OracleEntity.Transaction):
                    ReadMoney(p, t.Text);
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Time:
                    if (TimeOnly.TryParse(t.Text.Replace('.', ':'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) p.Time ??= time;
                    else if (DateTime.TryParseExact(t.Text.ToLowerInvariant(), ["htt", "hhtt"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) p.Time ??= TimeOnly.FromDateTime(dt);
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Host when FileExtensions.Contains(t.Norm[(t.Norm.LastIndexOf('.') + 1)..]):
                    break; // "invoice.pdf" is a file name, not a host
                case OracleTokenKind.Ip or OracleTokenKind.Host:
                    p.Host ??= t.Text;
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Money:
                    ReadMoney(p, t.Text);
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Percent:
                    p.Percent = decimal.Parse(t.Text[..^1], NumberStyles.Number, CultureInfo.InvariantCulture);
                    consumed[i] = true;
                    break;
                case OracleTokenKind.Number:
                    // a number with a currency word ("35 euro") is money; after a host it is a port
                    if (i + 1 < tokens.Count && roles[i + 1]?.StartsWith("cur:", StringComparison.Ordinal) == true)
                    {
                        ReadMoney(p, t.Text);
                        consumed[i] = true;
                    }
                    else if (i > 0 && roles[i - 1] == "tool:port" && int.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var pp) && pp is > 0 and <= 65535)
                    {
                        p.Port ??= pp;
                        consumed[i] = true;
                    }
                    else if (p.Host != null && int.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 && p.Port == null)
                    {
                        p.Port = port;
                        consumed[i] = true;
                    }
                    else if (int.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var num)) p.Numbers.Add(num);
                    break;
                case OracleTokenKind.Word when t.Norm == "localhost":
                    p.Host ??= t.Text;
                    consumed[i] = true;
                    break;
            }
        }
        // "port 443 on host" style: a port number given before the host
        if (p.Host != null && p.Port == null && p.Tool == "port" && p.Numbers.Count > 0 && p.Numbers[0] is > 0 and <= 65535) p.Port = p.Numbers[0];
        // a number right after a money-less "spent" is not money: leave it to the handler
    }

    private static void ReadDate(OracleParse p, string text, DateOnly today)
    {
        var t = text.TrimEnd('.');
        if (DateOnly.TryParseExact(t, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) { p.Period ??= new(iso, iso, "day"); return; }
        var parts = t.Split('.', '/');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return;
        var year = parts.Length > 2 && int.TryParse(parts[2], out var y) ? (y < 100 ? 2000 + y : y) : today.Year;
        DateOnly? Make(int day, int month) => month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
        if (text.Contains('.'))
        {
            if (Make(a, b) is { } d) p.Period ??= new(d, d, "day"); // dotted dates are day-first (German)
            return;
        }
        var dayFirst = Make(a, b);
        var monthFirst = Make(b, a);
        if (dayFirst is { } df && monthFirst is { } mf && df != mf) p.AmbiguousDate = (df, mf);
        else if ((dayFirst ?? monthFirst) is { } only) p.Period ??= new(only, only, "day");
    }

    private static void ReadMoney(OracleParse p, string text)
    {
        if (AmountParser.TryParse(text, null, out var v) == AmountParser.Outcome.Ok && v > 0)
        {
            p.Amount ??= v;
            p.AmountText ??= text;
            p.Currency ??= Finance.Currency.Detect(text);
        }
    }

    /// <summary>Splits the words that were not vocabulary into subject, target (after for/to/in) and topic (after about/tagged).</summary>
    private static void SplitFreeText(OracleParse p, IReadOnlyList<OracleToken> tokens, bool[] consumed, string?[] roles)
    {
        var subject = new List<string>();
        var free = new List<string>();
        List<string>? current = subject;
        string? mode = null;
        var targetKind = OracleEntity.None;
        var target = new List<string>();
        var topic = new List<string>();
        var extra = new List<string>();
        var segment = new List<string>();
        var pendingNoise = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var role = roles[i];
            // in a topic ("notes tagged network", "about home network") vocabulary words are the topic's words
            if (consumed[i] && mode == "topic" && role != null && (role.StartsWith("tool:", StringComparison.Ordinal) || role.StartsWith("module:", StringComparison.Ordinal)
                || role.StartsWith("metric:", StringComparison.Ordinal) || role.StartsWith("filter:", StringComparison.Ordinal)))
            {
                var value = role[(role.IndexOf(':') + 1)..];
                if (role.StartsWith("tool:", StringComparison.Ordinal) && p.Tool == value) p.Tool = null;
                if (role.StartsWith("module:", StringComparison.Ordinal) && p.Module == value) p.Module = null;
                if (role.StartsWith("metric:", StringComparison.Ordinal) && p.Metric == value) p.Metric = null;
                if (role.StartsWith("filter:", StringComparison.Ordinal)) p.Filters.Remove(value);
                p.Concepts.Remove(role);
                consumed[i] = false;
            }
            // a module word inside a name after a preposition ("tasks for Home Server") is part of the name
            if (consumed[i] && role?.StartsWith("module:", StringComparison.Ordinal) == true
                && ((mode != null && current!.Count > 0) || (i + 1 < tokens.Count && !consumed[i + 1] && tokens[i + 1].Kind == OracleTokenKind.Word)))
            {
                if (p.Module == role[7..]) p.Module = null;
                p.Concepts.Remove(role);
                consumed[i] = false;
            }
            if (consumed[i] && role == "noise")
            {
                if ((current ?? subject).Count > 0) pendingNoise.Add(tokens[i].Text);
                continue;
            }
            if (consumed[i]) pendingNoise.Clear();
            if (consumed[i])
            {
                if (segment.Count > 0) { p.Segments.Add(string.Join(' ', segment)); segment.Clear(); }
                if (role is "prep:about" or "prep:tagged") { mode = "topic"; current = topic.Count == 0 ? topic : extra; }
                else if (role is "prep:for" or "prep:to" or "prep:on" or "prep:related")
                {
                    if (target.Count == 0) { mode = "target"; current = target; targetKind = OracleEntity.None; }
                    else { mode = "extra"; current = extra; }
                }
                else if (mode == "target" && role?.StartsWith("entity:", StringComparison.Ordinal) == true && target.Count == 0) targetKind = EntityOf(role[7..]);
                else if (role?.StartsWith("period:", StringComparison.Ordinal) == true || role?.StartsWith("day:", StringComparison.Ordinal) == true) free.Add(tokens[i].Text);
                else if (current != subject && (current?.Count ?? 0) > 0 && role is not "noise")
                {
                    if (mode == "target" && role?.StartsWith("entity:", StringComparison.Ordinal) == true && targetKind == OracleEntity.None) targetKind = EntityOf(role[7..]);
                    mode = null;
                    current = subject;
                }
                continue;
            }
            if (tokens[i].Kind is OracleTokenKind.Word or OracleTokenKind.Number or OracleTokenKind.Host)
            {
                // filler words between name words belong to the name ("call the bank")
                if (pendingNoise.Count > 0) { (current ?? subject).AddRange(pendingNoise); segment.AddRange(pendingNoise); pendingNoise.Clear(); }
                (current ?? subject).Add(tokens[i].Text);
                free.Add(tokens[i].Text);
                segment.Add(tokens[i].Text);
            }
            else if (tokens[i].Kind is OracleTokenKind.Time or OracleTokenKind.Date) free.Add(tokens[i].Text);
        }
        p.Subject = string.Join(' ', subject);
        if (target.Count > 0) { p.Target = string.Join(' ', target); p.TargetKind = targetKind; }
        else if (targetKind != OracleEntity.None) p.TargetKind = targetKind;
        if (topic.Count > 0) p.Topic = string.Join(' ', topic);
        if (segment.Count > 0) p.Segments.Add(string.Join(' ', segment));
        p.FreeWordCount = subject.Count + target.Count + topic.Count + extra.Count;
        p.FreeTextWithDates = string.Join(' ', free);
    }
}
