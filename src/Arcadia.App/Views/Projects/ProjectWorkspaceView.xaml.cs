using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Projects;

namespace Arcadia.App.Views.Projects;

/// <summary>
/// Project workspace. Code-behind handles file drag and drop (Explorer → project: references only),
/// double-click shortcuts and the banner height for small windows.
/// </summary>
public partial class ProjectWorkspaceView : UserControl
{
    public static readonly DependencyProperty BannerHeightProperty = DependencyProperty.Register(nameof(BannerHeight), typeof(double), typeof(ProjectWorkspaceView), new PropertyMetadata(170.0));

    public double BannerHeight { get => (double)GetValue(BannerHeightProperty); set => SetValue(BannerHeightProperty, value); }

    public ProjectWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => BannerHeight = ActualHeight < 640 ? 140 : 170;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetDragOver(false);
        Drop += OnDrop;
    }

    private ProjectWorkspaceViewModel? Vm => DataContext as ProjectWorkspaceViewModel;

    private void SetDragOver(bool value)
    {
        if (Vm != null) Vm.IsDragOver = value;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(files);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDragOver(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Vm != null)
        {
            e.Handled = true;
            var vm = Vm;
            Services.UiSafe.Run(() => vm.AddPathsAsync(paths), "Adding dropped project files");
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedFile is { } file && e.OriginalSource is FrameworkElement { DataContext: FileRowViewModel }) Vm.OpenFileCommand.Execute(file);
    }

    private void OnLinkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: LinkRowViewModel link }) Vm?.OpenLinkCommand.Execute(link);
    }
}
