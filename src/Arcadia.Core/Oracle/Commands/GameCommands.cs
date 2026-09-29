using System.Globalization;
using Arcadia.Core.Games;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Oracle.Commands;

/// <summary>"show unfinished games", "which games am I playing", "how many games have I completed", "games not played recently".</summary>
public sealed class GameListCommand : OracleCommandBase
{
    public override string Id => "games.list";
    public override string Module => "games";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Game];
    public override bool CanRefine => true;
    public override string[] Examples => ["show unfinished games", "which games am I playing", "how many games have I completed", "games not played recently", "favorite games"];
    public override string[] ExamplesDe => ["zeige unfertige Spiele", "wie viele Spiele habe ich abgeschlossen"];
    public override string[] ExamplesAr => ["اعرض الألعاب غير المكتملة", "كم عدد الألعاب المكتملة"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Game) && IsQuery(p) && p.Metric is null or "stats" ? (p.Metric == "stats" ? 0.5 : 0.85) : 0;

    public static bool Favorite(Game g) => g.IsFeatured || g.TagList.Any(t => t.Equals("favorite", StringComparison.OrdinalIgnoreCase) || t.Equals("favourite", StringComparison.OrdinalIgnoreCase));

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        IEnumerable<Game> games = await c.S.Games.GetGamesAsync(ct).ConfigureAwait(false);
        var nowUtc = c.Now.ToUniversalTime();
        if (p.HasFilter("unfinished")) games = games.Where(g => g.Status is GameStatus.Playing or GameStatus.Backlog && g.CompletionPercent < 100);
        if (p.HasFilter("completed")) games = games.Where(g => g.Status == GameStatus.Completed);
        if (p.HasFilter("backlog")) games = games.Where(g => g.Status == GameStatus.Backlog);
        if (p.HasFilter("playing") || p.HasFilter("active")) games = games.Where(g => g.Status == GameStatus.Playing);
        if (p.HasFilter("abandoned")) games = games.Where(g => g.Status == GameStatus.Abandoned);
        if (p.HasFilter("favorite")) games = games.Where(Favorite);
        if (p.HasFilter("high")) games = games.Where(g => g.Priority == BacklogPriority.High);
        if (p.HasFilter("notplayed")) games = games.Where(g => g.Status != GameStatus.Completed && (g.LastPlayedAtUtc is null || nowUtc - g.LastPlayedAtUtc > TimeSpan.FromDays(30)));
        if (p.Period is { } per)
            games = games.Where(g => g.LastPlayedAtUtc is { } lp && DateOnly.FromDateTime(lp.ToLocalTime()) >= per.From && DateOnly.FromDateTime(lp.ToLocalTime()) <= per.To);
        if (p.Topic is { Length: > 0 } tag)
            games = games.Where(g => g.TagList.Any(t => OracleFuzzy.Similarity(tag, t) >= OracleFuzzy.Strong) || OracleFuzzy.Similarity(tag, g.Platform) >= OracleFuzzy.Strong);

        var list = (p.HasFilter("recent") || p.HasFilter("playing") || p.Period != null
            ? games.OrderByDescending(g => g.LastPlayedAtUtc ?? DateTime.MinValue)
            : p.HasFilter("notplayed") ? games.OrderBy(g => g.LastPlayedAtUtc ?? DateTime.MinValue) : games.OrderByDescending(g => g.LastPlayedAtUtc ?? g.AddedAtUtc)).ToList();

        if (Counting(p))
            return Count(c, "Orc_CountGames", list.Count, NavigateAction(c, "games", label: c.T("Orc_OpenLibrary")));
        var items = list.Select(g => Item(c, g)).ToList();
        return ListResult(c, c.T("Orc_ListGames", items.Count), items, "Orc_NoGames", NavigateAction(c, "games", label: c.T("Orc_OpenLibrary")));
    }

    public static OracleItem Item(OracleCall c, Game g)
    {
        var r = new EntityRef(EntityKind.Game, g.Id);
        var sub = c.T("Orc_GameStatus_" + g.Status) + " · " + c.T("Orc_Hours", Math.Round(g.PlaytimeHours, 1).ToString("0.#", CultureInfo.CurrentCulture));
        if (g.LastPlayedAtUtc is { } lp) sub += " · " + c.T("Orc_LastPlayed", c.Env.Date(DateOnly.FromDateTime(lp.ToLocalTime())));
        return new OracleItem
        {
            Title = g.Title,
            Subtitle = sub,
            Value = g.CompletionPercent + " %",
            Progress = g.CompletionPercent / 100.0,
            Icon = "Games",
            Accent = g.Status switch { GameStatus.Playing => "Cyan", GameStatus.Completed => "Green", GameStatus.Abandoned => "Red", _ => "Blue" },
            Kind = OracleEntity.Game,
            Ref = r,
            Actions =
            [
                new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true },
                new OracleAction { Label = c.T("Orc_Journal"), Icon = "Journal", Kind = OracleActionKind.Open, Target = r, Tab = "Journal" },
            ],
        };
    }
}

