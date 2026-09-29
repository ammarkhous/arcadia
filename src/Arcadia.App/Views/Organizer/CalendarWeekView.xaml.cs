using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

/// <summary>
/// Week and day grid. The hour height adapts so the configured working hours (Settings: calendar day start/end) fill the
/// viewport, with the rest of the day a scroll away. Double-click (or right-click) an empty slot to create an event there.
/// </summary>
public partial class CalendarWeekView : UserControl
{
    public static readonly DependencyProperty MinuteHeightProperty = DependencyProperty.Register(nameof(MinuteHeight), typeof(double), typeof(CalendarWeekView),
        new PropertyMetadata(0.8, (d, _) => ((CalendarWeekView)d).UpdateDerived()));
    public static readonly DependencyProperty HourHeightProperty = DependencyProperty.Register(nameof(HourHeight), typeof(double), typeof(CalendarWeekView), new PropertyMetadata(48.0));
    public static readonly DependencyProperty WorkingHeightProperty = DependencyProperty.Register(nameof(WorkingHeight), typeof(double), typeof(CalendarWeekView), new PropertyMetadata(0.0));
    public static readonly DependencyProperty WorkingMarginProperty = DependencyProperty.Register(nameof(WorkingMargin), typeof(Thickness), typeof(CalendarWeekView), new PropertyMetadata(default(Thickness)));

    private const double MinHourHeight = 36;
    private double _rightClickMinutes;
    private DayColumn? _rightClickColumn;
    private bool _scrolled;

    public CalendarWeekView()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, _) => Fit();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { _scrolled = false; Dispatcher.BeginInvoke(Fit, System.Windows.Threading.DispatcherPriority.Loaded); } };
    }

    public double MinuteHeight { get => (double)GetValue(MinuteHeightProperty); set => SetValue(MinuteHeightProperty, value); }
    public double HourHeight { get => (double)GetValue(HourHeightProperty); set => SetValue(HourHeightProperty, value); }
    public double WorkingHeight { get => (double)GetValue(WorkingHeightProperty); set => SetValue(WorkingHeightProperty, value); }
    public Thickness WorkingMargin { get => (Thickness)GetValue(WorkingMarginProperty); set => SetValue(WorkingMarginProperty, value); }

    /// <summary>The day view (one column) rather than the week.</summary>
    public bool IsSingleDay { get; set; }

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    private void Fit()
    {
        if (Vm is not { } vm || Scroller.ViewportHeight <= 0) return;
        var hours = Math.Max(1, vm.DayEndHour - vm.DayStartHour);
        var hourHeight = Math.Max(MinHourHeight, Math.Floor(Scroller.ViewportHeight / hours));
        MinuteHeight = hourHeight / 60;
        if (!_scrolled && IsVisible)
        {
            _scrolled = true;
            Scroller.ScrollToVerticalOffset(vm.DayStartHour * hourHeight);
        }
    }

    private void UpdateDerived()
    {
        HourHeight = MinuteHeight * 60;
        if (Vm is { } vm)
        {
            WorkingMargin = new Thickness(0, vm.DayStartHour * HourHeight, 0, 0);
            WorkingHeight = Math.Max(0, (vm.DayEndHour - vm.DayStartHour) * HourHeight);
        }
    }

    private double MinutesAt(IInputElement column, MouseEventArgs e) => Math.Clamp(e.GetPosition(column).Y / MinuteHeight, 0, 24 * 60 - 1);

    private void OnColumnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { Tag: DayColumn column } fe || Vm is not { } vm) return;
        _ = vm.NewEventAtAsync(column.Date.ToDateTime(TimeOnly.MinValue).AddMinutes(MinutesAt(fe, e)));
        e.Handled = true;
    }

    private void OnColumnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DayColumn column } fe) return;
        _rightClickColumn = column;
        _rightClickMinutes = MinutesAt(fe, e);
    }

    private void OnAllDayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { Tag: DayColumn column } || Vm is not { } vm) return;
        _ = vm.NewEventAtAsync(column.Date.ToDateTime(TimeOnly.MinValue), allDay: true);
        e.Handled = true;
    }

    private void OnMenuNewEvent(object sender, RoutedEventArgs e)
    {
        if (_rightClickColumn is { } c && Vm is { } vm) _ = vm.NewEventAtAsync(c.Date.ToDateTime(TimeOnly.MinValue).AddMinutes(_rightClickMinutes));
    }

    private void OnMenuNewAllDay(object sender, RoutedEventArgs e)
    {
        if (_rightClickColumn is { } c && Vm is { } vm) _ = vm.NewEventAtAsync(c.Date.ToDateTime(TimeOnly.MinValue), allDay: true);
    }

    private void OnMenuNewTask(object sender, RoutedEventArgs e)
    {
        if (_rightClickColumn is { } c && Vm is { } vm) vm.NewTaskOnDayCommand.Execute(c);
    }

    private void OnMenuOpenEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimedBlock b } && Vm is { } vm) vm.OpenEntryCommand.Execute(b.Entry);
    }

    private void OnMenuDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimedBlock b } && Vm is { } vm) vm.DeleteEntryCommand.Execute(b.Entry);
    }
}
