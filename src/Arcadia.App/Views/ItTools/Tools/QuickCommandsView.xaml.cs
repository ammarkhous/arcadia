using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.ItTools.Tools;

/// <summary>Code-behind only picks the number of columns for the available width.</summary>
public partial class QuickCommandsView : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(QuickCommandsView), new PropertyMetadata(2));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public QuickCommandsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Columns = ActualWidth >= 1150 ? 3 : ActualWidth >= 700 ? 2 : 1;
    }
}
