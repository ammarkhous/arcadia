using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.Travel;

public partial class TravelView : UserControl
{
    public TravelView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => RailWidth = e.NewSize.Width < 1250 ? new GridLength(176) : new GridLength(204);
    }

    public static readonly DependencyProperty RailWidthProperty = DependencyProperty.Register(nameof(RailWidth), typeof(GridLength), typeof(TravelView),
        new PropertyMetadata(new GridLength(204)));
    public GridLength RailWidth { get => (GridLength)GetValue(RailWidthProperty); set => SetValue(RailWidthProperty, value); }
}
