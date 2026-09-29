using Arcadia.Core.Organizer;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Organizer;

/// <summary>Names, icons and accents of the records tasks, notes and events point at. Deleted records resolve to nothing.</summary>
public sealed class EntityDirectory(IDbContextFactory<ArcadiaDbContext> factory) : IEntityDirectory
{
    public static string IconOf(EntityKind kind) => kind switch
    {
        EntityKind.Game => "Games",
        EntityKind.Project => "Projects",
        EntityKind.Document => "Documents",
        EntityKind.Trip => "Suitcase",
        EntityKind.Place => "MapPin",
        EntityKind.Task => "Tasks",
        EntityKind.Note => "Notes",
        EntityKind.Event => "Calendar",
        EntityKind.Diagnostic => "Diagnostic",
        EntityKind.Journal => "Journal",
        EntityKind.Transaction => "Receipt",
        EntityKind.Account => "Wallet",
        EntityKind.Budget => "Chart",
        EntityKind.Recurring => "Repeat",
        EntityKind.Goal => "Target",
        _ => "Link",
    };

    private static string AccentOf(EntityKind kind) => kind switch
    {
        EntityKind.Game => "Purple",
        EntityKind.Project => "Cyan",
        EntityKind.Document => "Amber",
        EntityKind.Trip or EntityKind.Place or EntityKind.Journal => "Green",
        EntityKind.Diagnostic => "Steel",
        EntityKind.Event => "Blue",
        EntityKind.Transaction or EntityKind.Account or EntityKind.Budget or EntityKind.Recurring or EntityKind.Goal => "Gold",
        _ => "Blue",
    };

    public async Task<IReadOnlyDictionary<EntityRef, EntityInfo>> DescribeAsync(IEnumerable<EntityRef> refs, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<EntityRef, EntityInfo>();
        var groups = refs.Distinct().GroupBy(r => r.Kind).ToList();
        if (groups.Count == 0) return result;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var g in groups)
        {
            var ids = g.Select(r => r.Id).ToList();
            foreach (var (id, name, accent) in await NamesAsync(db, g.Key, ids, cancellationToken).ConfigureAwait(false))
            {
                var r = new EntityRef(g.Key, id);
                result[r] = new EntityInfo(r, name, IconOf(g.Key), accent ?? AccentOf(g.Key));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<EntityInfo>> FindAsync(EntityKind kind, string? text, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pattern = string.IsNullOrWhiteSpace(text) ? null : "%" + text.Trim().Replace("%", "").Replace("_", "") + "%";
        take = Math.Clamp(take, 1, 500);
        List<(int Id, string Name, string? Accent)> rows = kind switch
        {
            EntityKind.Game => (await db.Games.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.LastPlayedAtUtc).ThenBy(x => x.Title)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            EntityKind.Project => (await db.Projects.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Name, pattern)).OrderByDescending(x => x.UpdatedAtUtc)
                .Take(take).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
            EntityKind.Document => (await db.Documents.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.DisplayName, pattern)).OrderByDescending(x => x.AddedAtUtc)
                .Take(take).Select(x => new { x.Id, x.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.DisplayName, (string?)null)).ToList(),
            EntityKind.Trip => (await db.Trips.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Name, pattern)).OrderByDescending(x => x.StartDate)
                .Take(take).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
            EntityKind.Place => (await db.TravelPlaces.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Name, pattern)).OrderBy(x => x.Name)
                .Take(take).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)null)).ToList(),
            EntityKind.Task => (await db.Tasks.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.UpdatedAtUtc)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            EntityKind.Note => (await db.Notes.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.UpdatedAtUtc)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            EntityKind.Event => (await db.CalendarEvents.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.Start)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            EntityKind.Diagnostic => (await db.DiagnosticSessions.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.UpdatedAtUtc)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            EntityKind.Journal => (await db.TravelJournalEntries.AsNoTracking().Where(x => pattern == null || EF.Functions.Like(x.Title, pattern)).OrderByDescending(x => x.EntryDate)
                .Take(take).Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
            _ => [],
        };
        return rows.Select(r => new EntityInfo(new EntityRef(kind, r.Id), r.Name, IconOf(kind), r.Accent ?? AccentOf(kind))).ToList();
    }

    private static async Task<List<(int Id, string Name, string? Accent)>> NamesAsync(ArcadiaDbContext db, EntityKind kind, List<int> ids, CancellationToken ct) => kind switch
    {
        EntityKind.Game => (await db.Games.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        EntityKind.Project => (await db.Projects.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
        EntityKind.Document => (await db.Documents.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.DisplayName, (string?)null)).ToList(),
        EntityKind.Trip => (await db.Trips.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
        EntityKind.Place => (await db.TravelPlaces.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)null)).ToList(),
        EntityKind.Task => (await db.Tasks.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        EntityKind.Note => (await db.Notes.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        EntityKind.Event => (await db.CalendarEvents.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        EntityKind.Diagnostic => (await db.DiagnosticSessions.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        EntityKind.Journal => (await db.TravelJournalEntries.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Title, (string?)null)).ToList(),
        // finance records are named without amounts (chips and links never reveal money)
        EntityKind.Transaction => (await db.FinanceTransactions.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Description, x.Payee }).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => (x.Id, string.IsNullOrWhiteSpace(x.Description) ? x.Payee ?? "—" : x.Description, (string?)null)).ToList(),
        EntityKind.Account => (await db.FinanceAccounts.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
        EntityKind.Recurring => (await db.RecurringItems.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)null)).ToList(),
        EntityKind.Goal => (await db.SavingsGoals.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.Accent }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name, (string?)x.Accent)).ToList(),
        _ => [],
    };
}

