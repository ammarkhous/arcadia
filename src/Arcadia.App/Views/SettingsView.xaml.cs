using System.Windows;
using System.Windows.Controls;
using Arcadia.App.ViewModels;

namespace Arcadia.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // PasswordBox values are not bindable by design; hand them to the view model once and clear the boxes.
    private void OnSaveOnlineServices(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var secret = IgdbSecretBox.Password;
        var key = SgdbKeyBox.Password;
        IgdbSecretBox.Clear();
        SgdbKeyBox.Clear();
        Services.UiSafe.Run(() => vm.SaveOnlineServicesAsync(secret, key), "Saving online service keys");
    }

    private void OnSaveCustomTiles(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var key = MapKeyBox.Password;
        MapKeyBox.Clear();
        vm.SaveCustomTiles(key);
    }
}
