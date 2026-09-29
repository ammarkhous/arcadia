using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Arcadia.App.ViewModels.Travel;

namespace Arcadia.App.Views.Travel;

public partial class TripWorkspaceView : UserControl
{
    private Point? _dragStart;
    private ItineraryItemViewModel? _dragItem;
    private TripWorkspaceViewModel? _vm;

    public TripWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => BannerHeight = e.NewSize.Height < 760 ? 150 : 184;
        DataContextChanged += (_, _) => Attach(DataContext as TripWorkspaceViewModel);
        // frame the trip whenever its map becomes visible (the first layout has no size yet)
        TripMap.IsVisibleChanged += (_, e) => { if (e.NewValue is true) _vm?.RequestFit(); };
        Unloaded += (_, _) => Attach(null);
    }

    public static readonly DependencyProperty BannerHeightProperty = DependencyProperty.Register(nameof(BannerHeight), typeof(double), typeof(TripWorkspaceView), new PropertyMetadata(184.0));
    public double BannerHeight { get => (double)GetValue(BannerHeightProperty); set => SetValue(BannerHeightProperty, value); }

    private void Attach(TripWorkspaceViewModel? vm)
    {
        if (_vm != null) _vm.FitRequested -= OnFitRequested;
        _vm = vm;
        if (_vm != null) _vm.FitRequested += OnFitRequested;
    }

    private void OnFitRequested(IReadOnlyCollection<(double Lat, double Lon)> points) =>
        Dispatcher.BeginInvoke(() => TripMap.Fit(points, 12), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnFitTrip(object sender, RoutedEventArgs e) => _vm?.RequestFit();

    // ── day titles: saved when leaving the box or pressing Enter ──

    private void OnDayTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TripDayViewModel day) _vm?.SaveDayTitleCommand.Execute(day);
    }

    private void OnDayTitleKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ── itinerary drag and drop (reorder within a day, or move to another day) ──

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        // never start a drag from buttons, check boxes or text boxes inside the row
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) { _dragStart = null; return; }
        _dragStart = e.GetPosition(this);
        _dragItem = (sender as FrameworkElement)?.Tag as ItineraryItemViewModel;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ItineraryItemViewModel), item), DragDropEffects.Move);
    }

    private void OnDayDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ItineraryItemViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDayDrop(object sender, DragEventArgs e)
    {
        if (_vm == null || e.Data.GetData(typeof(ItineraryItemViewModel)) is not ItineraryItemViewModel item || (sender as FrameworkElement)?.Tag is not TripDayViewModel day) return;
        e.Handled = true;
        // the drop position decides the index: before the first row whose middle is below the pointer
        var index = day.Items.Count;
        if (sender is FrameworkElement host)
        {
            var y = e.GetPosition(host).Y;
            var rows = FindChildren<Border>(host).Where(b => b.Tag is ItineraryItemViewModel).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var top = rows[i].TranslatePoint(new Point(0, 0), host).Y;
                if (y < top + rows[i].ActualHeight / 2) { index = i; break; }
            }
        }
        if (item.Day == day && day.Items.IndexOf(item) < index) index--; // removing it first shifts the rest up
        if (item.Day == day && day.Items.IndexOf(item) == index) return;
        var vm = _vm;
        Services.UiSafe.Run(() => vm.MoveItemAsync(item, day, index), "Moving an itinerary item");
    }

    // ── documents dropped from Explorer are referenced and attached to the trip ──

    private void OnDocumentsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDocumentsDrop(object sender, DragEventArgs e)
    {
        if (_vm == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;
        var vm = _vm;
        Services.UiSafe.Run(() => vm.AddDroppedFilesAsync(files), "Adding dropped trip documents");
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindChildren<T>(child)) yield return c;
        }
    }
}
