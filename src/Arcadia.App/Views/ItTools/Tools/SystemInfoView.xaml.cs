using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.ItTools.Tools;

/// <summary>Code-behind only picks the number of columns for the available width.</summary>
public partial class SystemInfoView : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(SystemInfoView), new PropertyMetadata(2));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public SystemInfoView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Columns = ActualWidth >= 860 ? 2 : 1;
    }
}
