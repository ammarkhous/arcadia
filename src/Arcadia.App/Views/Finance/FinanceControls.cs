using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Arcadia.App.Converters;
using Arcadia.App.Services;

namespace Arcadia.App.Views.Finance;

/// <summary>
/// A money value on screen: tabular figures, left-to-right in every language, and hidden ("••••••") while Finance
/// privacy mode is on. Bind <see cref="Money"/> to an already formatted amount ("€1,250.00").
/// </summary>
public sealed class MoneyText : TextBlock
{
    public static readonly DependencyProperty MoneyProperty = DependencyProperty.Register(nameof(Money), typeof(string), typeof(MoneyText), new PropertyMetadata(null, OnMoneyChanged));

    /// <summary>
    /// A bare amount stays left-to-right; a sentence around amounts ("… من …") follows the page's direction so the words
    /// are ordered correctly in Arabic.
    /// </summary>
    private static void OnMoneyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string ?? string.Empty;
        if (text.Any(c => c is >= '؀' and <= 'ۿ' && !char.IsDigit(c))) d.ClearValue(FlowDirectionProperty);
        else d.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
    }

    public string? Money { get => (string?)GetValue(MoneyProperty); set => SetValue(MoneyProperty, value); }

    private static readonly IMultiValueConverter MaskConverter = new Masker();

    public MoneyText()
    {
        FlowDirection = FlowDirection.LeftToRight;
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        // amounts keep Western digits in every language (the formatted separators belong to them)
        NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);
        SetBinding(TextProperty, new MultiBinding
        {
            Converter = MaskConverter,
            Bindings =
            {
                new Binding(nameof(Money)) { Source = this },
                new Binding(nameof(FinancePrivacy.IsHidden)) { Source = FinancePrivacy.Instance },
            },
        });
    }

    private sealed class Masker : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            FinancePrivacy.Apply(values.Length > 0 ? values[0] as string : null, values.Length > 1 && values[1] is true);

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
    }
}

/// <summary>Text colour for figures: no tone = the normal ink; a tone name = that tone (income green, over-budget red…).</summary>
public sealed class InkConverter : IValueConverter
{
    private static readonly Brush Default = MeterBar.Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0xE7, 0xF3)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } tone ? MeterBar.Frozen(new SolidColorBrush(ToneToColorConverter.Resolve(tone))) : Default;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A thin budget/goal meter: a dark track, a fill in the tone, and — past 100 % — the whole bar in the over tone with a tick where the limit was.</summary>
public sealed class MeterBar : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0.0, Render));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(string), typeof(MeterBar), new FrameworkPropertyMetadata("Cyan", Render));
    public static readonly DependencyProperty OverToneProperty = DependencyProperty.Register(nameof(OverTone), typeof(string), typeof(MeterBar), new FrameworkPropertyMetadata("Red", Render));

    /// <summary>0 = empty, 1 = full, above 1 = over the limit.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public string OverTone { get => (string)GetValue(OverToneProperty); set => SetValue(OverToneProperty, value); }

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x13, 0x1E)));
    private static readonly Pen TrackEdge = FrozenPen(Color.FromRgb(0x1C, 0x2A, 0x3D), 1);

    public MeterBar()
    {
        Height = 6;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;
        var r = Math.Min(h / 2, 3);
        dc.DrawRoundedRectangle(Track, TrackEdge, new Rect(0.5, 0.5, w - 1, h - 1), r, r);
        var v = double.IsFinite(Value) ? Math.Max(0, Value) : 0;
        if (v <= 0) return;
        var over = v > 1;
        var color = ToneToColorConverter.Resolve(over ? OverTone : Tone);
        var fill = new LinearGradientBrush(Color.FromArgb(0xFF, color.R, color.G, color.B), Color.FromArgb(0xB0, color.R, color.G, color.B), 0);
        fill.Freeze();
        var fw = over ? w - 1 : Math.Max(h, (w - 1) * Math.Min(1, v));
        dc.DrawRoundedRectangle(fill, null, new Rect(0.5, 0.5, fw, h - 1), r, r);
        if (over)
        {
            // where 100 % was
            var x = Math.Round((w - 1) / v) + 0.5;
            dc.DrawLine(FrozenPen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), 1.5), new Point(x, -1), new Point(x, h + 1));
        }
    }

    internal static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    internal static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }
}

/// <summary>One series of a <see cref="FinanceChart"/>: bars or a line, in one of Arcadia's tones.</summary>
public sealed record ChartSeries(string Label, IReadOnlyList<double> Values, string Tone, bool IsLine = false);

