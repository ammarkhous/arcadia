namespace Arcadia.Core.Tracking;

/// <summary>A running process as seen by the tracker (image path is the full executable path).</summary>
public sealed record RunningProcess(int Id, string ImagePath, DateTime StartedAtUtc);

/// <summary>Lists running processes. Implemented with Win32 in the app; faked in tests.</summary>
public interface IProcessScanner
{
    IReadOnlyList<RunningProcess> Snapshot();

    /// <summary>Processes with a visible top-level window (used only to learn an unknown game folder).</summary>
    IReadOnlyList<RunningProcess> WindowedSnapshot();
}

/// <summary>What the tracker needs to know about a library game.</summary>
public sealed record TrackableGame(int Id, string Title, string? InstallDirectory, IReadOnlyList<string>? Executables = null)
{
    /// <summary>"Cyberpunk2077.exe; D:\Games\x.exe" → entries (names or full paths).</summary>
    public static IReadOnlyList<string> ParseExecutables(string? text) =>
        (text ?? "").Split([';', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Trim('"')).Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
