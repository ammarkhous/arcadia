using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Clipboard access that tolerates the clipboard being briefly locked by another program.</summary>
public interface IClipboardService
{
    bool SetText(string text);
}

public sealed class ClipboardService(ILogger<ClipboardService> logger) : IClipboardService
{
    public bool SetText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(40);
            }
            catch (COMException ex)
            {
                logger.LogWarning(ex, "Clipboard is busy");
            }
        }
        return false;
    }
}

/// <summary>Shows files in Explorer without ever creating or changing anything.</summary>
public interface IExplorerLauncher
{
    /// <summary>Opens the folder containing <paramref name="path"/> with the item selected.</summary>
    bool Reveal(string path);
    bool OpenFolder(string folder);
}

public sealed class ExplorerLauncher(ILogger<ExplorerLauncher> logger) : IExplorerLauncher
{
    public bool Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return OpenFolder(Path.GetDirectoryName(path) ?? string.Empty);
        return Start("explorer.exe", $"/select,\"{path}\"");
    }

    public bool OpenFolder(string folder) => Directory.Exists(folder) && Start("explorer.exe", $"\"{folder}\"");

    private bool Start(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "Explorer could not be opened for {Args}", args);
            return false;
        }
    }
}

/// <summary>Saves reports through a Save dialog in the format the user picks (TXT or JSON; PDF can be added as another exporter).</summary>
public sealed class ReportExportService(IDialogService dialogs, IEnumerable<IReportExporter> exporters, INotificationService notifications, ILocalizer loc, ILogger<ReportExportService> logger)
{
    private readonly IReadOnlyList<IReportExporter> _exporters = exporters.ToList();

    public async Task<bool> ExportAsync(ReportDocument document, string suggestedName)
    {
        var filter = string.Join("|", _exporters.Select(e => e.FileFilter));
        var choice = dialogs.PickSaveFile(SafeFileName(suggestedName), filter);
        if (choice is not { } picked) return false;

        var exporter = _exporters[Math.Clamp(picked.FilterIndex - 1, 0, _exporters.Count - 1)];
        var path = picked.Path;
        if (!path.EndsWith(exporter.FileExtension, StringComparison.OrdinalIgnoreCase)) path += exporter.FileExtension;
        try
        {
            await using (var stream = File.Create(path))
                await exporter.WriteAsync(document, stream);
            notifications.Show(loc.Format("It_Exported", Path.GetFileName(path)), NotificationKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Export to {Path} failed", path);
            notifications.Show(loc.Format("It_ExportFailed", ex.Message), NotificationKind.Warning);
            return false;
        }
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Arcadia report" : clean.Length > 80 ? clean[..80] : clean;
    }
}

/// <summary>
/// The diagnostic session tools record into. The active session survives restarts (its id is kept in settings);
/// nothing is recorded unless the user starts or picks a session and presses "Add to session".
/// </summary>
public sealed partial class DiagnosticSessionContext(IDiagnosticSessionStore store, ISettingsService settings, INotificationService notifications, ILocalizer loc, ILogger<DiagnosticSessionContext> logger) : ObservableObject
{
    private bool _loaded;

    [ObservableProperty] private int? _activeId;
    [ObservableProperty] private string? _activeTitle;

    public bool IsActive => ActiveId != null;

    /// <summary>Raised after results or notes were added, or sessions changed.</summary>
    public event EventHandler? Changed;

    partial void OnActiveIdChanged(int? value) => OnPropertyChanged(nameof(IsActive));

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (settings.Current.ItTools.ActiveDiagnosticSessionId is not { } id) return;
        try
        {
            var session = await store.GetAsync(id);
            if (session is { IsClosed: false }) SetActive(session.Id, session.Title, persist: false);
            else await PersistAsync(null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The active diagnostic session could not be restored");
        }
    }

    public async Task<DiagnosticSession> StartAsync(string title, string? reference)
    {
        var session = await store.CreateAsync(title, reference, Environment.MachineName);
        SetActive(session.Id, session.Title);
        logger.LogInformation("Diagnostic session {Id} started", session.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public void SetActive(int id, string title, bool persist = true)
    {
        ActiveId = id;
        ActiveTitle = title;
        if (persist) _ = PersistAsync(id);
    }

    public void ClearActive()
    {
        ActiveId = null;
        ActiveTitle = null;
        _ = PersistAsync(null);
    }

    public async Task<bool> RecordAsync(NewDiagnosticResult result)
    {
        if (ActiveId is not { } id) return false;
        try
        {
            await store.AddResultAsync(id, result);
            notifications.Show(loc.Format("It_Recorded", ActiveTitle ?? string.Empty), NotificationKind.Success);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (KeyNotFoundException)
        {
            ClearActive(); // deleted meanwhile
            notifications.Show(loc["It_SessionGone"], NotificationKind.Warning);
            return false;
        }
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private async Task PersistAsync(int? id)
    {
        settings.Current.ItTools.ActiveDiagnosticSessionId = id;
        try { await settings.SaveAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Saving the active diagnostic session failed"); }
    }
}
