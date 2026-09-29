using System.Security.Cryptography;
using System.Text;

namespace Arcadia.App.Services;

/// <summary>
/// One Arcadia per library: a second launch signals the running instance to come to the front and exits.
/// Scoped by data folder, so isolated runs (e.g. --data-dir for QA) can coexist.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Returns a guard for the first instance; for a second instance, wakes the first and returns null.
    /// </summary>
    public static SingleInstance? TryAcquire(string scope, Action onActivate)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToUpperInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: true, $@"Local\Arcadia.Instance.{id}", out bool createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Arcadia.Activate.{id}");
        if (createdNew) return new SingleInstance(mutex, activate, onActivate);

        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* released on another thread's exit */ }
        _mutex.Dispose();
    }
}
