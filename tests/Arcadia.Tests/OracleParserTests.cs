using Arcadia.Core.Oracle;
using Xunit;

namespace Arcadia.Tests;

public sealed class OracleParserTests
{
    private static readonly DateOnly Today = new(2026, 9, 28); // a Monday

    private static OracleParse P(string text, OracleLanguage ui = OracleLanguage.English) => OracleParser.Parse(text, Today, ui);

    [Fact]
    public void Normalizer_keeps_payload_paths_money_and_names()
    {
        var p = P("Add note to Cyberpunk: Finish Panam missions at C:\\Games\\CP77");
        Assert.Equal("Finish Panam missions at C:\\Games\\CP77", p.Payload);
        Assert.Equal("create", p.Verb);
        Assert.Contains(OracleEntity.Note, p.Entities);
        Assert.Equal("Cyberpunk", p.Target);

        var q = P("add note \"Ünïcode Straße, 12.5€\"");
        Assert.Equal("Ünïcode Straße, 12.5€", q.Payload);
        Assert.Equal("https://x.org:8080/a", OracleText.Parse("open https://x.org:8080/a").Tokens[1].Text);
    }

    [Theory]
    [InlineData("show unfinished games", "show", OracleEntity.Game, "unfinished")]
    [InlineData("which games am I currently playing", "show", OracleEntity.Game, "playing")]
    [InlineData("show open tasks", "show", OracleEntity.Task, "unfinished")]
    [InlineData("show overdue tasks", "show", OracleEntity.Task, "overdue")]
    [InlineData("zeige unfertige Spiele", "show", OracleEntity.Game, "unfinished")]
    [InlineData("اعرض الألعاب غير المكتملة", "show", OracleEntity.Game, "unfinished")]
    [InlineData("اعرض المهام المتأخرة", "show", OracleEntity.Task, "overdue")]
    public void Reads_verb_entity_filter(string text, string verb, OracleEntity entity, string filter)
    {
        var p = P(text);
        Assert.Equal(verb, p.Verb);
        Assert.Equal(entity, p.Entity);
        Assert.Contains(filter, p.Filters);
    }

    [Fact]
    public void Open_is_a_verb_at_the_start_and_a_filter_later()
    {
        Assert.Equal("open", P("open games").Verb);
        var p = P("show open tasks");
        Assert.Equal("show", p.Verb);
        Assert.Contains("unfinished", p.Filters);
    }

    [Fact]
    public void Language_detection_is_deterministic()
    {
        Assert.Equal(OracleLanguage.German, P("zeige meine Aufgaben für heute").Language);
        Assert.Equal(OracleLanguage.Arabic, P("اعرض مهامي").Language);
        Assert.Equal(OracleLanguage.English, P("show my tasks").Language);
        Assert.Equal(OracleLanguage.German, OracleParser.Parse("show tasks", Today, OracleLanguage.English, OracleLanguage.German).Language);
    }

    [Theory]
    [InlineData("tasks today", "2026-09-28", "2026-09-28")]
    [InlineData("tasks tomorrow", "2026-09-29", "2026-09-29")]
    [InlineData("events this week", "2026-09-28", "2026-10-04")]
    [InlineData("events next week", "2026-10-05", "2026-10-11")]
    [InlineData("spending last month", "2026-08-01", "2026-08-31")]
    [InlineData("events on Friday", "2026-10-02", "2026-10-02")]
    [InlineData("events next Friday", "2026-10-09", "2026-10-09")]
    [InlineData("tasks due within 7 days", "2026-09-28", "2026-10-05")]
    [InlineData("was steht nächste Woche an", "2026-10-05", "2026-10-11")]
    [InlineData("مواعيد الاسبوع القادم", "2026-10-05", "2026-10-11")]
    [InlineData("كم صرفت هذا الشهر", "2026-09-01", "2026-09-30")]
    [InlineData("event 2026-10-12", "2026-10-12", "2026-10-12")]
    [InlineData("Termin 12.10.", "2026-10-12", "2026-10-12")]
    public void Reads_periods(string text, string from, string to)
    {
        var p = P(text);
        Assert.NotNull(p.Period);
        Assert.Equal(DateOnly.Parse(from), p.Period!.From);
        Assert.Equal(DateOnly.Parse(to), p.Period.To);
    }

