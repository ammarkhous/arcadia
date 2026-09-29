using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Arcadia.App.Dialogs;

/// <summary>
/// Friendly error surface. Technical details are only offered in debug builds; the log always has them.
/// </summary>
public partial class ErrorWindow : Window
{
    private readonly string _logsDirectory;

    public ErrorWindow(Exception exception, string logsDirectory, bool fatal)
    {
        InitializeComponent();
        _logsDirectory = logsDirectory;
#if DEBUG
        DetailsText.Text = exception.ToString();
#else
        DetailsExpander.Visibility = Visibility.Collapsed;
#endif
        if (fatal) ContinueButton.Content = Localization.LocalizationManager.Instance["Window_Close"];
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            Process.Start(new ProcessStartInfo(_logsDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Trace.TraceWarning($"Arcadia: could not open logs folder: {ex.Message}");
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e) => Close();
}
