using Arcadia.Core.ItTools.Reports;

namespace Arcadia.Core.ItTools.Diagnostics;

/// <summary>A troubleshooting session: tool results and notes collected while solving one problem.</summary>
public sealed class DiagnosticSession
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Optional ticket number, customer or device reference.</summary>
    public string? Reference { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    public List<DiagnosticResult> Results { get; set; } = [];
    public List<DiagnosticNote> Notes { get; set; } = [];

    public bool IsClosed => ClosedAtUtc != null;
}

public enum DiagnosticOutcome { Info, Success, Warning, Failure }

/// <summary>One recorded tool result. <see cref="SectionJson"/> holds the full <see cref="ReportSection"/>.</summary>
public sealed class DiagnosticResult
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }
    /// <summary>Tool key ("ping", "dns", …).</summary>
    public string Tool { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public DiagnosticOutcome Outcome { get; set; }
    public string SectionJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A note typed by the user, stored exactly as entered (any language, any direction).</summary>
public sealed class DiagnosticNote
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed record DiagnosticSessionSummary(int Id, string Title, string? Reference, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? ClosedAtUtc, int ResultCount, int NoteCount);

public sealed record NewDiagnosticResult(string Tool, string Title, string? Summary, DiagnosticOutcome Outcome, ReportSection Section);

public interface IDiagnosticSessionStore
{
    Task<IReadOnlyList<DiagnosticSessionSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<DiagnosticSession?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<DiagnosticSession> CreateAsync(string title, string? reference, string computerName, CancellationToken cancellationToken = default);
    Task UpdateDetailsAsync(int id, string title, string? reference, CancellationToken cancellationToken = default);
    Task SetClosedAsync(int id, bool closed, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<DiagnosticResult> AddResultAsync(int sessionId, NewDiagnosticResult result, CancellationToken cancellationToken = default);
    Task DeleteResultAsync(int resultId, CancellationToken cancellationToken = default);

    Task<DiagnosticNote> AddNoteAsync(int sessionId, string text, CancellationToken cancellationToken = default);
    Task UpdateNoteAsync(int noteId, string text, CancellationToken cancellationToken = default);
    Task DeleteNoteAsync(int noteId, CancellationToken cancellationToken = default);
}

/// <summary>Builds the exportable report of a session: header, then results and notes in time order.</summary>
public static class DiagnosticReport
{
    public sealed record Labels(string Session, string Reference, string Computer, string Started, string Closed, string Open, string Note, string Result, string Entries);

    public static ReportDocument Build(DiagnosticSession session, Labels labels, string generatedBy, DateTime nowUtc)
    {
        var doc = new ReportDocument
        {
            Title = session.Title,
            Subtitle = session.Reference,
            GeneratedAtUtc = nowUtc,
            GeneratedBy = generatedBy,
        };

        var header = new ReportSection(labels.Session)
            .Add(labels.Reference, session.Reference)
            .Add(labels.Computer, session.ComputerName)
            .Add(labels.Started, Local(session.CreatedAtUtc))
            .Add(labels.Closed, session.ClosedAtUtc is { } c ? Local(c) : labels.Open)
            .Add(labels.Entries, (session.Results.Count + session.Notes.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
        doc.Sections.Add(header);

        var entries = session.Results.Select(r => (At: r.CreatedAtUtc, Section: ResultSection(r, labels)))
            .Concat(session.Notes.Select(n => (At: n.CreatedAtUtc, Section: NoteSection(n, labels))))
            .OrderBy(e => e.At);
        doc.Sections.AddRange(entries.Select(e => e.Section));
        return doc;
    }

    private static ReportSection ResultSection(DiagnosticResult r, Labels labels)
    {
        var inner = JsonReportExporter.DeserializeSection(r.SectionJson) ?? new ReportSection();
        inner.Heading = $"[{Local(r.CreatedAtUtc)}] {r.Title}";
        if (!string.IsNullOrWhiteSpace(r.Summary)) inner.Fields.Insert(0, new ReportField(labels.Result, r.Summary));
        return inner;
    }

    private static ReportSection NoteSection(DiagnosticNote n, Labels labels) => new($"[{Local(n.CreatedAtUtc)}] {labels.Note}") { Preformatted = n.Text };

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
