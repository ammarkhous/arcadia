using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Arcadia.App.Views;

/// <summary>
/// Home dashboard. Code-behind only arranges the panels for the available width (desktop "responsiveness":
/// wide 3-column composition from the reference design, or a 2-column arrangement with vertical scrolling).
/// </summary>
public partial class HomeView : UserControl
{
    private const double CompactBelowWidth = 1040;
    private const double WideMinHeight = 830;
    private const double CompactMinHeight = 1320;
    private const double FeaturedDescriptionMinHeight = 412;
    private const double FeaturedLinksMinHeight = 394;

    private bool? _compact;

    public HomeView()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, _) => UpdateLayoutMode();
        // On short screens the description yields its space to the facts (it remains available as a tooltip).
        FeaturedPanel.SizeChanged += (_, _) =>
        {
            var h = FeaturedPanel.ActualHeight;
            FeaturedDescription.Visibility = h < FeaturedDescriptionMinHeight ? Visibility.Collapsed : Visibility.Visible;
            FeaturedLinks.Visibility = h < FeaturedLinksMinHeight ? Visibility.Collapsed : Visibility.Visible;
        };
        Loaded += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = Scroller.ActualWidth < CompactBelowWidth;
        if (compact != _compact)
        {
            _compact = compact;
            if (compact) ApplyCompact(); else ApplyWide();
        }
        // Fill the viewport when there is room, otherwise keep a usable minimum and scroll.
        Layout.Height = Math.Max(Scroller.ActualHeight + 10, compact ? CompactMinHeight : WideMinHeight);
    }

    private void ApplyWide()
    {
        // the Today strip is an Auto row: hidden in Settings, the original composition is untouched
        SetGrid(
            columns: [new(1.12, GridUnitType.Star), new(1.38, GridUnitType.Star), new(1.0, GridUnitType.Star)],
            rows: [new(1.36, GridUnitType.Star), GridLength.Auto, new(1.0, GridUnitType.Star), new(0.72, GridUnitType.Star)]);
        Place(FeaturedPanel, 0, 0, 1, 2);
        Place(OverviewPanel, 0, 2);
        Place(TodayStrip, 1, 0, 1, 3);
        Place(RecentPanel, 2, 0);
        Place(ScreenshotsPanel, 2, 1);
        Place(BacklogPanel, 2, 2);
        Place(ModulesStrip, 3, 0, 1, 3);
    }

    private void ApplyCompact()
    {
        SetGrid(
            columns: [new(1, GridUnitType.Star), new(1, GridUnitType.Star)],
            rows: [new(1.25, GridUnitType.Star), GridLength.Auto, new(1.05, GridUnitType.Star), new(1.0, GridUnitType.Star), new(0.75, GridUnitType.Star)]);
        Place(FeaturedPanel, 0, 0, 1, 2);
        Place(TodayStrip, 1, 0, 1, 2);
        Place(RecentPanel, 2, 0);
        Place(OverviewPanel, 2, 1);
        Place(ScreenshotsPanel, 3, 0);
        Place(BacklogPanel, 3, 1);
        Place(ModulesStrip, 4, 0, 1, 2);
    }

    private void SetGrid(GridLength[] columns, GridLength[] rows)
    {
        Layout.ColumnDefinitions.Clear();
        foreach (var c in columns) Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
        Layout.RowDefinitions.Clear();
        foreach (var r in rows) Layout.RowDefinitions.Add(new RowDefinition { Height = r });
    }

    private static void Place(UIElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private static string[] ImageFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(Services.GameActions.IsImportableImage).ToArray() : [];

    private void OnShotsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ImageFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnShotsDrop(object sender, DragEventArgs e)
    {
        var files = ImageFiles(e);
        if (files.Length > 0 && DataContext is ViewModels.Home.HomeViewModel vm) vm.ImportFilesCommand.Execute(files);
        e.Handled = true;
    }

    private void OnMoreActions(object sender, RoutedEventArgs e)
    {
        if (Resources["FeaturedMenu"] is not ContextMenu menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