    [Fact]
    public void Slash_dates_that_read_two_ways_are_ambiguous()
    {
        var p = P("create event dentist 03/04");
        Assert.Null(p.Period);
        Assert.Equal((new DateOnly(2026, 4, 3), new DateOnly(2026, 3, 4)), p.AmbiguousDate);
        Assert.NotNull(P("create event dentist 25/04").Period);
    }

    [Fact]
    public void Reads_money_hosts_ports_and_times()
    {
        var p = P("I spent 35 euro on groceries");
        Assert.Equal(35m, p.Amount);
        Assert.Equal("EUR", p.Currency);
        var q = P("test port 443 on google.com");
        Assert.Equal("google.com", q.Host);
        Assert.Equal(443, q.Port);
        Assert.Equal("port", q.Tool);
        var r = P("ping 192.168.1.1");
        Assert.Equal("192.168.1.1", r.Host);
        Assert.Equal(new TimeOnly(15, 0), P("meeting tomorrow at 15:00").Time);
        Assert.Equal(new TimeOnly(15, 0), P("remind me tomorrow at 3pm").Time);
        Assert.Equal(12.5m, P("add expense €12.50 coffee").Amount);
    }

    [Fact]
    public void Splits_subject_target_and_topic()
    {
        var p = P("show tasks for Home Server");
        Assert.Equal(OracleEntity.Task, p.Entity);
        Assert.Equal("Home Server", p.Target);
        var n = P("show notes about router");
        Assert.Equal("router", n.Topic);
        var o = P("open Cyberpunk");
        Assert.Equal("Cyberpunk", o.Subject);
        var t = P("show documents for project Home Server");
        Assert.Equal("Home Server", t.Target);
        Assert.Equal(OracleEntity.Project, t.TargetKind);
    }

    [Fact]
    public void Follow_up_and_reference_words()
    {
        Assert.True(P("only high priority").IsFollowUp);
        Assert.Contains("high", P("only high priority").Filters);
        Assert.True(OracleEngine.IsRefinement(P("only high priority")));
        Assert.True(OracleEngine.IsRefinement(P("this week")));
        Assert.False(OracleEngine.IsRefinement(P("show tasks")));
        var its = P("show its documents");
        Assert.True(its.RefersBack);
        Assert.Equal(OracleEntity.Document, its.Entity);
    }

    [Fact]
    public void Destructive_words_are_recognised_to_be_refused()
    {
        Assert.Equal("refuse", P("delete all tasks").Verb);
        Assert.Equal("refuse", P("kill process chrome").Verb);
        Assert.Equal("refuse", P("lösche alle Notizen").Verb);
        Assert.Equal("refuse", P("احذف المهام").Verb);
    }

    [Fact]
    public void Typos_in_long_vocabulary_words_are_tolerated_but_flagged()
    {
        var p = P("show unfinshed games");
        Assert.Contains("unfinished", p.Filters);
        Assert.True(p.UsedFuzzyVocabulary);
    }

    [Fact]
    public void Fuzzy_name_matching_is_conservative()
    {
        Assert.True(OracleFuzzy.Similarity("cyberpank", "Cyberpunk 2077") >= OracleFuzzy.Plausible);
        Assert.True(OracleFuzzy.Similarity("cyberpunk", "Cyberpunk 2077") >= OracleFuzzy.Strong);
        Assert.True(OracleFuzzy.Similarity("elden", "Elden Ring") >= OracleFuzzy.Strong);
        Assert.True(OracleFuzzy.Similarity("home", "Halo Infinite") < OracleFuzzy.Plausible);
        Assert.True(OracleFuzzy.Similarity("ab", "Abzu") < OracleFuzzy.Strong);
    }

    [Theory]
    [InlineData("show trips in 2024", 2024)]
    [InlineData("how much did I spend on groceries during 2025", 2025)]
    [InlineData("Reisen im Jahr 2023", 2023)]
    [InlineData("الرحلات في 2024", 2024)]
    public void Explicit_years_after_a_time_word_are_periods(string text, int year)
    {
        var p = P(text);
        Assert.Equal(new DateOnly(year, 1, 1), p.Period?.From);
        Assert.Equal(new DateOnly(year, 12, 31), p.Period?.To);
        Assert.DoesNotContain(year.ToString(), p.Subject);
    }

    [Fact]
    public void A_bare_number_is_not_a_year() => Assert.Null(P("spent 2024 euro").Period);

    [Fact]
    public void Whats_next_is_a_question() => Assert.Equal("what", P("what's next").Question);
}
