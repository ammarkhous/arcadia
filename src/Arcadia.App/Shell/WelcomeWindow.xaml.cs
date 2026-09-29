using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Arcadia.App.Shell;

/// <summary>The first-run window. Closing it without finishing exits Arcadia without creating anything.</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickFolder = initial =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(initial) ? initial : null };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        viewModel.Finished += (_, _) => { DialogResult = true; };
        Loaded += (_, _) => NextButton.Focus();
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>True → Collapsed, false → Visible.</summary>
public sealed class NotVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
