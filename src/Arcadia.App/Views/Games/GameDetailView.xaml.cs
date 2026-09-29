using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Arcadia.App.Views.Games;

public partial class GameDetailView : UserControl
{
    /// <summary>SteamGridDB hero banners are 1920×620; the frame keeps that shape so a chosen banner is never zoomed or cropped.</summary>
    private const double BannerAspect = 1920.0 / 620.0;

    public GameDetailView()
    {
        InitializeComponent();
        HeroBorder.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            HeroFrame.Height = Math.Max(220, Math.Round((HeroBorder.ActualWidth - 2) / BannerAspect));
        };
    }

    // Drag-and-drop is view plumbing; the import itself is the view model's ImportFiles command.
    private static string[] ImageFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(Services.GameActions.IsImportableImage).ToArray() : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = ImageFiles(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var files = ImageFiles(e);
        if (files.Length > 0 && DataContext is ViewModels.Games.GameDetailViewModel vm) vm.ImportFilesCommand.Execute(files);
        e.Handled = true;
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (Resources["MoreMenu"] is not ContextMenu menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
