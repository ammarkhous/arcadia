using Arcadia.Infrastructure.Platform;
using Arcadia.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcadia-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public async Task Missing_file_yields_defaults()
    {
        var service = new JsonSettingsService(FilePath, NullLogger<JsonSettingsService>.Instance);
        await service.LoadAsync();
        Assert.Equal("en", service.Current.Language);
        Assert.True(service.Current.AnimationsEnabled);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var service = new JsonSettingsService(FilePath, NullLogger<JsonSettingsService>.Instance);
        await service.LoadAsync();
        service.Current.Language = "ar";
        service.Current.AnimationsEnabled = false;
        service.Current.Startup.StartMaximized = true;
        await service.SaveAsync();

        var reloaded = new JsonSettingsService(FilePath, NullLogger<JsonSettingsService>.Instance);
        await reloaded.LoadAsync();
        Assert.Equal("ar", reloaded.Current.Language);
        Assert.False(reloaded.Current.AnimationsEnabled);
        Assert.True(reloaded.Current.Startup.StartMaximized);
    }

    [Fact]
    public async Task Corrupt_file_falls_back_to_defaults_and_is_preserved()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{ this is not json");
        var service = new JsonSettingsService(FilePath, NullLogger<JsonSettingsService>.Instance);

        await service.LoadAsync();

        Assert.Equal("en", service.Current.Language);
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void Settings_never_contain_secret_fields()
    {
        // API keys must live in protected storage, never in the plain JSON settings file.
        var names = typeof(Arcadia.Core.Settings.AppSettings).Assembly.GetTypes()
            .Where(t => t.Namespace == "Arcadia.Core.Settings")
            .SelectMany(t => t.GetProperties())
            .Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(names, n => n.Contains("key") || n.Contains("secret") || n.Contains("password") || n.Contains("token"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public sealed class AppPathsTests
{
    [Fact]
    public void Data_never_lives_beside_the_executable()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.EnvironmentOverride);
        Environment.SetEnvironmentVariable(AppPaths.EnvironmentOverride, null);
        try
        {
            var paths = new AppPaths();
            var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            Assert.False(paths.DatabasePath.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), paths.SettingsFilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.EnvironmentOverride, previous);
        }
    }
}
