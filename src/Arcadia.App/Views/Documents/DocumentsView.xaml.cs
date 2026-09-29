using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Documents;

namespace Arcadia.App.Views.Documents;

/// <summary>Document library. Code-behind: drag and drop, multi-selection, double-click and responsive columns.</summary>
public partial class DocumentsView : UserControl
{
    public static readonly DependencyProperty RailWidthProperty = DependencyProperty.Register(nameof(RailWidth), typeof(GridLength), typeof(DocumentsView), new PropertyMetadata(new GridLength(220)));
    public static readonly DependencyProperty DetailsWidthProperty = DependencyProperty.Register(nameof(DetailsWidth), typeof(GridLength), typeof(DocumentsView), new PropertyMetadata(new GridLength(360)));

    public GridLength RailWidth { get => (GridLength)GetValue(RailWidthProperty); set => SetValue(RailWidthProperty, value); }
    public GridLength DetailsWidth { get => (GridLength)GetValue(DetailsWidthProperty); set => SetValue(DetailsWidthProperty, value); }

    public DocumentsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            RailWidth = new GridLength(ActualWidth < 1250 ? 186 : 220);
            DetailsWidth = new GridLength(ActualWidth < 1250 ? 300 : 360);
        };
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { if (Vm != null) Vm.IsDragOver = false; };
        Drop += OnDrop;
    }

    private DocumentsViewModel? Vm => DataContext as DocumentsViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
        if (Vm != null) Vm.IsDragOver = files;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsDragOver = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            var vm = Vm;
            Services.UiSafe.Run(() => vm.AddPathsAsync(paths), "Adding dropped documents");
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(Grid.SelectedItems.OfType<DocumentRowViewModel>().Select(r => r.Id).ToList());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: DocumentRowViewModel row }) Vm?.OpenCommand.Execute(row);
    }

    private void OnDetailsLostFocus(object sender, RoutedEventArgs e) => Vm?.SaveDetailsCommand.Execute(null);
}
