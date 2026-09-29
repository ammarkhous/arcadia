using System.Runtime.InteropServices;
using System.Windows;

namespace Arcadia.App.Shell;

/// <summary>Checks a saved window position against the monitors that are connected now.</summary>
internal static class WindowPlacement
{
    /// <summary>True when the middle of the window's title bar lies on a connected monitor (so it can be grabbed).</summary>
    public static bool IsTitleBarVisible(Rect dipBounds)
    {
        var scale = GetDpiForSystem() / 96.0;
        var strip = new RECT
        {
            Left = (int)((dipBounds.Left + Math.Min(120, dipBounds.Width / 4)) * scale),
            Top = (int)(dipBounds.Top * scale),
            Right = (int)((dipBounds.Right - Math.Min(120, dipBounds.Width / 4)) * scale),
            Bottom = (int)((dipBounds.Top + 40) * scale),
        };
        return strip.Right > strip.Left && MonitorFromRect(ref strip, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
