using System.Text.Json;
using System.Text.Json.Serialization;
using Arcadia.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Settings;

public sealed class JsonSettingsService(string filePath, ILogger<JsonSettingsService> logger) : ISettingsService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // A corrupt settings file must never prevent Arcadia from starting. Keep a copy for diagnosis.
            logger.LogWarning(ex, "Settings file is invalid; falling back to defaults");
            TryBackupCorruptFile();
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, Current, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void NotifyChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private void TryBackupCorruptFile()
    {
        try { File.Copy(filePath, filePath + ".corrupt", overwrite: true); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not back up corrupt settings file"); }
    }
}
