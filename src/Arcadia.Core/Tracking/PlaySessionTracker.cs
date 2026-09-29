namespace Arcadia.Core.Tracking;

/// <summary>The session currently being recorded. Persisted so a session survives Arcadia restarting.</summary>
public sealed record ActiveSession(int GameId, string Title, DateTime StartedAtUtc, string Directory, DateTime LastSeenUtc, IReadOnlyList<string>? Executables = null);

public sealed record TickResult(ActiveSession? Started = null, ActiveSession? Ended = null, (int GameId, string Directory)? Learned = null)
{
    public static readonly TickResult None = new();
}

/// <summary>
/// Decides when a game is being played by watching which processes run from each game's install folder.
/// Pure logic: the caller supplies process snapshots (through <see cref="IProcessScanner"/>) and a clock,
/// calls <see cref="Tick"/> periodically and records the sessions it reports.
/// </summary>
public sealed class PlaySessionTracker(IProcessScanner scanner, TimeProvider time)
{
    /// <summary>Consecutive empty scans before a session counts as ended (tolerates brief restarts).</summary>
    public int MissedScansToEnd { get; set; } = 2;

    /// <summary>How long after a launch Arcadia waits for the game to appear.</summary>
    public TimeSpan LaunchWindow { get; set; } = TimeSpan.FromMinutes(5);

    public bool DetectExternalLaunches { get; set; } = true;

    public ActiveSession? Active { get; private set; }

    /// <summary>A session ends only after its game has been gone this long (a quick relaunch continues the same session).</summary>
    public TimeSpan EndGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A restored session whose last heartbeat is older than this ends at that heartbeat (the gap is unknown time).</summary>
    public TimeSpan RestoreGap { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Sessions never start before this (set on resume from sleep so sleep is not counted).</summary>
    private DateTime _notBefore;

    public bool IsWatching => Active != null || _pending != null || DetectExternalLaunches;

    /// <summary>A launch is expected; callers should poll quickly until the game appears.</summary>
    public bool HasPendingLaunch => _pending != null;

    private IReadOnlyList<TrackableGame> _games = [];
    private PendingLaunch? _pending;
    private int _missed;

    private sealed record PendingLaunch(TrackableGame Game, DateTime LaunchedAtUtc, HashSet<int> BaselinePids);

    // Launchers, shells and helpers that must never be mistaken for a game.
    private static readonly HashSet<string> ExcludedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe", "epicgameslauncher.exe", "epicwebhelper.exe",
        "galaxyclient.exe", "galaxyclient helper.exe", "upc.exe", "uplaywebcore.exe", "eadesktop.exe", "eabackgroundservice.exe",
        "battle.net.exe", "agent.exe", "xboxpcapp.exe", "gamingservices.exe", "explorer.exe", "arcadia.exe", "heroic.exe",
        "unitycrashhandler64.exe", "unitycrashhandler32.exe", "crashreportclient.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dxsetup.exe", "easyanticheat_setup.exe", "easyanticheat.exe", "beservice.exe", "msedge.exe", "chrome.exe", "firefox.exe",
        "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "werfault.exe",
    };

    private static readonly string[] ExcludedPathFragments =
        [@"\_commonredist\", @"\redist\", @"\__installer\", @"\directx\", @"\easyanticheat\", @"\battleye\"];

    public void UpdateLibrary(IEnumerable<TrackableGame> games) => _games = games.ToList();

    /// <summary>Arcadia just launched this game. A null directory means "learn it from the next new windowed process".</summary>
    public void ExpectLaunch(TrackableGame game)
    {
        var baseline = string.IsNullOrEmpty(game.InstallDirectory)
            ? scanner.WindowedSnapshot().Select(p => p.Id).ToHashSet()
            : [];
        _pending = new PendingLaunch(game, time.GetUtcNow().UtcDateTime, baseline);
    }

    /// <summary>
    /// Resumes a session that was active when Arcadia last closed. If its last heartbeat is too old (Arcadia crashed or the PC
    /// was off/asleep), that session is returned as ended at its last heartbeat and a still-running game starts a new one now:
    /// time Arcadia could not observe is never counted twice or guessed.
    /// </summary>
    public ActiveSession? Restore(ActiveSession session)
    {
        _missed = 0;
        var now = time.GetUtcNow().UtcDateTime;
        if (now - session.LastSeenUtc > RestoreGap)
        {
            Active = null;
            _notBefore = now;
            return session;
        }
        Active = session;
        return null;
    }

    /// <summary>The PC is going to sleep: the active session ends now.</summary>
    public ActiveSession? Suspend()
    {
        var ended = Active is { } a ? a with { LastSeenUtc = time.GetUtcNow().UtcDateTime } : null;
        Active = null;
        _missed = 0;
        return ended;
    }

    /// <summary>Back from sleep: a game that is still running starts a new session from now.</summary>
    public void Resumed() => _notBefore = time.GetUtcNow().UtcDateTime;

    /// <summary>Ends the active session now (user pressed stop). Returns it, or null.</summary>
    public ActiveSession? EndNow()
    {
        var ended = Active is { } a ? a with { LastSeenUtc = time.GetUtcNow().UtcDateTime } : null;
        Active = null;
        _pending = null;
        return ended;
    }

    public TickResult Tick()
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_pending != null && now - _pending.LaunchedAtUtc > LaunchWindow) _pending = null;
        if (!IsWatching) return TickResult.None;

