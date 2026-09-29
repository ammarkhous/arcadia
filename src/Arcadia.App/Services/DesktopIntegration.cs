using System.Windows;
using Arcadia.App.ViewModels;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// Ties the main window to Windows (Phase 9): tray icon and menu, minimize/start to tray, the optional global
/// "Show Arcadia" hotkey, the foreground test for notifications, and pausing the header clock while nobody can see it.
/// </summary>
public sealed class DesktopIntegration(TrayService tray, ArcadiaNotifier notifier, PerformanceService performance, AutomationService automation,
    ISettingsService settings, ILocalizer loc, ILogger<DesktopIntegration> logger)
{
    private Window? _window;
    private ShellViewModel? _shell;
    private string? _hotkeyApplied;

    public bool HotkeyConflict { get; private set; }

    public void Attach(Window window, ShellViewModel shell)
    {
        _window = window;
        _shell = shell;
        notifier.IsForeground = () => window.IsVisible && window.IsActive && window.WindowState != WindowState.Minimized;
        notifier.Open = target => { ShowWindow(); if (target is { } t) _ = shell.OpenAsync(t, t.Kind == Arcadia.Core.Organizer.EntityKind.Game ? "Journal" : null); };
        tray.Activated += (_, _) => ShowWindow();
        tray.HotkeyPressed += (_, _) => { logger.LogInformation("Global shortcut pressed"); ShowWindow(); };
        tray.MenuItems = BuildMenu;
        shell.ShowWindowRequested += (_, _) => ShowWindow();
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized && settings.Current.Windows.MinimizeToTray && tray.IsVisible) window.Hide();
            Shown();
        };
        window.IsVisibleChanged += (_, _) => Shown();
        void Shown()
        {
            var shown = window.IsVisible && window.WindowState != WindowState.Minimized;
            shell.Header.SetVisible(shown);
            shell.IsWindowShown = shown;
        }
        performance.Changed += (_, _) => UpdateStatus();
        automation.Changed += (_, _) => UpdateStatus();
        settings.SettingsChanged += (_, _) => Apply();
        Apply();
    }

    public void Apply()
    {
        if (settings.Current.Windows.ShowTrayIcon && !App.IsCaptureMode) tray.Show(); else tray.Hide();
        UpdateStatus();
        var gesture = settings.Current.Shortcuts.GlobalShowArcadia;
        if (gesture != _hotkeyApplied && !App.IsCaptureMode)
        {
            _hotkeyApplied = gesture;
            HotkeyConflict = !tray.RegisterHotkey(ShortcutManager.Parse(gesture));
            if (!string.IsNullOrEmpty(gesture)) logger.LogInformation("Global shortcut {Gesture}: {State}", gesture, HotkeyConflict ? "in use by another program" : "registered");
            if (HotkeyConflict) notifier.Notify("warning", loc["Win_HotkeyConflictTitle"], loc.Format("Win_HotkeyConflict", gesture), kind: NotificationKind.Warning);
        }
    }

    private void UpdateStatus()
    {
        var status = automation.IsPaused ? TrayStatus.Paused : performance.IsGamingMode ? TrayStatus.Gaming : TrayStatus.Normal;
        var tip = "Arcadia" + (performance.IsGamingMode ? " · " + loc["Perf_GamingMode"] : "") + (automation.IsPaused ? " · " + loc["Auto_Paused"] : "")
                  + (performance.IsPowerSaver ? " · " + loc["Perf_PowerSaver"] : "");
        tray.SetStatus(status, tip);
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Starts hidden in the tray (or minimized when there is no tray icon): used by "start with Windows".</summary>
    public void StartHidden(Window window)
    {
        if (tray.IsVisible) logger.LogInformation("Started in the notification area");
        else
        {
            window.WindowState = WindowState.Minimized;
            window.Show();
        }
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var shell = _shell!;
        return
        [
            new(loc["Tray_Open"], "Home", ShowWindow),
            new(loc["Tray_Today"], "Agenda", () => { ShowWindow(); _ = shell.NavigateToAsync(Sections.Calendar, "Today"); }),
            new(loc["Tray_Capture"], "Bolt", () => { ShowWindow(); shell.OpenQuickCaptureCommand.Execute(null); }),
            new(loc["Tray_Oracle"], "Oracle", () => { ShowWindow(); _ = shell.Oracle.OpenOverlayAsync(); }),
            new("", "", () => { }, Separator: true),
            new(loc["Perf_GamingMode"], "Games", () => performance.SetGamingMode(!performance.IsGamingMode, "manual"), performance.IsGamingMode),
            new(loc["Perf_PowerSaver"], "Bolt", performance.TogglePowerSaver, performance.IsPowerSaver),
            new(loc["Auto_PauseAll"], "Stop", () => _ = automation.SetPausedAsync(!automation.IsPaused), automation.IsPaused),
            new("", "", () => { }, Separator: true),
            new(loc["Tray_Exit"], "WindowClose", () => Application.Current.Shutdown()),
        ];
    }
}
