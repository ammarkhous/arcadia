using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Completes when the database has been migrated (and seeded in development).</summary>
public sealed class StartupState
{
    private readonly TaskCompletionSource _databaseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task DatabaseReady => _databaseReady.Task;

    public void MarkDatabaseReady() => _databaseReady.TrySetResult();

    public void MarkDatabaseFailed(Exception ex) => _databaseReady.TrySetException(ex);
}

public interface INavigationService
{
    string CurrentKey { get; }
    void Navigate(string key);
    /// <summary>Opens the Games module on a specific game's detail page.</summary>
    void ShowGame(int gameId);
    /// <summary>Opens the Projects module on a project's workspace.</summary>
    void ShowProject(int projectId);
    /// <summary>Opens the Documents module on a document.</summary>
    void ShowDocument(int documentId);
    /// <summary>Opens any Arcadia record (an arcadia:// link, a task or note context, a reminder, a calendar item).</summary>
    void Open(Arcadia.Core.Organizer.EntityRef target);
}

public enum NotificationKind { Info, Success, Warning }

public interface INotificationService
{
    void Show(string message, NotificationKind kind = NotificationKind.Info);
}

/// <summary>Opens URLs and folders through the Windows shell.</summary>
public interface IShellLauncher
{
    void OpenUri(Uri uri);
    void OpenFolder(string path);
    /// <summary>Starts a validated game launch target. Returns false if it was refused or failed.</summary>
    bool Launch(string target);
}

public sealed class ShellLauncher(ILogger<ShellLauncher> logger) : IShellLauncher
{
    public void OpenUri(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http"))
        {
            logger.LogWarning("Refused to open non-web URI {Uri}", uri);
            return;
        }
        Start(uri.AbsoluteUri);
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        Start(path);
    }

    public bool Launch(string target)
    {
        target = target.Trim().Trim('"');
        if (!Arcadia.Core.Games.LaunchTargets.IsAllowed(target))
        {
            logger.LogWarning("Refused launch target {Target}", target);
            return false;
        }

        var info = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!target.Contains("://", StringComparison.Ordinal))
        {
            if (!File.Exists(target))
            {
                logger.LogWarning("Launch target does not exist: {Target}", target);
                return false;
            }
            info.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
        }
        logger.LogInformation("Launching {Target}", target);
        return Start(info);
    }

    private bool Start(string target) => Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Target}", info.FileName);
            return false;
        }
    }
}
