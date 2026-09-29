using System.Text;
using System.Text.RegularExpressions;

namespace Arcadia.Core.Text;

public enum MdBlockKind { Heading, Paragraph, Bullet, Numbered, Task, Code, Quote, Rule, Blank }

public enum MdInlineKind { Text, Bold, Italic, BoldItalic, Code, Link }

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

/// <summary>A rendered block. <see cref="Level"/> is the heading level (1–3) or list indentation (0–3).</summary>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdInline> Inlines, int Level = 0, bool Checked = false, int Number = 0, string? Code = null);

/// <summary>
/// A small, predictable Markdown subset for notes: headings, bold/italic, inline code, links, bullet/numbered
/// lists, task checkboxes, code fences, quotes and rules. Anything else is plain text — notes never need Markdown.
/// Links are returned as data only; the UI decides whether to open them (http/https only).
/// </summary>
public static partial class MarkdownLite
{
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, Inlines(paragraph.ToString())));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            int indent = Math.Min(3, (line.Length - trimmed.Length) / 2);

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Append(code.Length > 0 ? "\n" : string.Empty).Append(lines[i]);
                blocks.Add(new MdBlock(MdBlockKind.Code, [], Code: code.ToString()));
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                if (blocks.Count > 0 && blocks[^1].Kind != MdBlockKind.Blank) blocks.Add(new MdBlock(MdBlockKind.Blank, []));
                continue;
            }
            if (HeadingPattern().Match(trimmed) is { Success: true } h)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, Inlines(h.Groups[2].Value), Math.Min(3, h.Groups[1].Length)));
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Rule, []));
                continue;
            }
            if (TaskPattern().Match(trimmed) is { Success: true } t)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Task, Inlines(t.Groups[2].Value), indent, Checked: t.Groups[1].Value is "x" or "X"));
                continue;
            }
            if (BulletPattern().Match(trimmed) is { Success: true } b)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, Inlines(b.Groups[1].Value), indent));
                continue;
            }
            if (NumberPattern().Match(trimmed) is { Success: true } n)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Numbered, Inlines(n.Groups[2].Value), indent, Number: int.TryParse(n.Groups[1].Value, out var num) ? num : 1));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, Inlines(trimmed.TrimStart('>').TrimStart())));
                continue;
            }
            paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(trimmed.TrimEnd());
        }
        FlushParagraph();
        while (blocks.Count > 0 && blocks[^1].Kind == MdBlockKind.Blank) blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    /// <summary>Inline spans: `code`, [text](url), ***bold italic***, **bold**, *italic* / _italic_.</summary>
    public static IReadOnlyList<MdInline> Inlines(string text)
    {
        var result = new List<MdInline>();
        int pos = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > pos) result.Add(new MdInline(MdInlineKind.Text, text[pos..m.Index]));
            if (m.Groups["code"].Success) result.Add(new MdInline(MdInlineKind.Code, m.Groups["code"].Value));
            else if (m.Groups["ltext"].Success) result.Add(new MdInline(MdInlineKind.Link, m.Groups["ltext"].Value, m.Groups["lurl"].Value));
            else if (m.Groups["bi"].Success) result.Add(new MdInline(MdInlineKind.BoldItalic, m.Groups["bi"].Value));
            else if (m.Groups["b"].Success) result.Add(new MdInline(MdInlineKind.Bold, m.Groups["b"].Value));
            else if (m.Groups["i"].Success) result.Add(new MdInline(MdInlineKind.Italic, m.Groups["i"].Value));
            else if (m.Groups["i2"].Success) result.Add(new MdInline(MdInlineKind.Italic, m.Groups["i2"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add(new MdInline(MdInlineKind.Text, text[pos..]));
        return result;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(\d{1,4})[.)]\s+(.*)$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\)|\*\*\*(?<bi>[^*]+)\*\*\*|\*\*(?<b>[^*]+)\*\*|(?<![\p{L}\p{N}*])\*(?<i>[^*\s][^*]*)\*(?![\p{L}\p{N}*])|(?<![\p{L}\p{N}])_(?<i2>[^_\s][^_]*)_(?![\p{L}\p{N}])")]
    private static partial Regex InlinePattern();
}
