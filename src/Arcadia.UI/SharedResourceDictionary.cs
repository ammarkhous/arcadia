using System.Windows;

namespace Arcadia.UI;

/// <summary>
/// A merged dictionary that is parsed once per URI and shared everywhere it is referenced.
/// Arcadia's theme files each merge the palette/typography they depend on; without sharing, the same XAML
/// would be parsed several times at startup.
/// </summary>
public sealed class SharedResourceDictionary : ResourceDictionary
{
    private static readonly Dictionary<string, ResourceDictionary> Cache = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _source;

    public new Uri? Source
    {
        get => _source;
        set
        {
            _source = value;
            if (value == null) return;
            var absolute = value.IsAbsoluteUri ? value : new Uri("pack://application:,,," + value.OriginalString, UriKind.Absolute);
            if (!Cache.TryGetValue(absolute.AbsoluteUri, out var shared))
            {
                shared = new ResourceDictionary { Source = absolute };
                Cache[absolute.AbsoluteUri] = shared;
            }
            MergedDictionaries.Add(shared);
        }
    }

    /// <summary>Number of distinct dictionaries parsed (diagnostics).</summary>
    public static int ParsedCount => Cache.Count;
}

/// <summary>Cheap render-cost counters for the startup performance log.</summary>
public static class RenderStats
{
    private static long _sceneTicks;
    private static int _scenes;

    public static void AddScene(long ticks) { Interlocked.Add(ref _sceneTicks, ticks); Interlocked.Increment(ref _scenes); }
    public static int ScenesPainted => _scenes;
    public static double SceneMilliseconds => _sceneTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
