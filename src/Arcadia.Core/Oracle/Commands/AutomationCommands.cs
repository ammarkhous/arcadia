using System.Globalization;
using System.Text.RegularExpressions;
using Arcadia.Core.Automation;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Oracle.Commands;

/// <summary>"show automations", "show failed automations", "what automation ran last".</summary>
public sealed class AutomationListCommand : OracleCommandBase
{
    public override string Id => "automation.list";
    public override string Module => "automation";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Automation];
    public override string[] Examples => ["show automations", "show failed automations", "what automation ran last"];
    public override string[] ExamplesDe => ["zeige Automationen"];
    public override string[] ExamplesAr => ["اعرض الأتمتة"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Automation) && IsQuery(p) && p.Verb is not ("on" or "off" or "pause" or "resume") ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Automation is not { } a) return new OracleResult { Kind = OracleResultKind.Error, Headline = c.T("Orc_ToolUnavailable") };
        var p = c.P;
        if (p.HasFilter("lastrun") || (p.Question == "what" && p.Input.Tokens.Any(t => t.Norm is "ran" or "last" or "lief" or "zuletzt" or "اخر")))
        {
            var runs = await a.RecentRunsAsync(8, ct).ConfigureAwait(false);
            var items = runs.Select(r => RunItem(c, r)).ToList();
            var headline = runs.Count > 0 ? c.T("Orc_AutoLastRun", runs[0].AutomationName, c.Env.Time(TimeOnly.FromDateTime(runs[0].StartedAtUtc.ToLocalTime())), c.T("Auto_Status_" + runs[0].Status)) : c.T("Orc_AutoNoRuns");
            return new OracleResult { Kind = OracleResultKind.List, Headline = headline, Items = items, Actions = [NavigateAction(c, "automation", "Activity", c.T("Orc_OpenActivity"))] };
        }
        IEnumerable<AutomationDefinition> list = a.Automations;
        if (p.HasFilter("failed")) list = list.Where(d => d.LastStatus is RunStatus.Failed or RunStatus.Incomplete);
        if (p.HasFilter("active")) list = list.Where(d => d.Enabled);
        var rows = list.ToList();
        if (Counting(p)) return Count(c, "Orc_CountAutomations", rows.Count, NavigateAction(c, "automation"));
        var result = ListResult(c, c.T(a.IsPaused ? "Orc_ListAutomationsPaused" : "Orc_ListAutomations", rows.Count), rows.Select(d => Item(c, d, a.IsPaused)).ToList(),
            p.HasFilter("failed") ? "Orc_NoFailedAutomations" : "Orc_NoAutomations", NavigateAction(c, "automation", label: c.T("Orc_OpenAutomations")));
        return result;
    }

    internal static OracleItem Item(OracleCall c, AutomationDefinition d, bool paused)
    {
        var r = new EntityRef(EntityKind.Automation, d.Id);
        var (glyph, accent) = !d.Enabled ? ("○", "Steel") : d.LastStatus == RunStatus.Failed ? ("⚠", "Red") : paused ? ("Ⅱ", "Amber") : ("●", "Green");
        return new OracleItem
        {
            Title = d.Name,
            Subtitle = glyph + " " + c.T("Auto_Trigger_" + d.Trigger.Type) + " → " + string.Join(", ", d.Actions.Select(x => c.T("Auto_Action_" + x.Type))),
            Value = d.LastRunAtUtc is { } t ? c.Env.Date(DateOnly.FromDateTime(t.ToLocalTime())) : "—",
            Icon = "Automation", Accent = accent, Ref = r, Kind = OracleEntity.Automation,
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
        };
    }

    private static OracleItem RunItem(OracleCall c, AutomationRunRecord r) => new()
    {
        Title = r.AutomationName,
        Subtitle = c.T("Auto_Trigger_" + r.TriggerType) + " · " + r.Summary,
        Value = c.Env.Time(TimeOnly.FromDateTime(r.StartedAtUtc.ToLocalTime())),
        Icon = "Automation",
        Accent = r.Status switch { RunStatus.Success => "Green", RunStatus.Skipped => "Steel", RunStatus.Blocked => "Amber", _ => "Red" },
        Kind = OracleEntity.Automation,
    };
}

/// <summary>"is gaming mode on?", "enable gaming mode", "turn off gaming mode", "power saver on".</summary>
public sealed class ModeCommand : OracleCommandBase
{
    public override string Id => "automation.mode";
    public override string Module => "automation";
    public override OracleIntent Intent => OracleIntent.Update;
    public override string[] Examples => ["is gaming mode on", "enable gaming mode", "disable gaming mode"];
    public override string[] ExamplesDe => ["Spielmodus einschalten"];
    public override string[] ExamplesAr => ["فعل وضع الألعاب"];