/// <summary>"gaming stats", "how many hours have I played this month".</summary>
public sealed class GameStatsCommand : OracleCommandBase
{
    public override string Id => "games.stats";
    public override string Module => "games";
    public override OracleIntent Intent => OracleIntent.Calculate;
    public override OracleEntity[] Entities => [OracleEntity.Game];
    public override bool UsesFreeText => true;
    public override string[] Examples => ["gaming stats", "how many hours have I played"];
    public override string[] ExamplesDe => ["Spiele Statistik"];
    public override string[] ExamplesAr => ["احصائيات الالعاب"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Question is "howmany" or "howmuch" && p.Input.Tokens.Any(t => t.Norm is "hours" or "stunden" or "ساعات" or "playtime" or "spielzeit")) return 0.9;
        if (!Mentions(p, OracleEntity.Game) && context.Module != "games") return 0;
        return p.Metric == "stats" ? 0.9 : 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var games = await c.S.Games.GetGamesAsync(ct).ConfigureAwait(false);
        var hours = games.Sum(g => g.PlaytimeHours);
        return new OracleResult
        {
            Kind = OracleResultKind.Metric,
            Headline = c.T("Orc_GameStatsHeadline", Math.Round(hours).ToString("N0", CultureInfo.CurrentCulture), games.Count),
            Metrics =
            [
                new(c.T("Orc_GameStatus_Playing"), games.Count(g => g.Status == GameStatus.Playing).ToString(CultureInfo.CurrentCulture), "Cyan"),
                new(c.T("Orc_GameStatus_Backlog"), games.Count(g => g.Status == GameStatus.Backlog).ToString(CultureInfo.CurrentCulture)),
                new(c.T("Orc_GameStatus_Completed"), games.Count(g => g.Status == GameStatus.Completed).ToString(CultureInfo.CurrentCulture), "Green"),
                new(c.T("Orc_AvgCompletion"), (games.Count == 0 ? 0 : (int)games.Average(g => g.CompletionPercent)) + " %"),
            ],
            Actions = [NavigateAction(c, "games", label: c.T("Orc_OpenLibrary"))],
        };
    }
}

