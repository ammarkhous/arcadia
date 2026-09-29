using System.Globalization;
using System.Net.NetworkInformation;
using Arcadia.App.Services;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.ItTools;

public enum ToolCategory { System, Network, Storage, Processes, Windows, Diagnostics, Utilities }

/// <summary>Services every tool uses, bundled so tool constructors stay short.</summary>
public sealed class ToolContext(
    ILocalizer loc,
    INotificationService notifications,
    IClipboardService clipboard,
    IDialogService dialogs,
    IExplorerLauncher explorer,
    ReportExportService exports,
    DiagnosticSessionContext sessions,
    ISettingsService settings,
    TimeProvider time,
    ILoggerFactory logs)
{
    public ILocalizer Loc { get; } = loc;
    public INotificationService Notifications { get; } = notifications;
    public IClipboardService Clipboard { get; } = clipboard;
    public IDialogService Dialogs { get; } = dialogs;
    public IExplorerLauncher Explorer { get; } = explorer;
    public ReportExportService Exports { get; } = exports;
    public DiagnosticSessionContext Sessions { get; } = sessions;
    public ISettingsService Settings { get; } = settings;
    public TimeProvider Time { get; } = time;
    public ILoggerFactory Logs { get; } = logs;
}

/// <summary>A friendly, localized error panel (the technical detail is logged and shown small).</summary>
public sealed record ToolError(string Title, string Message, string? Detail, string Icon);

/// <summary>One line of terminal-style output.</summary>
public sealed record ConsoleLine(string Text, string Tone = "Text");

/// <summary>A labelled value in a property sheet.</summary>
public sealed record InfoRow(string Label, string Value, string? Tone = null)
{
    public bool HasTone => Tone != null;
}

/// <summary>A titled block of <see cref="InfoRow"/>s.</summary>
public sealed record InfoGroup(string Title, string Icon, IReadOnlyList<InfoRow> Rows);

/// <summary>
/// Base for every IT tool: localized title/description, busy + error state, cancellation, and the shared
/// Copy All / Export / Add to session actions built on the tool's <see cref="ReportSection"/>.
/// </summary>
public abstract partial class ToolViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    protected ToolViewModel(ToolContext context)
    {
        Context = context;
        Logger = context.Logs.CreateLogger(GetType());
        context.Loc.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnCultureChanged();
        };
        context.Sessions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DiagnosticSessionContext.IsActive)) return;
            OnPropertyChanged(nameof(CanRecord));
            OnPropertyChanged(nameof(SessionActive));
        };
    }

    protected ToolContext Context { get; }
    protected ILocalizer Loc => Context.Loc;
    protected ILogger Logger { get; }

    public abstract string Key { get; }
    public abstract string Icon { get; }
    public abstract ToolCategory Category { get; }
    public string Title => Loc[$"Tool_{Key}"];
    public string Description => Loc[$"Tool_{Key}_Desc"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRecord), nameof(IsIdle))] private bool _isBusy;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private ToolError? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRecord))] private bool _hasResult;

    public bool IsIdle => !IsBusy;

    /// <summary>False for tools whose output is not a report (quick commands, the session manager).</summary>
    public virtual bool SupportsReport => true;

    /// <summary>"Add to session" is offered when a session is active and there is something to record.</summary>
    public bool CanRecord => Context.Sessions.IsActive && HasResult && !IsBusy;

    public bool SessionActive => Context.Sessions.IsActive;

    /// <summary>Called when the tool becomes visible; live tools start their timers here.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;

    /// <summary>Called when the tool is hidden (another tool, another page).</summary>
    public virtual void OnHidden() { }

    protected virtual void OnCultureChanged() { }

    /// <summary>The tool's current result as a report section (null when there is nothing to report).</summary>
    public abstract ReportSection? BuildReport();

    /// <summary>How the result is summarised in a diagnostic session.</summary>
    protected virtual (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() => (Title, StatusText, DiagnosticOutcome.Info);

    // ── long-running work ──────────────────────────────────────

    /// <summary>Runs work with a fresh cancellation token, busy state and error handling.</summary>
    protected async Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Error = null;
        IsBusy = true;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = Loc["It_Cancelled"];
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    protected void Cancel() => _cts?.Cancel();

    protected void ShowError(Exception ex)
    {
        var (kind, icon) = ex switch
        {
            ToolException t => (t.Failure.ToString(), t.Failure is ToolFailure.AccessDenied or ToolFailure.RequiresElevation ? "Shield" : "Warning"),
            UnauthorizedAccessException => (nameof(ToolFailure.AccessDenied), "Shield"),
            TimeoutException => (nameof(ToolFailure.Timeout), "Hourglass"),
            PingException or System.Net.Sockets.SocketException => (nameof(ToolFailure.HostUnreachable), "Warning"),
            PlatformNotSupportedException or System.ComponentModel.Win32Exception => (nameof(ToolFailure.Unavailable), "Warning"),
            _ => (nameof(ToolFailure.Unexpected), "Warning"),
        };
        if (kind == nameof(ToolFailure.Unexpected)) Logger.LogError(ex, "{Tool} failed unexpectedly", Key);
        else Logger.LogWarning(ex, "{Tool}: {Message}", Key, ex.Message);
        Error = new ToolError(Loc[$"ToolErr_{kind}"], Loc[$"ToolErr_{kind}_Hint"], ex.InnerException?.Message is { } inner ? $"{ex.Message} ({inner})" : ex.Message, icon);
        StatusText = null;
    }

    [RelayCommand]
    private void DismissError() => Error = null;

    // ── copy / export / record ─────────────────────────────────

    protected void Copy(string? text, string? what = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Context.Clipboard.SetText(text)) Context.Notifications.Show(what == null ? Loc["It_Copied"] : Loc.Format("It_CopiedWhat", what), NotificationKind.Success);
        else Context.Notifications.Show(Loc["It_ClipboardBusy"], NotificationKind.Warning);
    }

    [RelayCommand]
    private void CopyAll()
    {
        if (BuildReport() is { } section) Copy(TextReportExporter.Render(section));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (BuildReport() is not { } section) return;
        var now = Context.Time.GetUtcNow().UtcDateTime;
        var doc = new ReportDocument
        {
            Title = $"Arcadia · {Title}",
            Subtitle = Environment.MachineName,
            GeneratedAtUtc = now,
            GeneratedBy = "Arcadia",
            Sections = [section],
        };
        await Context.Exports.ExportAsync(doc, $"{Title} {now.ToLocalTime():yyyy-MM-dd HHmm}");
    }

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (BuildReport() is not { } section) return;
        var (title, summary, outcome) = DescribeResult();
        await Context.Sessions.RecordAsync(new NewDiagnosticResult(Key, title, summary, outcome, section));
    }

    // ── helpers ────────────────────────────────────────────────

    protected CultureInfo Culture => Loc.Culture;

    protected string Ms(long? ms) => ms is { } v ? Loc.Format("It_Ms", v) : "—";

    protected string LocalTime(DateTime? utc) =>
        utc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("g", Culture) : "—";
}