    public override double Match(OracleParse p, OracleContext context) => p.Has("mode:gaming") ? 0.95 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Automation is not { } a) return Task.FromResult(new OracleResult { Kind = OracleResultKind.Error, Headline = c.T("Orc_ToolUnavailable") });
        if (c.P.Verb is "on" or "off" or "create" or "open")
        {
            var on = c.P.Verb != "off";
            a.SetGamingMode(on);
            return Task.FromResult(new OracleResult { Kind = OracleResultKind.Done, Headline = c.T(on ? "Orc_GamingOn" : "Orc_GamingOff") });
        }
        var state = a.IsGamingMode;
        return Task.FromResult(new OracleResult
        {
            Kind = OracleResultKind.Message,
            Headline = c.T(state ? "Orc_GamingIsOn" : "Orc_GamingIsOff"),
            Actions = [RunAction(c.T(state ? "Orc_TurnOff" : "Orc_TurnOn"), state ? "disable gaming mode" : "enable gaming mode", "Games", primary: true)],
        });
    }
}

/// <summary>"pause automations", "resume automations".</summary>
public sealed class AutomationPauseCommand : OracleCommandBase
{
    public override string Id => "automation.pause";
    public override string Module => "automation";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Automation];
    public override string[] Examples => ["pause automations", "resume automations"];
    public override string[] ExamplesDe => ["pausiere Automationen", "Automationen fortsetzen"];
    public override string[] ExamplesAr => ["استأنف الأتمتة"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb is "pause" or "resume" or "on" or "off" && (Is(p, OracleEntity.Automation) || (p.Entities.Count == 0 && context.Module == "automation")) ? 0.95 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Automation is not { } a) return new OracleResult { Kind = OracleResultKind.Error, Headline = c.T("Orc_ToolUnavailable") };
        var pause = c.P.Verb is "pause" or "off";
        await a.SetPausedAsync(pause).ConfigureAwait(false);
        return new OracleResult
        {
            Kind = OracleResultKind.Done,
            Headline = c.T(pause ? "Orc_AutoPaused" : "Orc_AutoResumed", a.Automations.Count(d => d.Enabled)),
            Actions = [RunAction(c.T(pause ? "Orc_Resume" : "Orc_Pause"), pause ? "resume automations" : "pause automations", "Stop")],
        };
    }
}

/// <summary>
/// A deliberately small grammar for creating automations from text:
/// "when Cyberpunk starts enable gaming mode", "when steam.exe closes show notification Steam closed",
/// "wenn Cyberpunk startet Spielmodus einschalten", "عندما يبدأ Cyberpunk فعل وضع الألعاب".
/// Anything outside it is not guessed. The result is always a preview; nothing is created without CONFIRM.
/// </summary>
public sealed partial class AutomationCreateCommand : OracleCommandBase
{
    public override string Id => "automation.create";
    public override string Module => "automation";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["when Cyberpunk starts enable gaming mode", "when steam.exe closes show notification Steam closed", "when Hades stops open today"];
    public override string[] ExamplesDe => ["wenn Cyberpunk startet Spielmodus einschalten"];
    public override string[] ExamplesAr => ["عندما يبدأ Cyberpunk فعل وضع الألعاب"];

