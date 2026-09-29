using Arcadia.Core.Organizer;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Reports;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

/// <summary>SQLite persistence for IT Tools diagnostic sessions.</summary>
public sealed class DiagnosticSessionStore(IDbContextFactory<ArcadiaDbContext> contextFactory, TimeProvider time) : IDiagnosticSessionStore
{
    public const int MaxNoteLength = 8000;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<DiagnosticSessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DiagnosticSessions.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAtUtc)
            .Select(s => new DiagnosticSessionSummary(s.Id, s.Title, s.Reference, s.CreatedAtUtc, s.UpdatedAtUtc, s.ClosedAtUtc, s.Results.Count, s.Notes.Count))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticSession?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.DiagnosticSessions.AsNoTracking()
            .Include(s => s.Results)
            .Include(s => s.Notes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (session == null) return null;
        session.Results = [.. session.Results.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)];
        session.Notes = [.. session.Notes.OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id)];
        return session;
    }

    public async Task<DiagnosticSession> CreateAsync(string title, string? reference, string computerName, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var session = new DiagnosticSession
        {
            Title = Clip(title, 200),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : Clip(reference, 200),
            ComputerName = Clip(computerName, 128),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.DiagnosticSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task UpdateDetailsAsync(int id, string title, string? reference, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.DiagnosticSessions.FindAsync([id], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        session.Title = Clip(title, 200);
        session.Reference = string.IsNullOrWhiteSpace(reference) ? null : Clip(reference, 200);
        session.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetClosedAsync(int id, bool closed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.DiagnosticSessions.FindAsync([id], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        session.ClosedAtUtc = closed ? Now : null;
        session.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DiagnosticSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticResult> AddResultAsync(int sessionId, NewDiagnosticResult result, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.DiagnosticSessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var entity = new DiagnosticResult
        {
            SessionId = sessionId,
            Tool = Clip(result.Tool, 32),
            Title = Clip(result.Title, 300),
            Summary = result.Summary == null ? null : Clip(result.Summary, 500),
            Outcome = result.Outcome,
            SectionJson = JsonReportExporter.Serialize(result.Section),
            CreatedAtUtc = Now,
        };
        db.DiagnosticResults.Add(entity);
        session.UpdatedAtUtc = entity.CreatedAtUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity;
    }

    public async Task DeleteResultAsync(int resultId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DiagnosticResults.Where(r => r.Id == resultId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticNote> AddNoteAsync(int sessionId, string text, CancellationToken cancellationToken = default)
    {
        ValidateNote(text);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.DiagnosticSessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var now = Now;
        // The note is stored exactly as typed (no trimming inside, no normalisation of scripts).
        var note = new DiagnosticNote { SessionId = sessionId, Text = text, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.DiagnosticNotes.Add(note);
        session.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return note;
    }

    public async Task UpdateNoteAsync(int noteId, string text, CancellationToken cancellationToken = default)
    {
        ValidateNote(text);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.DiagnosticNotes.FindAsync([noteId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        note.Text = text;
        note.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteNoteAsync(int noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DiagnosticNotes.Where(n => n.Id == noteId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A note cannot be empty.", nameof(text));
        if (text.Length > MaxNoteLength) throw new ArgumentException($"A note can be at most {MaxNoteLength} characters.", nameof(text));
    }

    private static string Clip(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
