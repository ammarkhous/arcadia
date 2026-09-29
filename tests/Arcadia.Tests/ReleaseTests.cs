using Arcadia.App.Diagnostics;
using Arcadia.App.Services;
using Arcadia.Core.Settings;

namespace Arcadia.Tests;

/// <summary>Phase 10 release behaviour: automatic backup schedule, command-line hardening, release defaults.</summary>
public sealed class ReleaseTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("off", null, null, false)]
    [InlineData("daily", null, null, true)]
    [InlineData("daily", -10, null, false)]
    [InlineData("daily", -24, null, true)]
    [InlineData("weekly", -24 * 3, null, false)]
    [InlineData("weekly", -24 * 7, null, true)]
    [InlineData("update", -1, "1.0.0", false)]
    [InlineData("update", -1, "0.9.0", true)]
    [InlineData("update", null, null, true)]
    public void Automatic_backups_run_only_when_due(string schedule, int? lastHoursAgo, string? lastVersion, bool due)
    {
        var b = new BackupSettings { Schedule = schedule, LastAutomaticUtc = lastHoursAgo is { } h ? Now.AddHours(h) : null, LastVersion = lastVersion };
        Assert.Equal(due, AutomaticBackupService.IsDue(b, Now, "1.0.0"));
    }

    [Fact]
    public void Capture_outputs_must_be_png_files()
    {
        Assert.Equal(@"C:\qa\shot.png", LaunchOptions.Parse(["--capture", @"C:\qa\shot.png"]).CapturePath);
        Assert.Null(LaunchOptions.Parse(["--capture", @"C:\Users\me\Documents\thesis.docx"]).CapturePath);
        Assert.Null(LaunchOptions.Parse(["--art-sheet", @"C:\Windows\win.ini"]).ArtSheetPath);
        Assert.Equal(1234, LaunchOptions.Parse(["--wait-pid", "1234"]).WaitPid);
    }

    [Fact]
    public void Release_defaults_add_no_sample_data_and_no_backups_until_asked()
    {
        var s = new AppSettings();
        Assert.False(s.Development.SeedSampleData);
        Assert.Equal("off", s.Backup.Schedule);
        Assert.False(s.Backup.IncludeReferencedFiles);
        Assert.False(s.FirstRunCompleted);
    }

    [Fact]
    public void Version_is_one_point_oh() => Assert.StartsWith("1.0.0", AppInfo.Version);
}