/// <summary>
/// One line of text becomes a task, note or event. Dates are understood by <see cref="QuickDateParser"/>; when the text
/// only contained a date, the original text is kept as the title rather than inventing one.
/// </summary>
public sealed class QuickCaptureService(ITaskStore tasks, INoteStore notes, ICalendarStore calendar) : IQuickCaptureService
{
    public async Task<CaptureResult> CaptureAsync(CaptureKind kind, string text, DateTime now, EntityRef? context = null, CancellationToken cancellationToken = default)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0) throw new ArgumentException("Nothing to save.");
        if (kind == CaptureKind.Expense) throw new ArgumentException("Expenses are captured by Finance's quick expense service.");
        switch (kind)
        {
            case CaptureKind.Note:
            {
                var lines = raw.Replace("\r\n", "\n").Split('\n');
                var title = lines[0].Trim();
                if (title.Length > 120) title = title[..120].TrimEnd() + "…";
                var note = await notes.CreateAsync(new NoteDraft(title, raw, Context: context), cancellationToken).ConfigureAwait(false);
                return new CaptureResult(CaptureKind.Note, note.Id, note.Title, null, null, false);
            }
            case CaptureKind.Event:
            {
                var parsed = QuickDateParser.Parse(raw, now);
                var title = TitleOr(parsed, raw);
                var date = parsed.Date ?? DateOnly.FromDateTime(now);
                var draft = parsed.Time is { } t
                    ? new EventDraft(title, date.ToDateTime(t), date.ToDateTime(t).AddHours(1), Context: context)
                    : new EventDraft(title, date.ToDateTime(TimeOnly.MinValue), date.ToDateTime(TimeOnly.MinValue), IsAllDay: true, Context: context);
                var ev = await calendar.CreateEventAsync(draft, cancellationToken).ConfigureAwait(false);
                return new CaptureResult(CaptureKind.Event, ev.Id, ev.Title, date, parsed.Time, parsed.Ambiguous);
            }
            default:
            {
                var parsed = QuickDateParser.Parse(raw, now);
                var title = TitleOr(parsed, raw);
                var task = await tasks.CreateAsync(new TaskItemDraft(title, DueDate: parsed.Date, DueTime: parsed.Date == null ? null : parsed.Time, Context: context), cancellationToken)
                    .ConfigureAwait(false);
                return new CaptureResult(CaptureKind.Task, task.Id, task.Title, parsed.Date, parsed.Time, parsed.Ambiguous);
            }
        }
    }

    private static string TitleOr(QuickDate parsed, string raw)
    {
        var title = parsed.Title.Length > 0 ? parsed.Title : raw;
        return title.Length > 300 ? title[..300] : title;
    }
}
