using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcadia.Core.ItTools.Reports;

/// <summary>
/// Format-neutral result of a tool: labelled fields, an optional table and optional preformatted text.
/// Every tool produces one for Copy/Export and for diagnostic sessions; exporters turn documents into files,
/// so a PDF exporter can be added later without touching the tools.
/// </summary>
public sealed class ReportSection
{
    public string Heading { get; set; } = string.Empty;
    public List<ReportField> Fields { get; set; } = [];
    public ReportTable? Table { get; set; }
    /// <summary>Monospaced text (e.g. ping output) reproduced exactly.</summary>
    public string? Preformatted { get; set; }
    public List<ReportSection> Children { get; set; } = [];

    public ReportSection() { }

    public ReportSection(string heading) => Heading = heading;

    public ReportSection Add(string label, string? value)
    {
        Fields.Add(new ReportField(label, string.IsNullOrWhiteSpace(value) ? "—" : value));
        return this;
    }
}

public sealed record ReportField(string Label, string Value);

public sealed class ReportTable
{
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];

    public ReportTable() { }

    public ReportTable(params string[] columns) => Columns = [.. columns];

    public ReportTable AddRow(params string?[] cells)
    {
        Rows.Add(cells.Select(c => string.IsNullOrWhiteSpace(c) ? "—" : c!).ToList());
        return this;
    }
}

public sealed class ReportDocument
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public string? GeneratedBy { get; set; }
    public List<ReportSection> Sections { get; set; } = [];
}

/// <summary>Turns a report into a file format. Implementations must not contact any external service.</summary>
public interface IReportExporter
{
    /// <summary>"txt", "json" (later "pdf").</summary>
    string Format { get; }
    string FileExtension { get; }
    string FileFilter { get; }
    Task WriteAsync(ReportDocument document, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class TextReportExporter : IReportExporter
{
    public string Format => "txt";
    public string FileExtension => ".txt";
    public string FileFilter => "Text (*.txt)|*.txt";

    public async Task WriteAsync(ReportDocument document, Stream destination, CancellationToken cancellationToken = default)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(Render(document));
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static string Render(ReportDocument document)
    {
        var sb = new StringBuilder();
        var rule = new string('=', Math.Max(40, Math.Min(78, document.Title.Length + 8)));
        sb.AppendLine(rule).AppendLine(document.Title);
        if (!string.IsNullOrWhiteSpace(document.Subtitle)) sb.AppendLine(document.Subtitle);
        sb.AppendLine(document.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                      + (document.GeneratedBy is { } by ? "  ·  " + by : string.Empty));
        sb.AppendLine(rule);
        foreach (var section in document.Sections)
        {
            sb.AppendLine();
            AppendSection(sb, section, 0);
        }
        return sb.ToString();
    }

    /// <summary>Plain text of one section (used by the tools' Copy All).</summary>
    public static string Render(ReportSection section)
    {
        var sb = new StringBuilder();
        AppendSection(sb, section, 0);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendSection(StringBuilder sb, ReportSection section, int depth)
    {
        if (!string.IsNullOrWhiteSpace(section.Heading))
        {
            sb.AppendLine(section.Heading);
            sb.AppendLine(new string(depth == 0 ? '-' : '·', Math.Min(78, Math.Max(3, section.Heading.Length))));
        }
        if (section.Fields.Count > 0)
        {
            int width = section.Fields.Max(f => f.Label.Length) + 2;
            foreach (var f in section.Fields)
            {
                var lines = f.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                sb.Append((f.Label + ":").PadRight(width)).AppendLine(lines[0]);
                foreach (var more in lines.Skip(1)) sb.Append(' ', width).AppendLine(more);
            }
        }
        if (section.Table is { } table && table.Columns.Count > 0)
        {
            if (section.Fields.Count > 0) sb.AppendLine();
            AppendTable(sb, table);
        }
        if (!string.IsNullOrEmpty(section.Preformatted))
        {
            if (section.Fields.Count > 0 || section.Table != null) sb.AppendLine();
            sb.AppendLine(section.Preformatted.TrimEnd());
        }
        foreach (var child in section.Children)
        {
            sb.AppendLine();
            AppendSection(sb, child, depth + 1);
        }
    }

    public static void AppendTable(StringBuilder sb, ReportTable table)
    {
        var widths = table.Columns.Select((c, i) => Math.Min(60, Math.Max(c.Length, table.Rows.Select(r => i < r.Count ? r[i].Length : 0).DefaultIfEmpty(0).Max()))).ToArray();
        void Row(IReadOnlyList<string> cells)
        {
            for (int i = 0; i < widths.Length; i++)
            {
                var cell = i < cells.Count ? cells[i] : string.Empty;
                if (cell.Length > widths[i]) cell = cell[..(widths[i] - 1)] + "…";
                sb.Append(i == widths.Length - 1 ? cell : cell.PadRight(widths[i] + 2));
            }
            sb.AppendLine();
        }
        Row(table.Columns);
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in table.Rows) Row(r);
    }
}

public sealed class JsonReportExporter : IReportExporter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep Arabic/German text readable in the file instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Format => "json";
    public string FileExtension => ".json";
    public string FileFilter => "JSON (*.json)|*.json";

    public Task WriteAsync(ReportDocument document, Stream destination, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(destination, document, Options, cancellationToken);

    public static string Serialize(ReportSection section) => JsonSerializer.Serialize(section, Options);

    public static ReportSection? DeserializeSection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReportSection>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