/// <summary>
/// Arcadia's report chart: a sunken well, thin axes, faint grid lines at round values, grouped bars and/or lines, labels
/// under each column and a readout for the column under the mouse. Values on the axis and in the readout are hidden in
/// privacy mode (the shapes stay). Time runs left to right in every language.
/// </summary>
public sealed class FinanceChart : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(FinanceChart), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(IReadOnlyList<string>), typeof(FinanceChart), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty FormatterProperty = DependencyProperty.Register(nameof(Formatter), typeof(Func<double, string>), typeof(FinanceChart), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty AxisFormatterProperty = DependencyProperty.Register(nameof(AxisFormatter), typeof(Func<double, string>), typeof(FinanceChart), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty HideValuesProperty = DependencyProperty.Register(nameof(HideValues), typeof(bool), typeof(FinanceChart), new FrameworkPropertyMetadata(false, Render));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<string>? Labels { get => (IReadOnlyList<string>?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    /// <summary>Full values for the readout ("€1,250.00").</summary>
    public Func<double, string>? Formatter { get => (Func<double, string>?)GetValue(FormatterProperty); set => SetValue(FormatterProperty, value); }
    /// <summary>Short values for the axis ("€1.2k").</summary>
    public Func<double, string>? AxisFormatter { get => (Func<double, string>?)GetValue(AxisFormatterProperty); set => SetValue(AxisFormatterProperty, value); }
    public bool HideValues { get => (bool)GetValue(HideValuesProperty); set => SetValue(HideValuesProperty, value); }

    private static readonly Brush Well = MeterBar.Frozen(new LinearGradientBrush(Color.FromRgb(0x07, 0x0B, 0x12), Color.FromRgb(0x0A, 0x10, 0x19), 90));
    private static readonly Pen WellEdge = MeterBar.FrozenPen(Color.FromRgb(0x1A, 0x26, 0x38), 1);
    private static readonly Pen Grid = MeterBar.FrozenPen(Color.FromArgb(0x2C, 0x5A, 0x7C, 0xA8), 1);
    private static readonly Pen Axis = MeterBar.FrozenPen(Color.FromArgb(0x70, 0x6F, 0x8C, 0xB0), 1);
    private static readonly Brush LabelBrush = MeterBar.Frozen(new SolidColorBrush(Color.FromRgb(0x7D, 0x92, 0xAE)));
    private static readonly Brush ReadoutBrush = MeterBar.Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x0C, 0x14, 0x20)));
    private static readonly Pen ReadoutEdge = MeterBar.FrozenPen(Color.FromRgb(0x2E, 0x46, 0x66), 1);
    private static readonly Brush HoverBrush = MeterBar.Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0x45, 0xD2, 0xFF)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private int _hover = -1;
    private Rect _plot;

    public FinanceChart()
    {
        FlowDirection = FlowDirection.LeftToRight;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MinHeight = 120;
        Focusable = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var n = Labels?.Count ?? 0;
        var index = -1;
        if (n > 0 && _plot.Width > 0)
        {
            var p = e.GetPosition(this);
            if (_plot.Contains(p) || (p.X >= _plot.Left && p.X <= _plot.Right)) index = Math.Clamp((int)((p.X - _plot.Left) / (_plot.Width / n)), 0, n - 1);
        }
        if (index != _hover) { _hover = index; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width < 40 || size.Height < 40) return;
        dc.DrawRoundedRectangle(Well, WellEdge, new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), 3, 3);
        var series = Series?.Where(s => s.Values.Count > 0).ToList() ?? [];
        var labels = Labels ?? [];
        var n = labels.Count;
        if (n == 0 || series.Count == 0)
        {
            var dash = Text("—", 14, LabelBrush);
            dc.DrawText(dash, new Point((size.Width - dash.Width) / 2, (size.Height - dash.Height) / 2));
            return;
        }

        var values = series.SelectMany(s => s.Values.Take(n)).Where(double.IsFinite).ToList();
        var min = Math.Min(0, values.DefaultIfEmpty(0).Min());
        var max = Math.Max(0, values.DefaultIfEmpty(0).Max());
        if (max - min < 1e-9) max = min + 1;
        var step = NiceStep((max - min) / 4);
        var top = Math.Ceiling(max / step) * step;
        var bottom = Math.Floor(min / step) * step;
        var axis = AxisFormatter ?? (v => v.ToString("0", CultureInfo.CurrentCulture));
        var ticks = new List<double>();
        for (var v = bottom; v <= top + step / 2; v += step) ticks.Add(Math.Round(v, 6));
        var tickTexts = ticks.Select(t => Text(HideValues ? "•••" : axis(t), 10, LabelBrush)).ToList();
        var left = 10 + tickTexts.Max(t => t.Width);
        _plot = new Rect(left, 10, Math.Max(10, size.Width - left - 10), Math.Max(10, size.Height - 10 - 22));
        double Y(double v) => _plot.Bottom - (v - bottom) / (top - bottom) * _plot.Height;

        // grid and axis
        for (var i = 0; i < ticks.Count; i++)
        {
            var y = Math.Round(Y(ticks[i])) + 0.5;
            dc.DrawLine(Math.Abs(ticks[i]) < 1e-9 ? Axis : Grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.DrawText(tickTexts[i], new Point(_plot.Left - 6 - tickTexts[i].Width, y - tickTexts[i].Height / 2));
        }
        dc.DrawLine(Axis, new Point(Math.Round(_plot.Left) + 0.5, _plot.Top), new Point(Math.Round(_plot.Left) + 0.5, _plot.Bottom));

        var group = _plot.Width / n;
        if (_hover >= 0 && _hover < n) dc.DrawRectangle(HoverBrush, null, new Rect(_plot.Left + _hover * group, _plot.Top, group, _plot.Height));

        // bars, grouped
        var bars = series.Where(s => !s.IsLine).ToList();
        if (bars.Count > 0)
        {
            var inner = group * (n > 18 ? 0.8 : 0.66);
            var bw = Math.Max(1.5, inner / bars.Count - (bars.Count > 1 ? 2 : 0));
            for (var b = 0; b < bars.Count; b++)
            {
                var color = ToneToColorConverter.Resolve(bars[b].Tone);
                var fill = MeterBar.Frozen(new LinearGradientBrush(Color.FromArgb(0xF0, color.R, color.G, color.B), Color.FromArgb(0x90, color.R, color.G, color.B), 90));
                for (var i = 0; i < Math.Min(n, bars[b].Values.Count); i++)
                {
                    var v = bars[b].Values[i];
                    if (!double.IsFinite(v) || Math.Abs(v) < 1e-12) continue;
                    var x = _plot.Left + i * group + (group - inner) / 2 + b * (inner / bars.Count);
                    var y0 = Y(0);
                    var y1 = Y(v);
                    dc.DrawRectangle(fill, null, new Rect(x, Math.Min(y0, y1), bw, Math.Max(1, Math.Abs(y1 - y0))));
                }
            }
        }

        // lines
        foreach (var line in series.Where(s => s.IsLine))
        {
            var color = ToneToColorConverter.Resolve(line.Tone);
            var points = Enumerable.Range(0, Math.Min(n, line.Values.Count)).Where(i => double.IsFinite(line.Values[i]))
                .Select(i => new Point(_plot.Left + (i + 0.5) * group, Y(line.Values[i]))).ToList();
            if (points.Count == 0) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(points[0], false, false);
                foreach (var p in points.Skip(1)) g.LineTo(p, true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, MeterBar.FrozenPen(Color.FromArgb(0x40, color.R, color.G, color.B), 4), geo);
            dc.DrawGeometry(null, MeterBar.FrozenPen(Arcadia.UI.Media.Tones.Mix(color, Colors.White, 0.2), 1.6), geo);
            var dot = MeterBar.Frozen(new SolidColorBrush(color));
            foreach (var p in points) dc.DrawEllipse(dot, null, p, 2.4, 2.4);
        }

        // x labels (thinned out when crowded)
        var widest = labels.Max(l => Text(l, 10, LabelBrush).Width) + 8;
        var every = Math.Max(1, (int)Math.Ceiling(widest / group));
        for (var i = 0; i < n; i += every)
        {
            var t = Text(labels[i], 10, i == _hover ? Brushes.White : LabelBrush);
            dc.DrawText(t, new Point(_plot.Left + (i + 0.5) * group - t.Width / 2, _plot.Bottom + 5));
        }

        // readout for the hovered column
        if (_hover >= 0 && _hover < n)
        {
            var format = Formatter ?? (v => v.ToString("N2", CultureInfo.CurrentCulture));
            var lines = new List<(FormattedText Text, Color Tone)> { (Text(labels[_hover], 11, Brushes.White, bold: true), Colors.Transparent) };
            foreach (var s in series)
                if (_hover < s.Values.Count)
                    lines.Add((Text($"{s.Label}  {(HideValues ? FinancePrivacy.Mask : format(s.Values[_hover]))}", 11, LabelBrush), ToneToColorConverter.Resolve(s.Tone)));
            var w = lines.Max(l => l.Text.Width) + 26;
            var h = lines.Sum(l => l.Text.Height) + 12;
            var x = _plot.Left + (_hover + 0.5) * group + 10;
            if (x + w > size.Width - 4) x = _plot.Left + (_hover + 0.5) * group - 10 - w;
            x = Math.Max(4, x);
            var box = new Rect(x, _plot.Top + 2, w, h);
            dc.DrawRoundedRectangle(ReadoutBrush, ReadoutEdge, box, 3, 3);
            var y = box.Top + 6;
            foreach (var (text, tone) in lines)
            {
                if (tone != Colors.Transparent) dc.DrawRectangle(MeterBar.Frozen(new SolidColorBrush(tone)), null, new Rect(box.Left + 8, y + text.Height / 2 - 3, 6, 6));
                dc.DrawText(text, new Point(box.Left + (tone == Colors.Transparent ? 8 : 20), y));
                y += text.Height;
            }
        }
    }

    /// <summary>1, 2, 2.5 or 5 times a power of ten: grid lines at values people read easily.</summary>
    internal static double NiceStep(double raw)
    {
        if (raw <= 0 || !double.IsFinite(raw)) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var f in new[] { 1, 2, 2.5, 5, 10 })
            if (f * mag >= raw) return f * mag;
        return 10 * mag;
    }
}
