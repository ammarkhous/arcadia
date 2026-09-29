using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Travel;

namespace Arcadia.App.Views.Travel;

public partial class TravelCountriesView : UserControl
{
    public TravelCountriesView() => InitializeComponent();

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is TravelViewModel vm && (sender as DataGrid)?.SelectedItem is CountryRow row) vm.ShowCountry(row.Code);
    }

    private void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TravelViewModel vm && (sender as DataGrid)?.SelectedItem is CountryRow row) vm.OpenCountryCommand.Execute(row);
    }

    private void OnClosePanel(object sender, RoutedEventArgs e)
    {
        if (DataContext is TravelViewModel vm) vm.Country = null;
    }
}
