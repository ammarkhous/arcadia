using Arcadia.Core.Tracking;
using Arcadia.Infrastructure.Games;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class PlaySessionTrackerTests
{
    private sealed class FakeTime(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class FakeScanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = [];
        public List<RunningProcess> Windowed { get; } = [];
        public IReadOnlyList<RunningProcess> Snapshot() => [.. Processes];
        public IReadOnlyList<RunningProcess> WindowedSnapshot() => [.. Windowed];
    }

    private const string WitcherDir = @"D:\SteamLibrary\steamapps\common\The Witcher 3";
    private static readonly DateTime T0 = new(2026, 9, 27, 18, 0, 0, DateTimeKind.Utc);

    private readonly FakeTime _time = new(T0);
    private readonly FakeScanner _scanner = new();
    private readonly PlaySessionTracker _tracker;

    public PlaySessionTrackerTests()
    {
        _tracker = new PlaySessionTracker(_scanner, _time);
        _tracker.UpdateLibrary([new TrackableGame(1, "The Witcher 3", WitcherDir), new TrackableGame(2, "Hades", null)]);
    }

    private RunningProcess Game(int pid, DateTime started, string exe = @"bin\x64\witcher3.exe") =>
        new(pid, Path.Combine(WitcherDir, exe), started);

    [Fact]
    public void Launched_game_session_starts_when_its_process_appears_and_ends_after_it_exits()
    {
        _tracker.ExpectLaunch(new TrackableGame(1, "The Witcher 3", WitcherDir));
        Assert.Equal(TickResult.None, _tracker.Tick());           // launcher still starting the game

        _scanner.Processes.Add(Game(100, T0.AddSeconds(20)));
        _time.Now = T0.AddSeconds(30);
        var started = _tracker.Tick().Started;
        Assert.NotNull(started);
        Assert.Equal(T0.AddSeconds(20), started!.StartedAtUtc);   // process start time, not detection time

        _time.Now = T0.AddMinutes(90);
        Assert.Equal(TickResult.None, _tracker.Tick());           // still running
        _scanner.Processes.Clear();
        _time.Now = T0.AddMinutes(90).AddSeconds(3);
        Assert.Equal(TickResult.None, _tracker.Tick());           // one missed scan is tolerated
        _time.Now = T0.AddMinutes(90).AddSeconds(6);
        Assert.Equal(TickResult.None, _tracker.Tick());           // within the relaunch grace
        _time.Now = T0.AddMinutes(90).AddSeconds(33);
        var ended = _tracker.Tick().Ended;
        Assert.NotNull(ended);
        Assert.Equal(T0.AddMinutes(90), ended!.LastSeenUtc);      // ends when last seen, not when noticed
        Assert.Null(_tracker.Active);
    }

    [Fact]
    public void A_brief_gap_between_processes_does_not_split_the_session()
    {
        _scanner.Processes.Add(Game(100, T0));                    // launcher exe
        Assert.NotNull(_tracker.Tick().Started);
        _scanner.Processes.Clear();
        Assert.Equal(TickResult.None, _tracker.Tick());           // missed once
        _scanner.Processes.Add(Game(101, T0.AddSeconds(4), @"bin\x64\game.exe"));
        Assert.Equal(TickResult.None, _tracker.Tick());           // same session continues
        Assert.Equal(T0, _tracker.Active!.StartedAtUtc);
    }

    [Fact]
    public void Games_started_outside_arcadia_are_detected_only_when_enabled()
    {
        _scanner.Processes.Add(Game(100, T0.AddMinutes(-30)));
        _tracker.DetectExternalLaunches = false;
        Assert.Equal(TickResult.None, _tracker.Tick());
        _tracker.DetectExternalLaunches = true;
        var started = _tracker.Tick().Started;
        Assert.Equal(1, started!.GameId);
        Assert.Equal(T0.AddMinutes(-30), started.StartedAtUtc);   // a session already running is back-dated correctly
    }

    [Fact]
    public void Launchers_redistributables_and_other_folders_never_count()
    {
        _scanner.Processes.Add(new RunningProcess(1, @"C:\Program Files (x86)\Steam\steam.exe", T0));
        _scanner.Processes.Add(new RunningProcess(2, Path.Combine(WitcherDir, @"_CommonRedist\vcredist\vc_redist.x64.exe"), T0));
        _scanner.Processes.Add(new RunningProcess(3, @"D:\SteamLibrary\steamapps\common\The Witcher 3 Mods\tool.exe", T0));
        Assert.Equal(TickResult.None, _tracker.Tick());
    }

    [Fact]
    public void An_unknown_folder_is_learned_from_the_first_new_window_after_launch()
    {
        _scanner.Windowed.Add(new RunningProcess(5, @"C:\Windows\explorer.exe", T0.AddHours(-1)));
        _tracker.ExpectLaunch(new TrackableGame(2, "Hades", null));

        var hadesExe = @"E:\Games\Hades\x64\Hades.exe";
        _scanner.Windowed.Add(new RunningProcess(7, @"C:\Program Files (x86)\Steam\steam.exe", T0.AddSeconds(2)));
        _scanner.Windowed.Add(new RunningProcess(8, hadesExe, T0.AddSeconds(10)));
        _scanner.Processes.Add(new RunningProcess(8, hadesExe, T0.AddSeconds(10)));
        _time.Now = T0.AddSeconds(15);

        var result = _tracker.Tick();
        Assert.Equal((2, @"E:\Games\Hades\x64"), result.Learned);
        Assert.Equal(2, result.Started!.GameId);
    }

    [Fact]
    public void Pending_launch_expires_and_end_now_returns_the_session()
    {
        _tracker.ExpectLaunch(new TrackableGame(1, "The Witcher 3", WitcherDir));
        _time.Now = T0.AddMinutes(6);
        _tracker.DetectExternalLaunches = false;
        Assert.Equal(TickResult.None, _tracker.Tick());           // expires the pending launch
        Assert.False(_tracker.IsWatching);

        _tracker.Restore(new ActiveSession(1, "The Witcher 3", T0, WitcherDir, T0.AddMinutes(1)));
        var ended = _tracker.EndNow();
        Assert.Equal(T0.AddMinutes(6), ended!.LastSeenUtc);
        Assert.Null(_tracker.Active);
    }

    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:\Games\Hades", true)]
    public void Overly_broad_folders_are_rejected(string dir, bool ok) =>
        Assert.Equal(ok, PlaySessionTracker.IsReasonableGameDirectory(dir));

    [Fact]
    public void Program_files_root_is_rejected() =>
        Assert.False(PlaySessionTracker.IsReasonableGameDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
}

public class LaunchTargetInfoTests
{
    [Theory]
    [InlineData("steam://rungameid/292030", 292030)]
    [InlineData("steam://run/1091500", 1091500)]
    [InlineData("com.epicgames.launcher://apps/x", null)]
    [InlineData(null, null)]
    public void Steam_app_ids(string? target, int? expected) => Assert.Equal(expected, LaunchTargetInfo.SteamAppId(target));

    [Fact]
    public void Epic_app_names() =>
        Assert.Equal("Fortnite", LaunchTargetInfo.EpicAppName("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true"));

    [Fact]
    public void Executable_folder() =>
        Assert.Equal(@"C:\Games\Hades", LaunchTargetInfo.ExecutableDirectory(@"""C:\Games\Hades\Hades.exe"""));
}

public class ScreenshotMatcherTests
{
    private static readonly CaptureGame[] Games =
    [
        new(1, "The Witcher 3: Wild Hunt", 292030),
        new(2, "Hades", null),
        new(3, "Cyberpunk 2077", 1091500),
    ];

    [Fact]
    public void The_game_being_played_wins() =>
        Assert.Equal(2, ScreenshotMatcher.Match(@"C:\Users\me\Videos\Captures\Something 2026-09-27.png", Games, playingGameId: 2));

    [Fact]
    public void Steam_folder_layout_identifies_the_app() =>
        Assert.Equal(3, ScreenshotMatcher.Match(@"C:\Program Files (x86)\Steam\userdata\123\760\remote\1091500\screenshots\20260927_1.jpg", Games, null));

    [Theory]
    [InlineData(@"C:\Users\me\Videos\Captures\The Witcher 3 2026-09-27 21-30-11.png", 1)]
    [InlineData(@"C:\Users\me\Videos\Hades\Hades Screenshot 2026.09.27 - 21.30.11.12.png", 2)]
    [InlineData(@"C:\Users\me\Videos\Cyberpunk 2077\cap_0001.png", 3)]
    [InlineData(@"C:\Users\me\Videos\Captures\Desktop 2026-09-27 10-00-00.png", null)]
    public void Game_bar_and_nvidia_names_are_matched_by_title(string path, int? expected) =>
        Assert.Equal(expected, ScreenshotMatcher.Match(path, Games, null));
}

public sealed class GameInstallLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcadia-locator-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Steam_games_are_found_in_any_library_folder()
    {
        var steam = Path.Combine(_root, "Steam");
        var library = Path.Combine(_root, "Library2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "The Witcher 3"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace(@"\", @"\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace(@"\", @"\\")}\"\n\t}}\n}}");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_292030.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"292030\"\n\t\"installdir\"\t\t\"The Witcher 3\"\n}");

        var locator = new GameInstallLocator(NullLogger<GameInstallLocator>.Instance, steamRoot: steam, epicManifestFolder: Path.Combine(_root, "none"));

        Assert.Equal(Path.Combine(library, "steamapps", "common", "The Witcher 3"), locator.Locate("steam://rungameid/292030"));
        Assert.Null(locator.Locate("steam://rungameid/1"));
    }

    [Fact]
    public void Epic_games_are_found_from_manifests()
    {
        var manifests = Path.Combine(_root, "Manifests");
        var install = Path.Combine(_root, "Epic", "Hades");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(manifests, "abc.item"),
            $"{{ \"AppName\": \"Min\", \"CatalogItemId\": \"cat123\", \"InstallLocation\": \"{install.Replace(@"\", @"\\")}\" }}");

        var locator = new GameInstallLocator(NullLogger<GameInstallLocator>.Instance, steamRoot: Path.Combine(_root, "nosteam"), epicManifestFolder: manifests);

        Assert.Equal(install, locator.Locate("com.epicgames.launcher://apps/ns%3Acat123%3AMin?action=launch"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public sealed class TrackingCatalogTests : IAsyncLifetime
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("Data Source=:memory:");
    private Arcadia.Data.GameCatalog _catalog = null!;

    private sealed class Factory(Microsoft.EntityFrameworkCore.DbContextOptions<Arcadia.Data.ArcadiaDbContext> o)
        : Microsoft.EntityFrameworkCore.IDbContextFactory<Arcadia.Data.ArcadiaDbContext>
    {
        public Arcadia.Data.ArcadiaDbContext CreateDbContext() => new(o);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var factory = new Factory(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions
            .UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Arcadia.Data.ArcadiaDbContext>(), _connection).Options);
        await new Arcadia.Data.DatabaseInitializer(factory, TimeProvider.System, NullLogger<Arcadia.Data.DatabaseInitializer>.Instance).InitializeAsync(false);
        _catalog = new Arcadia.Data.GameCatalog(factory, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Install_folder_automatic_sessions_and_capture_dedupe_round_trip()
    {
        var id = await _catalog.SaveGameAsync(new Arcadia.Core.Games.Game { Title = "Hades" });

        await _catalog.SetInstallDirectoryAsync(id, @"E:\Games\Hades\");
        await _catalog.LogSessionAsync(new Arcadia.Core.Games.SessionLog(id, DateTime.UtcNow.AddHours(-1), 45, "auto", IsAutomatic: true));
        await _catalog.AddScreenshotAsync(new Arcadia.Core.Games.Screenshot { GameId = id, Title = "s", SourcePath = @"C:\Captures\a.png", CapturedAtUtc = DateTime.UtcNow });

        var game = (await _catalog.GetGameDetailAsync(id))!;
        Assert.Equal(@"E:\Games\Hades", game.InstallDirectory);
        Assert.True(Assert.Single(game.Sessions).IsAutomatic);
        Assert.True(await _catalog.HasScreenshotFromSourceAsync(@"C:\Captures\a.png"));
        Assert.False(await _catalog.HasScreenshotFromSourceAsync(@"C:\Captures\b.png"));
    }
}
