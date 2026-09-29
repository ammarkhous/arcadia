using System.Reflection;
using System.Runtime.InteropServices;

namespace Arcadia.App.Diagnostics;

/// <summary>One place for the version shown in About, written to logs and backup manifests, and stamped by the installer.</summary>
public static class AppInfo
{
    /// <summary>"1.0.0", or "1.0.0-rc.1" for a release candidate.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>True when the .NET runtime ships inside Arcadia's folder (the installer build).</summary>
    public static bool IsSelfContained { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    public static string Runtime => RuntimeInformation.FrameworkDescription;

#if DEBUG
    public const string Configuration = "Debug";
#else
    public const string Configuration = "Release";
#endif
}
