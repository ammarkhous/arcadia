using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Arcadia.App.Converters;

namespace Arcadia.App.Views.Organizer;

/// <summary>
/// Lays out timed calendar blocks in a day column: vertical position and height from minutes, side-by-side lanes for
/// overlaps. Children set the attached <c>Start</c>, <c>Duration</c>, <c>Lane</c> and <c>Lanes</c> values.
/// </summary>
public sealed class TimelinePanel : Panel
{
    public static readonly DependencyProperty MinuteHeightProperty = DependencyProperty.Register(nameof(MinuteHeight), typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached("Start", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached("Duration", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty LaneProperty = DependencyProperty.RegisterAttached("Lane", typeof(int), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty LanesProperty = DependencyProperty.RegisterAttached("Lanes", typeof(int), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public double MinuteHeight { get => (double)GetValue(MinuteHeightProperty); set => SetValue(MinuteHeightProperty, value); }
    public static double GetStart(DependencyObject d) => (double)d.GetValue(StartProperty);
    public static void SetStart(DependencyObject d, double v) => d.SetValue(StartProperty, v);
    public static double GetDuration(DependencyObject d) => (double)d.GetValue(DurationProperty);
    public static void SetDuration(DependencyObject d, double v) => d.SetValue(DurationProperty, v);
    public static int GetLane(DependencyObject d) => (int)d.GetValue(LaneProperty);
    public static void SetLane(DependencyObject d, int v) => d.SetValue(LaneProperty, v);
    public static int GetLanes(DependencyObject d) => (int)d.GetValue(LanesProperty);
    public static void SetLanes(DependencyObject d, int v) => d.SetValue(LanesProperty, v);

    /// <summary>Short events still get enough height for one line of text.</summary>
    public const double MinBlockHeight = 20;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 200 : available.Width;
        foreach (UIElement child in InternalChildren)
        {
            var lanes = Math.Max(1, GetLanes(child));
            child.Measure(new Size(Math.Max(0, width / lanes - 2), BlockHeight(child)));
        }
        return new Size(width, 24 * 60 * MinuteHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            var lanes = Math.Max(1, GetLanes(child));
            var laneWidth = final.Width / lanes;
            var x = GetLane(child) * laneWidth;
            child.Arrange(new Rect(x + 1, GetStart(child) * MinuteHeight + 1, Math.Max(0, laneWidth - 3), BlockHeight(child) - 2));
        }
        return final;
    }

    private double BlockHeight(UIElement child) => Math.Max(MinBlockHeight, GetDuration(child) * MinuteHeight);
}

/// <summary>A tone name ("Blue", "Amber"…) as a brush with the opacity given as parameter ("0.2"): calm fills for event blocks.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = ToneToColorConverter.Resolve(value as string);
        var opacity = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Minutes since midnight × minute height = pixels (the "now" line, scrolling to the working hours).</summary>
public sealed class MinutesToPixelsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is double minutes && values[1] is double perMinute ? new Thickness(0, Math.Max(0, minutes * perMinute), 0, 0) : new Thickness(0);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>
/// An ItemsControl for navigation rails whose automation peer lists its visual children (the rail's radio buttons)
/// directly. WPF's item peers can stay empty for rails created after a combo box was opened through UI Automation,
/// which hid the rail from screen readers; a plain element peer has no such cache.
/// </summary>
public sealed class RailItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}