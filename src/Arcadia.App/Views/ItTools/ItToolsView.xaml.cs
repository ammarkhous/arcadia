using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.ItTools;

/// <summary>IT Tools page. Code-behind only adapts density to the available width (1366×768 up to 4K).</summary>
public partial class ItToolsView : UserControl
{
    public static readonly DependencyProperty StatusRowsProperty = DependencyProperty.Register(nameof(StatusRows), typeof(int), typeof(ItToolsView), new PropertyMetadata(1));
    public static readonly DependencyProperty RailWidthProperty = DependencyProperty.Register(nameof(RailWidth), typeof(GridLength), typeof(ItToolsView), new PropertyMetadata(new GridLength(214)));
    public static readonly DependencyProperty OverviewColumnCountProperty = DependencyProperty.Register(nameof(OverviewColumnCount), typeof(int), typeof(ItToolsView), new PropertyMetadata(3));

    public int StatusRows { get => (int)GetValue(StatusRowsProperty); set => SetValue(StatusRowsProperty, value); }
    public GridLength RailWidth { get => (GridLength)GetValue(RailWidthProperty); set => SetValue(RailWidthProperty, value); }
    public int OverviewColumnCount { get => (int)GetValue(OverviewColumnCountProperty); set => SetValue(OverviewColumnCountProperty, value); }

    public ItToolsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var w = ActualWidth;
            StatusRows = w < 1150 ? 2 : 1;
            RailWidth = new GridLength(w < 1100 ? 184 : 214);
            OverviewColumnCount = w - RailWidth.Value < 820 ? 2 : 3;
        };
    }
}
