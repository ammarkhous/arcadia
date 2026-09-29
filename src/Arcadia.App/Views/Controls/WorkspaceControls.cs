using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Arcadia.App.Services;
using Arcadia.Core.Projects;
using Arcadia.Core.Text;

namespace Arcadia.App.Views.Controls;

/// <summary>Windows' own icon for a file type (or folder), looked up by extension and cached.</summary>
public sealed class FileIcon : Image
{
    public static readonly DependencyProperty ExtensionProperty = DependencyProperty.Register(nameof(Extension), typeof(string), typeof(FileIcon), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsFolderProperty = DependencyProperty.Register(nameof(IsFolder), typeof(bool), typeof(FileIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty LargeProperty = DependencyProperty.Register(nameof(Large), typeof(bool), typeof(FileIcon), new PropertyMetadata(false, Changed));

    public string? Extension { get => (string?)GetValue(ExtensionProperty); set => SetValue(ExtensionProperty, value); }
    public bool IsFolder { get => (bool)GetValue(IsFolderProperty); set => SetValue(IsFolderProperty, value); }
    public bool Large { get => (bool)GetValue(LargeProperty); set => SetValue(LargeProperty, value); }

    public FileIcon()
    {
        Width = 16;
        Height = 16;
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        FlowDirection = FlowDirection.LeftToRight; // icons never mirror
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (FileIcon)d;
        icon.Source = ShellFileInfo.Icon(icon.Extension ?? string.Empty, icon.IsFolder, icon.Large);
    }
}

/// <summary>
/// Renders a note's lightweight Markdown into a read-only, selectable FlowDocument in Arcadia's colours.
/// Only http(s) links (opened in the browser) and internal arcadia:// links are clickable; nothing is downloaded or embedded.
/// </summary>
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    public string? Markdown { get => (string?)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    private static readonly Brush Text = Frozen(Color.FromRgb(0xDC, 0xE6, 0xF2));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8F, 0xA3, 0xBF));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x7C, 0xC4, 0xFF));
    private static readonly Brush CodeBack = Frozen(Color.FromRgb(0x06, 0x0A, 0x10));
    private static readonly Brush CodeText = Frozen(Color.FromRgb(0xB9, 0xD4, 0xF0));
    private static readonly Brush Rule = Frozen(Color.FromRgb(0x26, 0x34, 0x4A));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public MarkdownView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        IsToolBarVisible = false;
        Focusable = true;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Render()
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Text,
            Background = Brushes.Transparent,
            PagePadding = new Thickness(4, 2, 8, 12),
            LineHeight = 22,
            TextAlignment = TextAlignment.Left,
        };
        List? list = null;
        MdBlockKind? listKind = null;
        foreach (var block in MarkdownLite.Parse(Markdown))
        {
            bool isList = block.Kind is MdBlockKind.Bullet or MdBlockKind.Numbered or MdBlockKind.Task;
            if (!isList || listKind != block.Kind) { list = null; listKind = null; }
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    doc.Blocks.Add(Para(block.Inlines, block.Level switch { 1 => 22, 2 => 18, _ => 15.5 }, FontWeights.SemiBold, new Thickness(0, 10, 0, 4), Accent));
                    break;
                case MdBlockKind.Paragraph:
                    doc.Blocks.Add(Para(block.Inlines, 14, FontWeights.Normal, new Thickness(0, 0, 0, 8)));
                    break;
                case MdBlockKind.Quote:
                    var q = Para(block.Inlines, 14, FontWeights.Normal, new Thickness(0, 0, 0, 8), Dim);
                    q.BorderBrush = Accent;
                    q.BorderThickness = new Thickness(3, 0, 0, 0);
                    q.Padding = new Thickness(10, 2, 0, 2);
                    doc.Blocks.Add(q);
                    break;
                case MdBlockKind.Code:
                    doc.Blocks.Add(new Paragraph(new Run(block.Code ?? string.Empty))
                    {
                        FontFamily = Mono, FontSize = 12.5, Foreground = CodeText, Background = CodeBack, Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 2, 0, 10), BorderBrush = Rule, BorderThickness = new Thickness(1),
                    });
                    break;
                case MdBlockKind.Rule:
                    doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 6, 0, 10) }));
                    break;
                case MdBlockKind.Bullet or MdBlockKind.Numbered or MdBlockKind.Task:
                    if (list == null)
                    {
                        list = new List
                        {
                            MarkerStyle = block.Kind == MdBlockKind.Numbered ? TextMarkerStyle.Decimal : block.Kind == MdBlockKind.Task ? TextMarkerStyle.None : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(block.Kind == MdBlockKind.Task ? 4 : 22, 0, 0, 0),
                            StartIndex = Math.Max(1, block.Number),
                        };
                        listKind = block.Kind;
                        doc.Blocks.Add(list);
                    }
                    var p = Para(block.Inlines, 14, FontWeights.Normal, new Thickness(block.Level * 18, 0, 0, 2));
                    if (block.Kind == MdBlockKind.Task)
                    {
                        p.Inlines.InsertBefore(p.Inlines.FirstInline, new Run(block.Checked ? "☑  " : "☐  ") { Foreground = block.Checked ? Accent : Dim });
                        if (block.Checked) p.Foreground = Dim;
                    }
                    list.ListItems.Add(new ListItem(p));
                    break;
            }
        }
        Document = doc;
    }

    private Paragraph Para(IReadOnlyList<MdInline> inlines, double size, FontWeight weight, Thickness margin, Brush? brush = null)
    {
        var p = new Paragraph { FontSize = size, FontWeight = weight, Margin = margin };
        if (brush != null) p.Foreground = brush;
        foreach (var i in inlines)
        {
            Inline run = i.Kind switch
            {
                MdInlineKind.Bold => new Bold(new Run(i.Text)),
                MdInlineKind.Italic => new Italic(new Run(i.Text)),
                MdInlineKind.BoldItalic => new Bold(new Italic(new Run(i.Text))),
                MdInlineKind.Code => new Run(i.Text) { FontFamily = Mono, FontSize = size - 1, Background = CodeBack, Foreground = CodeText },
                MdInlineKind.Link => MakeLink(i),
                _ => new Run(i.Text),
            };
            p.Inlines.Add(run);
        }
        return p;
    }

    /// <summary>Opens <c>arcadia://kind/id</c> links inside the app (set by the shell). Such links never leave Arcadia.</summary>
    public static Action<Arcadia.Core.Organizer.EntityRef>? OpenInternal { get; set; }

    private static Inline MakeLink(MdInline i)
    {
        if (Arcadia.Core.Organizer.EntityRef.TryParse(i.Url, out var target))
        {
            var inner = new Hyperlink(new Run(i.Text)) { Foreground = Accent, ToolTip = target.ToString(), Cursor = System.Windows.Input.Cursors.Hand };
            inner.Click += (_, _) => OpenInternal?.Invoke(target);
            return inner;
        }
        if (!LinkRules.TryNormalize(i.Url, out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new Run(i.Text);
        var link = new Hyperlink(new Run(i.Text)) { NavigateUri = uri, Foreground = Accent, ToolTip = url };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
            e.Handled = true;
        };
        return link;
    }
}

