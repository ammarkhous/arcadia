using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

/// <summary>
/// The notebook view. Code-behind only edits the text box: Markdown formatting around the selection, list continuation
/// on Enter, and link insertion. Saving is the view model's job (debounced autosave).
/// </summary>
public partial class NotesView : UserControl
{
    private NotesViewModel? _vm;
    private static readonly Regex ListLine = new(@"^(\s*)([-*+]|\d+[.)])( \[[ xX]\])?\s+", RegexOptions.Compiled);

    public NotesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) { _vm.PropertyChanged -= OnVmChanged; _vm.TitleFocusRequested -= OnTitleFocus; }
            _vm = DataContext as NotesViewModel;
            if (_vm != null) { _vm.PropertyChanged += OnVmChanged; _vm.TitleFocusRequested += OnTitleFocus; }
            ApplyMode();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotesViewModel.Mode)) ApplyMode();
    }

    private void ApplyMode()
    {
        var mode = _vm?.Mode ?? "Edit";
        EditorColumn.Width = mode == "Preview" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PreviewColumn.Width = mode == "Edit" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void OnTitleFocus(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { TitleBox.Focus(); Keyboard.Focus(TitleBox); TitleBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);

    // ───────────── formatting ─────────────

    private void Wrap(string before, string after, string placeholder)
    {
        var box = Editor;
        var start = box.SelectionStart;
        var selected = box.SelectedText;
        var inner = selected.Length > 0 ? selected : placeholder;
        box.SelectedText = before + inner + after;
        box.Select(start + before.Length, inner.Length);
        box.Focus();
    }

    /// <summary>Adds (or removes) a prefix at the start of every selected line.</summary>
    private void PrefixLines(string prefix)
    {
        var box = Editor;
        var text = box.Text;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, box.SelectionStart - 1)) + 1;
        if (box.SelectionStart == 0) lineStart = 0;
        var end = box.SelectionStart + box.SelectionLength;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
        var block = text[lineStart..lineEnd];
        var lines = block.Split('\n');
        var all = lines.All(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var changed = string.Join('\n', lines.Select(l => all ? l[prefix.Length..] : prefix + l));
        box.Select(lineStart, lineEnd - lineStart);
        box.SelectedText = changed;
        box.Select(lineStart + changed.Length, 0);
        box.Focus();
    }

    private void OnBold(object sender, RoutedEventArgs e) => Wrap("**", "**", Loc("Fmt_BoldSample"));
    private void OnItalic(object sender, RoutedEventArgs e) => Wrap("*", "*", Loc("Fmt_ItalicSample"));
    private void OnHeading(object sender, RoutedEventArgs e) => PrefixLines("## ");
    private void OnBullet(object sender, RoutedEventArgs e) => PrefixLines("- ");
    private void OnCheckbox(object sender, RoutedEventArgs e) => PrefixLines("- [ ] ");
    private void OnCode(object sender, RoutedEventArgs e)
    {
        if (Editor.SelectedText.Contains('\n') || Editor.SelectionLength == 0) Wrap("```\n", "\n```", Loc("Fmt_CodeSample"));
        else Wrap("`", "`", "code");
    }

    private void OnWebLink(object sender, RoutedEventArgs e)
    {
        var text = Editor.SelectedText.Length > 0 ? Editor.SelectedText : Loc("Fmt_LinkSample");
        var start = Editor.SelectionStart;
        Editor.SelectedText = $"[{text}](https://)";
        Editor.Select(start + text.Length + 3, 8); // select "https://" for typing the address
        Editor.Focus();
    }

    private void OnArcadiaLink(object sender, RoutedEventArgs e)
    {
        if (_vm?.PickInternalLink() is not { } link) return;
        var start = Editor.SelectionStart;
        Editor.SelectedText = link;
        Editor.Select(start + link.Length, 0);
        Editor.Focus();
    }

    private static string Loc(string key) => Arcadia.App.Localization.LocalizationManager.Instance[key];

    /// <summary>Ctrl+B / Ctrl+I format; Enter continues a list ("- ", "1. ", "- [ ] "); Enter on an empty item ends the list.</summary>
    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) { OnBold(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.I) { OnItalic(sender, e); e.Handled = true; return; }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || Editor.SelectionLength > 0) return;
        var text = Editor.Text;
        var caret = Editor.CaretIndex;
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var line = text[lineStart..caret];
        var m = ListLine.Match(line);
        if (!m.Success) return;
        if (line.Length == m.Length)
        {
            // an empty item: end the list
            Editor.Select(lineStart, line.Length);
            Editor.SelectedText = string.Empty;
            e.Handled = true;
            return;
        }
        var marker = m.Groups[2].Value;
        if (int.TryParse(marker.TrimEnd('.', ')'), out var n)) marker = (n + 1) + marker[^1..];
        var next = "\n" + m.Groups[1].Value + marker + (m.Groups[3].Success ? " [ ]" : string.Empty) + " ";
        Editor.SelectedText = next;
        Editor.CaretIndex = caret + next.Length;
        e.Handled = true;
    }
}
