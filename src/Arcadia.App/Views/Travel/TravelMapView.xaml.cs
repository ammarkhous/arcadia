using System.Windows;
using System.Windows.Controls;
using Arcadia.App.ViewModels.Travel;

namespace Arcadia.App.Views.Travel;

public partial class TravelMapView : UserControl
{
    private TravelViewModel? _vm;

    public TravelMapView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TravelViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as TravelViewModel);
        IsVisibleChanged += OnVisibleChanged;
    }

    private void Attach(TravelViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.FlyRequested -= OnFly;
            _vm.FitRequested -= OnFit;
            _vm.Tiles.Changed -= OnTiles;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.FlyRequested += OnFly;
            _vm.FitRequested += OnFit;
            _vm.Tiles.Changed += OnTiles;
            _vm.ReplayPendingMapRequest();
        }
    }

    // A request often comes together with switching to this section: it runs once the map is visible and laid out.
    private Action? _pending;

    private void Run(Action action)
    {
        if (IsVisible && Map.ActualWidth > 10) Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Loaded);
        else _pending = action;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || _pending is not { } action) return;
        _pending = null;
        Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnFly(double lat, double lon, double? zoom) => Run(() => Map.FlyTo(lat, lon, zoom));
    private void OnFit(IReadOnlyCollection<(double Lat, double Lon)> points) => Run(() => Map.Fit(points));
    private void OnTiles(object? sender, EventArgs e) => _vm?.UpdateTileNotice(_vm.Tiles.LastStatus, Map.ShowsTiles);
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);
}