    [GeneratedRegex(@"^\s*(when|whenever|wenn|sobald)\s+(?<subject>.+?)\s+(?<event>starts|launches|opens|is started|is launched|startet|gestartet wird|closes|stops|exits|quits|is closed|beendet wird|schließt|endet|stoppt)\s*[,:]?\s*(then\s+|dann\s+)?(?<action>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Western();

    [GeneratedRegex(@"^\s*(عندما|لما|حين)\s+(?<event>يبدأ|يبدا|يشتغل|يعمل|يفتح|يتوقف|يغلق|ينتهي)\s+(?<subject>.+?)\s*[،,]?\s+(?<action>(فعل|فعّل|شغل|عطل|اوقف|أوقف|اعرض|أظهر|افتح).+?)\s*$")]
    private static partial Regex Arabic();

    private static readonly string[] StopWords = ["closes", "stops", "exits", "quits", "is closed", "beendet wird", "schließt", "endet", "stoppt", "يتوقف", "يغلق", "ينتهي"];

    public override double Match(OracleParse p, OracleContext context) =>
        Western().IsMatch(p.Input.Original) || Arabic().IsMatch(p.Input.Original) ? 0.97 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Automation is not { } a) return new OracleResult { Kind = OracleResultKind.Error, Headline = c.T("Orc_ToolUnavailable") };
        var m = Western().Match(c.P.Input.Original);
        if (!m.Success) m = Arabic().Match(c.P.Input.Original);
        var subject = m.Groups["subject"].Value.Trim().Trim('"', '“', '”');
        var stops = StopWords.Contains(m.Groups["event"].Value.ToLowerInvariant());
        var actionText = m.Groups["action"].Value;

        // WHEN: an executable name, else a library game (a strong single match only)
        AutomationStep trigger;
        string whenLabel;
        if (AutomationText.IsExecutableName(subject))
        {
            trigger = new AutomationStep(stops ? "process.stopped" : "process.started", new() { ["exe"] = AutomationText.ExeName(subject) });
            whenLabel = c.T(stops ? "Auto_Trigger_process.stopped" : "Auto_Trigger_process.started") + " · " + AutomationText.ExeName(subject);
        }
        else
        {
            var r = await c.ResolveAsync(subject, ct, EntityKind.Game).ConfigureAwait(false);
            if (r.Single is not { } game)
                return r.IsNone
                    ? new OracleResult { Kind = OracleResultKind.Clarify, Headline = c.T("Orc_AutoUnknownSubject", subject), Suggestions = ["when Cyberpunk2077.exe starts enable gaming mode"], Confidence = OracleConfidence.Low }
                    : c.Choose(r, x => Regex.Replace(c.P.Input.Original, Regex.Escape(subject), x.Name.Replace("$", "$$"), RegexOptions.IgnoreCase));
            trigger = new AutomationStep(stops ? "game.stopped" : "game.started", new() { ["game"] = game.Ref.Id.ToString(CultureInfo.InvariantCulture) });
            whenLabel = c.T(stops ? "Auto_Trigger_game.stopped" : "Auto_Trigger_game.started") + " · " + game.Name;
        }

        // THEN: one of a few clear actions
        var action = ReadAction(actionText, c);
        if (action == null)
            return new OracleResult
            {
                Kind = OracleResultKind.Clarify,
                Headline = c.T("Orc_AutoUnknownAction"),
                Detail = c.T("Orc_AutoActionsKnown"),
                Suggestions = [$"when {subject} {(stops ? "stops" : "starts")} enable gaming mode", $"when {subject} {(stops ? "stops" : "starts")} show notification {subject}"],
                Confidence = OracleConfidence.Low,
            };

        var def = new AutomationDefinition(0, c.T("Orc_AutoName", subject, c.T("Auto_Action_" + action.Type)), c.T("Orc_AutoCreatedBy"), true, trigger, ConditionMode.All, [], [action]);
        var problems = a.Registry.Validate(def);
        if (problems.Count > 0) return new OracleResult { Kind = OracleResultKind.Error, Headline = c.T("Orc_AutoInvalid"), Detail = string.Join(", ", problems) };

        var preview = new OraclePreview
        {
            Title = c.T("Orc_AutoPreview"),
            Icon = "Automation",
            Fields = [(c.T("Auto_WhenCaps"), whenLabel), (c.T("Auto_ThenCaps"), c.T("Auto_Action_" + action.Type) + (action.Get("title") is { } t ? " · " + t : ""))],
            Confirm = c.ConfirmAction(async x =>
            {
                var saved = await a.CreateAsync(def, x).ConfigureAwait(false);
                var r = new EntityRef(EntityKind.Automation, saved.Id);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_AutoCreated", saved.Name),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
                };
            }),
            Edit = new OracleAction { Label = c.T("Orc_Edit"), Icon = "Journal", Kind = OracleActionKind.EditAutomation, Payload = def },
        };
        return await c.ChangeAsync(preview, false, ct, alwaysConfirm: true).ConfigureAwait(false);
    }

    private static AutomationStep? ReadAction(string text, OracleCall c)
    {
        var t = OracleText.FoldPhrase(text);
        bool Has(params string[] words) => words.Any(w => (" " + t + " ").Contains(" " + OracleText.FoldPhrase(w) + " ", StringComparison.Ordinal));
        var gaming = Has("gaming mode", "game mode", "spielmodus", "وضع الالعاب", "وضع اللعب");
        if (gaming && Has("disable", "turn off", "switch off", "deactivate", "ausschalten", "deaktiviere", "schalte aus", "عطل", "اوقف", "ايقاف")) return new AutomationStep("gaming.off");
        if (gaming) return new AutomationStep("gaming.on");
        if (Has("open today", "show today", "today", "heute", "اليوم")) return new AutomationStep("open.today");
        if (Has("show notification", "notify", "notification", "benachrichtigung", "اشعار", "إشعار"))
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => OracleText.Fold(w) is "show" or "notification" or "notify" or "zeige" or "benachrichtigung" or "اعرض" or "اشعار").ToArray();
            return new AutomationStep("notify", new() { ["title"] = words.Length > 0 ? string.Join(' ', words) : "{gameTitle}{exe}" });
        }
        if (Has("mark as playing", "mark playing", "als gespielt")) return new AutomationStep("game.playing");
        return null;
    }
}
