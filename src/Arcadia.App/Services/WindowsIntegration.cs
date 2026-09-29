using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.App.Services;

/// <summary>Status shown on the tray icon (a small corner mark, never animated).</summary>
public enum TrayStatus { Normal, Gaming, Paused }

public sealed record TrayMenuItem(string Label, string Icon, Action Run, bool? IsChecked = null, bool Separator = false);

/// <summary>
/// Arcadia's notification-area icon, done with Shell_NotifyIcon on a message-only window (no WinForms). Left click opens
/// Arcadia; right click shows the Arcadia-styled menu. Also carries Windows notifications (balloons become toasts) and
/// the one optional system-wide hotkey (RegisterHotKey), both owned by the same hidden window.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int WmHotkey = 0x0312;
    private const int NinBalloonUserClick = 0x0405;
    private const int HotkeyId = 0xA3C1;
    private readonly ILogger<TrayService> _logger;
    private HwndSource? _window;
    private bool _added;
    private int _taskbarCreated;
    private IntPtr _icon;
    private TrayStatus _status;
    private string _tip = "Arcadia";
    private Action? _balloonClick;

    public TrayService(ILogger<TrayService> logger) => _logger = logger;

    public event EventHandler? Activated;
    public event EventHandler? HotkeyPressed;
    /// <summary>Builds the menu when it is opened (so checks are current).</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuItems { get; set; }

    public bool IsVisible => _added;

    public void Show()
    {
        if (_added) return;
        EnsureWindow();
        _icon = CreateIcon(_status);
        var data = Data(Nif.Message | Nif.Icon | Nif.Tip | Nif.ShowTip);
        _added = Shell_NotifyIcon(Nim.Add, ref data);
        data.uTimeoutOrVersion = 4;
        Shell_NotifyIcon(Nim.SetVersion, ref data);
        if (!_added) _logger.LogWarning("The tray icon could not be added");
    }

    public void Hide()
    {
        if (!_added) return;
        var data = Data(0);
        Shell_NotifyIcon(Nim.Delete, ref data);
        _added = false;
    }

    public void SetStatus(TrayStatus status, string tip)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        if (status == _status && !_added) return;
        if (status != _status)
        {
            _status = status;
            var old = _icon;
            _icon = CreateIcon(status);
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        if (!_added) return;
        var data = Data(Nif.Icon | Nif.Tip | Nif.ShowTip);
        Shell_NotifyIcon(Nim.Modify, ref data);
    }

    /// <summary>A Windows notification (needs the tray icon). Returns false when it could not be shown.</summary>
    public bool ShowNotification(string title, string text, bool warning, Action? onClick)
    {
        if (!_added) return false;
        _balloonClick = onClick;
        var data = Data(Nif.Info);
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(string.IsNullOrWhiteSpace(text) ? " " : text, 255);
        data.dwInfoFlags = (warning ? 2 : 1) | 0x20; // NIIF_WARNING / NIIF_INFO, NIIF_LARGE_ICON
        return Shell_NotifyIcon(Nim.Modify, ref data);
    }

    /// <summary>Registers the system-wide hotkey; false when another program owns it (reported as a conflict).</summary>
    public bool RegisterHotkey(KeyGesture? gesture)
    {
        EnsureWindow();
        UnregisterHotKey(_window!.Handle, HotkeyId);
        if (gesture == null) return true;
        uint mods = 0x4000; // MOD_NOREPEAT
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
        var ok = RegisterHotKey(_window.Handle, HotkeyId, mods, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));
        if (!ok) _logger.LogWarning("Global shortcut {Gesture} is taken by another program", gesture.GetDisplayStringForCulture(null));
        return ok;
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        _window = new HwndSource(new HwndSourceParameters("ArcadiaTray") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case 0x0202: // WM_LBUTTONUP
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x0205 or 0x007B: // WM_RBUTTONUP, WM_CONTEXTMENU
                    ShowMenu();
                    break;
                case NinBalloonUserClick:
                    (_balloonClick ?? (() => Activated?.Invoke(this, EventArgs.Empty)))();
                    _balloonClick = null;
                    break;
            }
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        else if (msg == _taskbarCreated && _added)
        {
            _added = false; // Explorer restarted: add the icon again
            Show();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (MenuItems == null) return;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var item in MenuItems())
        {
            if (item.Separator) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem
            {
                Header = item.Label,
                Icon = new Arcadia.UI.Controls.ArcadiaIcon { Kind = item.Icon, Width = 14, Height = 14 },
                IsCheckable = item.IsChecked != null,
                IsChecked = item.IsChecked == true,
            };
            var run = item.Run;
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
        }
        menu.Opened += (_, _) =>
        {
            // the menu must own the foreground or it would not close when clicking elsewhere
            if (PresentationSource.FromVisual(menu) is HwndSource src) SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    /// <summary>The Arcadia icon with a corner mark: cyan for Gaming Mode, amber bars for paused automations.</summary>
    private static IntPtr CreateIcon(TrayStatus status)
    {
        const int size = 32;
        var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Arcadia;component/Assets/arcadia.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(frame, new Rect(0, 0, size, size));
            if (status == TrayStatus.Gaming)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A)), null, new Point(25, 25), 7.5, 7.5);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x45, 0xD2, 0xFF)), null, new Point(25, 25), 5.5, 5.5);
            }
            else if (status == TrayStatus.Paused)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A)), null, new Rect(17, 17, 15, 15), 3, 3);
                var amber = new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x64));
                dc.DrawRectangle(amber, null, new Rect(20, 19, 3.5, 11));
                dc.DrawRectangle(amber, null, new Rect(26, 19, 3.5, 11));
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var pixels = new byte[size * size * 4];
        bmp.CopyPixels(pixels, size * 4, 0);
        var color = CreateBitmap(size, size, 1, 32, pixels);
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private NotifyIconData Data(Nif flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window?.Handle ?? IntPtr.Zero,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    public void Dispose()
    {
        Hide();
        if (_window != null)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _window.Dispose();
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    // ── interop ──

    [Flags] private enum Nif { Message = 1, Icon = 2, Tip = 4, Info = 0x10, ShowTip = 0x80 }
    private enum Nim { Add = 0, Modify = 1, Delete = 2, SetVersion = 4 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Nif uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(Nim message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

/// <summary>
/// The one place modules notify the user (Phase 9). Per-category preference (in-app, Windows, off), duplicate
/// suppression (same message within a minute), a rate limit for Windows notifications, and quiet Gaming Mode
/// (only reminders and warnings reach Windows while gaming).
/// </summary>
public sealed class ArcadiaNotifier(ISettingsService settings, INotificationService inApp, TrayService tray, PerformanceService performance, TimeProvider time, ILogger<ArcadiaNotifier> logger)
{
    private static readonly TimeSpan SameMessageWindow = TimeSpan.FromMinutes(1);
    private const int WindowsPerMinute = 4;
    private readonly Dictionary<string, DateTime> _recent = [];
    private readonly Queue<DateTime> _windowsSent = new();

    /// <summary>Is Arcadia in front of the user (so an in-app toast is enough)?</summary>
    public Func<bool> IsForeground { get; set; } = () => true;
    /// <summary>Opens a record when a Windows notification is clicked.</summary>
    public Action<EntityRef?>? Open { get; set; }

    public int Suppressed { get; private set; }

    public void Notify(string category, string title, string text, EntityRef? target = null, NotificationKind kind = NotificationKind.Info)
    {
        var n = settings.Current.Notifications;
        var pref = category switch
        {
            "reminder" => n.Reminder, "automation" => n.Automation, "gaming" => n.Gaming, "warning" => n.Warning, _ => n.Info,
        };
        if (pref == "off") return;
        var now = time.GetUtcNow().UtcDateTime;
        var key = category + "|" + title + "|" + text;
        foreach (var old in _recent.Where(kv => now - kv.Value > SameMessageWindow).Select(kv => kv.Key).ToList()) _recent.Remove(old);
        if (_recent.ContainsKey(key)) { Suppressed++; return; }
        _recent[key] = now;

        var message = string.IsNullOrWhiteSpace(text) ? title : title + " — " + text;
        var toWindows = pref == "windows" && !IsForeground()
                        && !(performance.QuietNotifications && category is not ("reminder" or "warning"));
        if (toWindows)
        {
            while (_windowsSent.Count > 0 && now - _windowsSent.Peek() > TimeSpan.FromMinutes(1)) _windowsSent.Dequeue();
            if (_windowsSent.Count >= WindowsPerMinute) { Suppressed++; logger.LogDebug("Windows notification rate limit"); return; }
            if (tray.ShowNotification(title, text, category == "warning", target is { } t ? () => Open?.Invoke(t) : null))
            {
                _windowsSent.Enqueue(now);
                return;
            }
        }
        inApp.Show(message, kind);
    }
}

/// <summary>"Start Arcadia with Windows": the per-user Run key, written only when the user turns the option on.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Arcadia";

    public static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && v.Contains(Path.GetFileName(Environment.ProcessPath ?? "Arcadia.exe"), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Central keyboard shortcuts (Phase 9): in-window gestures for Oracle, palette, Quick Capture and Gaming Mode, read from
/// settings, with conflict detection. The only system-wide hotkey is the optional "Show Arcadia" (see <see cref="TrayService"/>).
/// </summary>
public static class ShortcutManager
{
    /// <summary>Fixed shortcuts other features own (Ctrl+N, Ctrl+S, Ctrl+F, Ctrl+Shift+H).</summary>
    public static readonly string[] Reserved = ["Ctrl+N", "Ctrl+S", "Ctrl+F", "Ctrl+Shift+H"];

    public static readonly string[] Choices = ["", "Ctrl+Space", "Ctrl+Shift+Space", "Ctrl+Shift+O", "Ctrl+Alt+Space", "Ctrl+K", "Ctrl+Shift+K", "Ctrl+Shift+N", "Ctrl+Shift+G", "Ctrl+Alt+G", "Ctrl+Shift+A", "Ctrl+Alt+A", "F9", "F10"];

    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(text); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException) { return null; }
    }

    public static bool Matches(KeyGesture? g, KeyEventArgs e)
    {
        if (g == null) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == g.Key && Keyboard.Modifiers == g.Modifiers;
    }

    /// <summary>The actions whose gestures collide (with each other or with a reserved shortcut).</summary>
    public static List<string> Conflicts(IReadOnlyDictionary<string, string> gestures)
    {
        var conflicts = new List<string>();
        foreach (var group in gestures.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).GroupBy(kv => kv.Value.Replace(" ", ""), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1) conflicts.AddRange(group.Select(kv => kv.Key));
            if (Reserved.Contains(group.Key, StringComparer.OrdinalIgnoreCase)) conflicts.AddRange(group.Select(kv => kv.Key));
        }
        return conflicts.Distinct().ToList();
    }
}