/// <summary>Marks the text box Ctrl+F should focus on the current page.</summary>
public static class Shortcuts
{
    public static readonly DependencyProperty IsSearchBoxProperty = DependencyProperty.RegisterAttached(
        "IsSearchBox", typeof(bool), typeof(Shortcuts), new PropertyMetadata(false));

    public static bool GetIsSearchBox(DependencyObject d) => (bool)d.GetValue(IsSearchBoxProperty);
    public static void SetIsSearchBox(DependencyObject d, bool value) => d.SetValue(IsSearchBoxProperty, value);

    /// <summary>The first visible search box under <paramref name="root"/>.</summary>
    public static TextBox? FindSearchBox(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb && GetIsSearchBox(tb) && tb.IsVisible) return tb;
            if (child is UIElement { IsVisible: false }) continue;
            if (FindSearchBox(child) is { } found) return found;
        }
        return null;
    }
}

/// <summary>Visible when the bound value equals the parameter (enum tabs).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value equals the parameter (tab radio buttons). Two-way on an enum, checking a
/// radio selects its value, so keyboard, clicks and UI Automation (Select) all switch the tab.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && targetType.IsEnum && parameter?.ToString() is { } name && Enum.TryParse(targetType, name, out var result) ? result! : Binding.DoNothing;
}

/// <summary>Packs a row and a value for commands that need both (e.g. set task status from a menu).</summary>
public sealed class PairConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) => values.ToArray();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

/// <summary>
/// A page section that is created the first time it is shown (Key == Current) and kept afterwards. Stacked sections
/// with Collapsed visibility were still built: on a large library Travel generated ~12,000 visuals for hidden trip and
/// timeline lists on every visit.
/// </summary>
public sealed class LazySection : ContentControl
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(string), typeof(LazySection),
        new PropertyMetadata(null, (d, _) => ((LazySection)d).Update()));
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(nameof(Current), typeof(object), typeof(LazySection),
        new PropertyMetadata(null, (d, _) => ((LazySection)d).Update()));
    public static readonly DependencyProperty SectionTemplateProperty = DependencyProperty.Register(nameof(SectionTemplate), typeof(DataTemplate), typeof(LazySection),
        new PropertyMetadata(null, (d, _) => ((LazySection)d).Update()));

    public LazySection()
    {
        Focusable = false;
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
    }

    public string? Key { get => (string?)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public object? Current { get => GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }
    public DataTemplate? SectionTemplate { get => (DataTemplate?)GetValue(SectionTemplateProperty); set => SetValue(SectionTemplateProperty, value); }

    private void Update()
    {
        var shown = Key != null && string.Equals(Current?.ToString(), Key, StringComparison.Ordinal);
        if (shown && Content == null && SectionTemplate != null) Content = SectionTemplate.LoadContent();
        Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }
}
