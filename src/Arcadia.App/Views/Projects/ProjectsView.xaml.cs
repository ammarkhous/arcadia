using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Views.Projects;

/// <summary>Projects landing page. Code-behind only adapts the side column to the available width.</summary>
public partial class ProjectsView : UserControl
{
    public static readonly DependencyProperty SideWidthProperty = DependencyProperty.Register(nameof(SideWidth), typeof(GridLength), typeof(ProjectsView), new PropertyMetadata(new GridLength(330)));

    public GridLength SideWidth { get => (GridLength)GetValue(SideWidthProperty); set => SetValue(SideWidthProperty, value); }

    public ProjectsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => SideWidth = new GridLength(ActualWidth < 1250 ? 270 : 330);
    }
}
