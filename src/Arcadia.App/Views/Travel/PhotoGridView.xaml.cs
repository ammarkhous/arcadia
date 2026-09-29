using System.Windows;
using System.Windows.Controls;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Travel;

namespace Arcadia.App.Views.Travel;

public partial class PhotoGridView : UserControl
{
    public PhotoGridView() => InitializeComponent();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is TravelPhotoGridViewModel vm) vm.SetWidth(e.NewSize.Width - 18); // leave room for the scroll bar
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Photos dragged from Explorer are referenced (never copied or moved).</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not TravelPhotoGridViewModel vm || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var images = files.Where(TravelPhotoService.IsImage).ToList();
        if (images.Count > 0) Services.UiSafe.Run(() => vm.AddPathsAsync(images), "Adding dropped photos");
    }
}
