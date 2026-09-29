namespace Arcadia.Core.ItTools;

/// <summary>A fixed shortcut to a built-in Windows tool. Nothing here takes user input.</summary>
/// <param name="Key">Stable id (also the localisation key suffix: QuickCmd_{Key}).</param>
/// <param name="FileName">Program, console or shell URI started through the Windows shell.</param>
/// <param name="RunAsAdministrator">Starts through a UAC prompt (only for the explicit "(Administrator)" entries).</param>
/// <param name="PromptsForAdministrator">Windows itself asks for administrator rights when this tool opens.</param>
public sealed record QuickCommand(string Key, string Icon, string FileName, string? Arguments = null, bool RunAsAdministrator = false, bool PromptsForAdministrator = false);

public static class QuickCommands
{
    public static readonly IReadOnlyList<QuickCommand> All =
    [
        new("DeviceManager", "Cpu", "devmgmt.msc"),
        new("DiskManagement", "Drive", "diskmgmt.msc", PromptsForAdministrator: true),
        new("EventViewer", "EventLog", "eventvwr.msc"),
        new("Services", "Services", "services.msc"),
        new("TaskManager", "Processes", "taskmgr.exe"),
        new("ResourceMonitor", "Monitor", "resmon.exe"),
        new("ComputerManagement", "ITTools", "compmgmt.msc"),
        new("ControlPanel", "Settings", "control.exe"),
        new("NetworkConnections", "Network", "ncpa.cpl"),
        new("WindowsSettings", "WindowsLogo", "ms-settings:"),
        new("WindowsUpdate", "Refresh", "ms-settings:windowsupdate"),
        new("SystemProperties", "Monitor", "SystemPropertiesAdvanced.exe", PromptsForAdministrator: true),
        new("SystemInformation", "Info", "msinfo32.exe"),
        new("AppsFeatures", "Library", "appwiz.cpl"),
        new("Terminal", "Terminal", "wt.exe"),
        new("PowerShell", "Terminal", "powershell.exe"),
        new("PowerShellAdmin", "Shield", "powershell.exe", RunAsAdministrator: true),
        new("CommandPrompt", "Terminal", "cmd.exe"),
    ];

    public static QuickCommand? Find(string? key) => All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
}

public interface IQuickCommandLauncher
{
    /// <summary>Starts a catalogued command. Returns false if Windows could not start it or the user declined UAC.</summary>
    bool Launch(QuickCommand command);
}
