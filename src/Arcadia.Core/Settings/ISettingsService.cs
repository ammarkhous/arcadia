namespace Arcadia.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    event EventHandler? SettingsChanged;
    void NotifyChanged();
}