        var processes = scanner.Snapshot();

        if (Active is { } active)
        {
            var running = processes.Where(p => Matches(p, active.Directory, active.Executables)).ToList();
            if (running.Count > 0)
            {
                _missed = 0;
                Active = active with { LastSeenUtc = now };
                return TickResult.None;
            }
            if (++_missed < MissedScansToEnd || now - active.LastSeenUtc < EndGrace) return TickResult.None;
            Active = null;
            _missed = 0;
            return new TickResult(Ended: active);
        }

        // A launch Arcadia started takes priority over external detection.
        if (_pending is { } pending)
        {
            if (!string.IsNullOrEmpty(pending.Game.InstallDirectory) || pending.Game.Executables is { Count: > 0 })
            {
                var started = TryStart(pending.Game, pending.Game.InstallDirectory ?? "", processes, now);
                if (started != null) { _pending = null; return new TickResult(Started: started); }
            }
            else
            {
                var learned = LearnDirectory(pending);
                if (learned != null)
                {
                    _pending = null;
                    var started = TryStart(pending.Game, learned, processes, now);
                    return new TickResult(Started: started, Learned: (pending.Game.Id, learned));
                }
            }
        }

        if (DetectExternalLaunches)
        {
            foreach (var game in _games)
            {
                if (string.IsNullOrEmpty(game.InstallDirectory) && game.Executables is not { Count: > 0 }) continue;
                var started = TryStart(game, game.InstallDirectory ?? "", processes, now);
                if (started != null) return new TickResult(Started: started);
            }
        }
        return TickResult.None;
    }

    private ActiveSession? TryStart(TrackableGame game, string directory, IReadOnlyList<RunningProcess> processes, DateTime now)
    {
        var matches = processes.Where(p => Matches(p, directory, game.Executables)).ToList();
        if (matches.Count == 0) return null;
        // The earliest process start is when play began (also correct when detected late, e.g. after Arcadia restarts),
        // but never before a resume from sleep or an unobserved gap.
        var start = matches.Min(p => p.StartedAtUtc);
        if (start > now || start == default) start = now;
        if (start < _notBefore) start = _notBefore;
        Active = new ActiveSession(game.Id, game.Title, start, directory, now, game.Executables);
        _missed = 0;
        return Active;
    }

    private string? LearnDirectory(PendingLaunch pending)
    {
        var candidate = scanner.WindowedSnapshot()
            .Where(p => !pending.BaselinePids.Contains(p.Id))
            .Where(p => p.StartedAtUtc >= pending.LaunchedAtUtc.AddSeconds(-5))
            .Where(p => !IsExcluded(p.ImagePath))
            .OrderBy(p => p.StartedAtUtc)
            .FirstOrDefault();
        if (candidate == null) return null;
        var dir = Path.GetDirectoryName(candidate.ImagePath);
        return IsReasonableGameDirectory(dir) ? dir : null;
    }

    /// <summary>An associated executable name (or full path) matches exactly; otherwise any process inside the game's folder.</summary>
    public static bool Matches(RunningProcess process, string directory, IReadOnlyList<string>? executables)
    {
        if (executables is { Count: > 0 })
        {
            if (string.IsNullOrEmpty(process.ImagePath)) return false;
            var name = Path.GetFileName(process.ImagePath);
            foreach (var exe in executables)
                if (exe.Contains(Path.DirectorySeparatorChar) ? string.Equals(exe, process.ImagePath, StringComparison.OrdinalIgnoreCase) : string.Equals(exe, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        return !string.IsNullOrEmpty(directory) && IsGameProcess(process, directory);
    }

    public static bool IsGameProcess(RunningProcess process, string directory)
    {
        if (string.IsNullOrEmpty(process.ImagePath) || IsExcluded(process.ImagePath)) return false;
        var dir = directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return process.ImagePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcluded(string imagePath)
    {
        if (ExcludedExecutables.Contains(Path.GetFileName(imagePath))) return true;
        var lower = imagePath.ToLowerInvariant();
        if (lower.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant() + "\\", StringComparison.Ordinal)) return true;
        return ExcludedPathFragments.Any(lower.Contains);
    }

    /// <summary>Rejects folders so broad that every program would match (drive roots, Program Files, Windows).</summary>
    public static bool IsReasonableGameDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var full = dir.TrimEnd('\\', '/');
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full) return false;
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];
        return !broad.Any(b => !string.IsNullOrEmpty(b) && string.Equals(b.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
    }
}
