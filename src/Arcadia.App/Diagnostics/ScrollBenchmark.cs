using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Arcadia.App.ViewModels.Games;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Diagnostics;

/// <summary>
/// QA benchmark: opens the library, scrolls it from top to bottom at a steady speed and logs frame-time
/// statistics ("Perf: scroll ..."). Used to keep large libraries smooth.
/// </summary>
internal static class ScrollBenchmark
{
    public static async Task RunAsync(Window window, GamesViewModel games, bool list, ILogger logger)
    {
        var opened = Stopwatch.StartNew();
        games.IsListView = list;
        await games.ActivateAsync();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        logger.LogInformation("Perf: library ({Mode}, {Count} games) ready in {Ms:0} ms", list ? "list" : "grid", games.Items.Count, opened.Elapsed.TotalMilliseconds);
        await Task.Delay(800);

        var viewer = FindScrollViewers(window).FirstOrDefault(v => v.IsVisible && v.ScrollableHeight > 0 && v.CanContentScroll);
        if (viewer == null)
        {
            logger.LogWarning("Perf: scroll benchmark found nothing to scroll");
            return;
        }

        var frames = new List<double>();
        var last = Stopwatch.GetTimestamp();
        const double pixelsPerSecond = 2400;
        var clock = Stopwatch.StartNew();
        var done = new TaskCompletionSource();
        void OnFrame(object? s, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp();
            frames.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            var target = clock.Elapsed.TotalSeconds * pixelsPerSecond;
            viewer.ScrollToVerticalOffset(target);
            if (target >= viewer.ScrollableHeight) done.TrySetResult();
        }
        CompositionTarget.Rendering += OnFrame;
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        CompositionTarget.Rendering -= OnFrame;

        frames.RemoveAt(0);
        frames.Sort();
        double P(double q) => frames[Math.Min(frames.Count - 1, (int)(frames.Count * q))];
        logger.LogInformation(
            "Perf: scroll {Mode} {Height:0} px in {Seconds:0.0} s: {Frames} frames, avg {Avg:0.0} ms, p50 {P50:0.0}, p95 {P95:0.0}, worst {Worst:0.0}, over 33 ms: {Janky}; working set {Mb:0} MB",
            list ? "list" : "grid", viewer.ScrollableHeight, clock.Elapsed.TotalSeconds, frames.Count, frames.Average(), P(0.5), P(0.95), frames[^1],
            frames.Count(f => f > 33.4), Process.GetCurrentProcess().WorkingSet64 / 1048576.0);
    }

    private static IEnumerable<ScrollViewer> FindScrollViewers(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) yield return sv;
            foreach (var nested in FindScrollViewers(child)) yield return nested;
        }
    }
}
