using Arcadia.Core.Organizer;

namespace Arcadia.Core.Oracle;

/// <summary>Command languages. Responses use Arcadia's interface language; input may be any of these.</summary>
public enum OracleLanguage { English, German, Arabic }

/// <summary>What the user wants done (combined with an entity and parameters instead of hundreds of intents).</summary>
public enum OracleIntent { Navigate, Show, Search, Count, Calculate, Open, Create, Update, Diagnose, Help, Reset, Refuse }

/// <summary>Things Oracle can talk about.</summary>
public enum OracleEntity { None, Game, Project, Task, Note, Document, Trip, Place, Event, Account, Transaction, Budget, Subscription, Bill, Goal, Session, Tool, Module, Screenshot, Journal, Bucket, Activity, Category, Automation }

public enum OracleConfidence { High, Medium, Low }

/// <summary>How much a command changes: read-only commands run at once; changes follow the confirmation setting; destructive ones are refused.</summary>
public enum OracleSafety { ReadOnly, Create, Update }

/// <summary>A request from any input surface (the overlay, the Oracle page, a future speech front end).</summary>
public sealed record OracleRequest(string Text, OracleLanguage? Language = null);

/// <summary>
/// Where the user is and what the conversation is about. Small and structured: the current page's record, the record
/// the last result focused on, and the last query (for follow-ups like "only high priority"). It expires.
/// </summary>
public sealed class OracleContext
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The shell section the user is on ("games", "projects", …).</summary>
    public string Module { get; set; } = "home";
    /// <summary>The record the current page is about (an open game, project, trip…).</summary>
    public EntityRef? Current { get; set; }
    public string? CurrentName { get; set; }

    /// <summary>The record the last result was about ("open Italy trip" → "show its documents").</summary>
    public EntityRef? Focus { get; private set; }
    public string? FocusName { get; private set; }

    /// <summary>The last query that follow-ups refine.</summary>
    public OracleFollowUp? Last { get; private set; }

    public DateTime UpdatedUtc { get; private set; }

    public void SetFocus(EntityRef? focus, string? name, DateTime nowUtc)
    {
        Focus = focus;
        FocusName = name;
        UpdatedUtc = nowUtc;
    }

    public void SetLast(OracleFollowUp? last, DateTime nowUtc)
    {
        Last = last;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Drops the conversation (focus and last query) but keeps the page context.</summary>
    public void Reset()
    {
        Focus = null;
        FocusName = null;
        Last = null;
    }

    /// <summary>Conversation context older than <see cref="Lifetime"/>, or from another module, no longer applies.</summary>
    public void Expire(DateTime nowUtc)
    {
        if (UpdatedUtc != default && nowUtc - UpdatedUtc > Lifetime) Reset();
        if (Last != null && !string.Equals(Last.Module, Module, StringComparison.OrdinalIgnoreCase)) Last = null;
    }

    /// <summary>The record a "its"/"this" or an implicit target refers to: the conversation's focus, else the page's record.</summary>
    public (EntityRef Ref, string? Name)? Referent => Focus is { } f ? (f, FocusName) : Current is { } c ? (c, CurrentName) : null;
}

/// <summary>A query that can be refined by a follow-up.</summary>
public sealed record OracleFollowUp(string CommandId, OracleParse Parse, string Module);

/// <summary>An action on a result: open something, navigate, run a command, confirm a preview, copy text.</summary>
public enum OracleActionKind { Open, Navigate, Run, Invoke, Confirm, Cancel, Edit, Copy, OpenFile, OpenFolder, EditTransaction, EditAutomation }

public sealed class OracleAction
{
    public required string Label { get; init; }
    public string Icon { get; init; } = "ArrowRight";
    public OracleActionKind Kind { get; init; }
    public EntityRef? Target { get; init; }
    /// <summary>A shell section key (Navigate) and optional tab/sub-view.</summary>
    public string? Section { get; init; }
    public string? Tab { get; init; }
    /// <summary>Oracle text to run (Run, Edit).</summary>
    public string? Command { get; init; }
    /// <summary>Performs a change (Invoke, Confirm) and returns what to show next.</summary>
    public Func<CancellationToken, Task<OracleResult>>? Invoke { get; init; }
    /// <summary>Text to copy, or a file/folder path.</summary>
    public string? Text { get; init; }
    /// <summary>For EditTransaction: the draft to open in the editor.</summary>
    public object? Payload { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsEnabled { get; init; } = true;
}

/// <summary>One row of a result: a game, a task, a transaction, a ping reply… with its own compact actions.</summary>
public sealed class OracleItem
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>The figure on the right (64 %, a date, an amount).</summary>
    public string? Value { get; init; }
    public bool ValueIsMoney { get; init; }
    public string Icon { get; init; } = "Sparkle";
    public string Accent { get; init; } = "Blue";
    public OracleEntity Kind { get; init; }
    public EntityRef? Ref { get; init; }
    /// <summary>0–1 progress bar (games, budgets, goals).</summary>
    public double? Progress { get; init; }
    /// <summary>Section heading in cross-module results ("Tasks", "Documents").</summary>
    public string? Group { get; init; }
    public bool IsMuted { get; init; }
    public List<OracleAction> Actions { get; init; } = [];
}

public sealed record OracleMetric(string Label, string Value, string? Tone = null, bool IsMoney = false);

/// <summary>What a change will do, shown before it happens.</summary>
public sealed class OraclePreview
{
    public required string Title { get; init; }
    public string Icon { get; init; } = "Plus";
    public List<(string Label, string Value)> Fields { get; init; } = [];
    public required OracleAction Confirm { get; init; }
    public OracleAction? Edit { get; init; }
    /// <summary>Assumptions Oracle made (date read as…, project from the current page…).</summary>
    public List<string> Notes { get; init; } = [];
}

public enum OracleResultKind { List, Metric, Preview, Message, Clarify, Unknown, Help, Tool, Refused, Error, Navigated, Done }

/// <summary>A structured answer. The UI renders it; nothing here is prose generated on the fly.</summary>
public sealed class OracleResult
{
    public OracleResultKind Kind { get; init; }
    public required string Headline { get; init; }
    public string? Detail { get; set; }
    public OracleConfidence Confidence { get; set; } = OracleConfidence.High;
    public List<OracleItem> Items { get; init; } = [];
    public List<OracleMetric> Metrics { get; init; } = [];
    public OraclePreview? Preview { get; init; }
    /// <summary>Commands the user can run next (clarifications, related queries, help examples).</summary>
    public List<string> Suggestions { get; init; } = [];
    /// <summary>Run at once (navigation commands).</summary>
    public OracleAction? AutoAction { get; init; }
    public List<OracleAction> Actions { get; init; } = [];
    /// <summary>The record this result is about (becomes the conversation's focus).</summary>
    public EntityRef? Focus { get; init; }
    public string? FocusName { get; init; }
    public bool HeadlineIsMoney { get; init; }
    public OracleTrace? Trace { get; set; }
}

/// <summary>Developer diagnostics for one command.</summary>
public sealed record OracleTrace(
    string Input,
    string Normalized,
    OracleLanguage Language,
    string Concepts,
    string Intent,
    string Entity,
    string Parameters,
    string Context,
    OracleConfidence Confidence,
    string Score,
    string Handler,
    double ParseMs,
    double TotalMs);
