using System.Globalization;

namespace Arcadia.Core.Organizer;

/// <summary>Things in Arcadia that tasks, notes and events can point at (and that <c>arcadia://</c> links name).</summary>
public enum EntityKind { Game, Project, Document, Trip, Place, Task, Note, Event, Diagnostic, Journal, Transaction, Account, Budget, Recurring, Goal, Automation }

/// <summary>
/// A reference to one Arcadia record, e.g. <c>arcadia://trip/12</c>. Internal navigation only: no Windows protocol handler is
/// registered. A reference whose record was deleted simply resolves to nothing.
/// </summary>
public readonly record struct EntityRef(EntityKind Kind, int Id)
{
    public const string Scheme = "arcadia";

    private static readonly Dictionary<string, EntityKind> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["game"] = EntityKind.Game, ["project"] = EntityKind.Project, ["document"] = EntityKind.Document, ["doc"] = EntityKind.Document,
        ["trip"] = EntityKind.Trip, ["place"] = EntityKind.Place, ["task"] = EntityKind.Task, ["note"] = EntityKind.Note,
        ["event"] = EntityKind.Event, ["diagnostic"] = EntityKind.Diagnostic, ["session"] = EntityKind.Diagnostic, ["journal"] = EntityKind.Journal,
        ["transaction"] = EntityKind.Transaction, ["account"] = EntityKind.Account, ["budget"] = EntityKind.Budget, ["recurring"] = EntityKind.Recurring, ["bill"] = EntityKind.Recurring, ["goal"] = EntityKind.Goal, ["automation"] = EntityKind.Automation,
    };

    public static string KindName(EntityKind kind) => kind.ToString().ToLowerInvariant();

    public override string ToString() => $"{Scheme}://{KindName(Kind)}/{Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Parses <c>arcadia://kind/id</c> (case-insensitive, optional trailing slash). Anything else is rejected.</summary>
    public static bool TryParse(string? text, out EntityRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        const string prefix = Scheme + "://";
        if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = s[prefix.Length..].TrimEnd('/').Split('/');
        if (parts.Length != 2 || !ByName.TryGetValue(parts[0], out var kind)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return false;
        result = new EntityRef(kind, id);
        return true;
    }

    /// <summary>From stored columns (<c>ContextKind</c>, <c>ContextId</c>); null when either is missing or unknown.</summary>
    public static EntityRef? From(string? kind, int? id) =>
        kind != null && id is > 0 && Enum.TryParse<EntityKind>(kind, true, out var k) ? new EntityRef(k, id.Value) : null;
}
