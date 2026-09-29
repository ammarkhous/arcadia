using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.ItTools.Tools;

/// <summary>Code-behind only picks the number of columns for the available width.</summary>
public partial class NetworkAdaptersView : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(NetworkAdaptersView), new PropertyMetadata(2));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public NetworkAdaptersView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Columns = ActualWidth >= 860 ? 2 : 1;
    }
}
