using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Diagnostics;

/// <summary>
/// QA only. ARCADIA_QA_STARTUP=1: logs UI-thread work of 8 ms or more until the first frame, to find what delays it.
/// ARCADIA_QA_STALLS=1: keeps running for the whole session and logs every UI-thread operation of 50 ms or more (UI-thread audit).
/// </summary>
internal static class StartupTrace
{
    public static void Attach(Dispatcher dispatcher, Window window, ILogger logger, bool wholeSession = false)
    {
        var threshold = wholeSession ? 50 : 8;
        var field = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        var argsField = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
        var started = new Dictionary<DispatcherOperation, long>();
        var done = false;
        double busy = 0; var count = 0;
        void OnStarted(object? s, DispatcherHookEventArgs e) => started[e.Operation] = Stopwatch.GetTimestamp();
        void OnCompleted(object? s, DispatcherHookEventArgs e)
        {
            if (!started.Remove(e.Operation, out var t0)) return;
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            busy += ms; count++;
            if (ms < threshold) return;
            var method = field?.GetValue(e.Operation) as Delegate;
            var name = method == null ? "?" : $"{method.Method.DeclaringType?.FullName}.{method.Method.Name}";
            // async continuations: the posted state is the resumed Action; name its state machine
            var state = argsField?.GetValue(e.Operation);
            if (state is Delegate resumed)
            {
                var box = resumed.Target;
                var sm = box?.GetType().GetField("StateMachine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(box);
                name = sm?.GetType().FullName ?? (box?.GetType().FullName + "." + resumed.Method.Name);
            }
            logger.LogInformation("StartupTrace: {Ms:0} ms at {At:0} ({Priority}) {Name}", ms, App.SinceProcessStart(), e.Operation.Priority, name);
            if (wholeSession && ms >= 300 && e.Operation.Priority == DispatcherPriority.Render)
            {
                logger.LogInformation("StartupTrace: visuals in window {Count}", CountVisuals(window));
                var lists = new List<(int Count, string What)>();
                Collect(window, lists);
                foreach (var (count, what) in lists.OrderByDescending(l => l.Count).Take(4))
                    logger.LogInformation("StartupTrace: visuals {Count} in {What}", count, what);
            }
        }
        dispatcher.Hooks.OperationStarted += OnStarted;
        dispatcher.Hooks.OperationCompleted += OnCompleted;
        var frames = 0;
        void OnRendering(object? s, EventArgs e)
        {
            if (frames++ == 0) logger.LogInformation("StartupTrace: first CompositionTarget.Rendering at {At:0}", App.SinceProcessStart());
        }
        CompositionTarget.Rendering += OnRendering;
        window.Loaded += (_, _) => logger.LogInformation("StartupTrace: window Loaded at {At:0}", App.SinceProcessStart());
        window.ContentRendered += (_, _) =>
        {
            if (done || wholeSession) return;
            done = true;
            dispatcher.Hooks.OperationStarted -= OnStarted;
            dispatcher.Hooks.OperationCompleted -= OnCompleted;
            CompositionTarget.Rendering -= OnRendering;
            logger.LogInformation("StartupTrace: ContentRendered at {At:0}; UI thread busy {Busy:0} ms in {Count} operations", App.SinceProcessStart(), busy, count);
        };
    }

    private static int CountVisuals(DependencyObject root)
    {
        var n = 1;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) n += CountVisuals(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    private static void Collect(DependencyObject node, List<(int, string)> lists)
    {
        if (node is System.Windows.Controls.ItemsControl ic && ic.Items.Count > 20)
        {
            var binding = System.Windows.Data.BindingOperations.GetBinding(ic, System.Windows.Controls.ItemsControl.ItemsSourceProperty)?.Path?.Path;
            var owner = node;
            while (owner != null && owner is not System.Windows.Controls.UserControl) owner = VisualTreeHelper.GetParent(owner);
            lists.Add((CountVisuals(node), $"{ic.GetType().Name} '{binding}' ({ic.Items.Count} items) in {owner?.GetType().Name}"));
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i), lists);
    }
}
