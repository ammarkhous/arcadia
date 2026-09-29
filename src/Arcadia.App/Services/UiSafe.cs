using Arcadia.App.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// For the few <c>async void</c> view event handlers (drops, password boxes): a failure is logged and shown as a quiet
/// notification instead of reaching the global error window. Cancellation is not an error.
/// </summary>
public static class UiSafe
{
    public static async void Run(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var sp = App.Services;
            sp.GetService<ILoggerFactory>()?.CreateLogger("Arcadia.App.UiSafe").LogError(ex, "{What} failed", what);
            sp.GetService<INotificationService>()?.Show(LocalizationManager.Instance["Error_Message"], NotificationKind.Warning);
        }
    }
}