/// <summary>"mark Cyberpunk as completed", "set Elden Ring to playing", "favorite Hades". Exact names change directly (Smart); others preview.</summary>
public sealed class GameUpdateCommand : OracleCommandBase
{
    public override string Id => "games.update";
    public override string Module => "games";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Game];
    public override OracleSafety Safety => OracleSafety.Update;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["mark Cyberpunk as completed", "set Elden Ring to playing", "favorite Hades"];
    public override string[] ExamplesDe => ["markiere Cyberpunk als abgeschlossen"];
    public override string[] ExamplesAr => ["علم Cyberpunk مكتملة"];

    private static GameStatus? StatusOf(OracleParse p) =>
        p.HasFilter("completed") || p.Verb == "complete" ? GameStatus.Completed
        : p.HasFilter("playing") || p.HasFilter("active") ? GameStatus.Playing
        : p.HasFilter("backlog") ? GameStatus.Backlog
        : p.HasFilter("abandoned") ? GameStatus.Abandoned : null;

    public override double Match(OracleParse p, OracleContext context)
    {
        var named = NameOf(p).Length > 0 || (p.RefersBack && context.Referent is { Ref.Kind: EntityKind.Game });
        if (!named || p.Entity is not (OracleEntity.None or OracleEntity.Game)) return 0;
        if (p.Verb is "favorite" or "unfavorite") return 0.8;
        if (p.Verb is "set" or "complete" && StatusOf(p) != null) return Mentions(p, OracleEntity.Game) || context.Module == "games" ? 0.85 : 0.55;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        EntityRef target;
        string name;
        var exact = true;
        if (NameOf(p) is { Length: > 0 } text)
        {
            var r = await c.ResolveAsync(text, ct, EntityKind.Game).ConfigureAwait(false);
            if (r.Single is not { } s) return r.IsNone ? c.NotFound(text) : c.Choose(r, x => Rebuild(p, x.Name));
            (target, name, exact) = (s.Ref, s.Name, r.IsExact);
        }
        else
        {
            var referent = c.Referent(EntityKind.Game)!.Value;
            (target, name) = (referent.Ref, referent.Name ?? string.Empty);
        }

        if (p.Verb is "favorite" or "unfavorite")
        {
            var on = p.Verb == "favorite";
            var preview = new OraclePreview
            {
                Title = c.T(on ? "Orc_FavoriteTitle" : "Orc_UnfavoriteTitle", name),
                Icon = "Star",
                Confirm = c.ConfirmAction(async t =>
                {
                    var game = await c.S.Games.GetGameDetailAsync(target.Id, t).ConfigureAwait(false) ?? throw new InvalidOperationException("Game not found.");
                    var tags = game.TagList.Where(x => !x.Equals("favorite", StringComparison.OrdinalIgnoreCase) && !x.Equals("favourite", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (on) tags.Add("Favorite");
                    game.Tags = string.Join(", ", tags);
                    await c.S.Games.SaveGameAsync(game, t).ConfigureAwait(false);
                    return Done(c, c.T(on ? "Orc_FavoriteDone" : "Orc_UnfavoriteDone", name), target, name);
                }),
            };
            return await c.ChangeAsync(preview, exact, ct).ConfigureAwait(false);
        }

        var status = StatusOf(p)!.Value;
        var change = new OraclePreview
        {
            Title = c.T("Orc_GameStatusTitle", name, c.T("Orc_GameStatus_" + status)),
            Icon = "Games",
            Fields = [(c.T("Orc_Field_Status"), c.T("Orc_GameStatus_" + status))],
            Confirm = c.ConfirmAction(async t =>
            {
                var game = await c.S.Games.GetGameDetailAsync(target.Id, t).ConfigureAwait(false) ?? throw new InvalidOperationException("Game not found.");
                game.Status = status;
                if (status == GameStatus.Completed) game.CompletionPercent = 100;
                await c.S.Games.SaveGameAsync(game, t).ConfigureAwait(false);
                return Done(c, c.T("Orc_GameStatusDone", name, c.T("Orc_GameStatus_" + status)), target, name);
            }),
        };
        return await c.ChangeAsync(change, exact, ct).ConfigureAwait(false);
    }

    private static string Rebuild(OracleParse p, string name) =>
        p.Verb is "favorite" or "unfavorite" ? $"{p.Verb} game \"{name}\"" : $"mark game \"{name}\" as {(StatusOf(p) ?? GameStatus.Playing).ToString().ToLowerInvariant()}";

    internal static OracleResult Done(OracleCall c, string headline, EntityRef target, string name) => new()
    {
        Kind = OracleResultKind.Done,
        Headline = headline,
        Focus = target,
        FocusName = name,
        Actions = [new OracleAction { Label = c.T("Orc_OpenName", name), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = target, IsPrimary = true }],
    };
}
