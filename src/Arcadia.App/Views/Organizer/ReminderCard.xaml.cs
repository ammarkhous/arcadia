using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Arcadia.App.Services;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Views.Organizer;

/// <summary>The corner card that lists due reminders (one window, however many reminders).</summary>
public partial class ReminderCard : Window
{
    private readonly ObservableCollection<ReminderNotice> _items = [];

    public ReminderCard()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        SizeChanged += (_, _) => PlaceInCorner();
    }

    public event Action<ReminderNotice>? OpenRequested;
    public event Action<ReminderNotice>? SnoozeRequested;
    public event Action<ReminderNotice>? Dismissed;

    public void Add(ReminderNotice notice)
    {
        if (_items.Any(n => n.Due.Key == notice.Due.Key)) return;
        _items.Insert(0, notice);
        while (_items.Count > 4) _items.RemoveAt(_items.Count - 1);
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    private static ReminderNotice? NoticeOf(object sender) => (sender as FrameworkElement)?.Tag as ReminderNotice;

    private void Remove(ReminderNotice n)
    {
        _items.Remove(n);
        if (_items.Count == 0) Hide();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (NoticeOf(sender) is not { } n) return;
        Remove(n);
        OpenRequested?.Invoke(n);
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        if (NoticeOf(sender) is not { } n) return;
        Remove(n);
        SnoozeRequested?.Invoke(n);
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        if (NoticeOf(sender) is not { } n) return;
        Remove(n);
        Dismissed?.Invoke(n);
    }

    private void OnDismissAll(object sender, RoutedEventArgs e)
    {
        foreach (var n in _items.ToList()) Dismissed?.Invoke(n);
        _items.Clear();
        Hide();
    }
}

/// <summary>
/// Shows reminders: the corner card, the bell list, a short Windows sound and a flashing taskbar button when Arcadia
/// is in the background. Everything is local. Failures are logged and never escalate (the bell keeps the reminder).
/// </summary>
public sealed class ReminderPresenter(IServiceProvider services, NotificationCenter center, ISettingsService settings, ILogger<ReminderPresenter> logger) : IReminderPresenter
{
    private ReminderCard? _card;
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(10);

    public void Present(IReadOnlyList<ReminderNotice> notices)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || notices.Count == 0) return;
        dispatcher.BeginInvoke(() =>
        {
            foreach (var n in notices.OrderBy(n => n.Due.TriggerAt)) center.Add(n);
            try
            {
                var card = _card ??= CreateCard();
                foreach (var n in notices.OrderBy(n => n.Due.TriggerAt)) card.Add(n);
                if (!card.IsVisible) card.Show();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The reminder card could not be shown; the reminder stays in the notification list");
            }
            if (settings.Current.Organizer.ReminderSound)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception ex) { logger.LogDebug(ex, "Reminder sound failed"); }
            }
            FlashTaskbar();
            logger.LogInformation("Showed {Count} reminder(s)", notices.Count);
        });
    }

    private ReminderCard CreateCard()
    {
        var card = new ReminderCard();
        card.OpenRequested += n =>
        {
            center.Remove(n);
            if (Application.Current?.MainWindow is { } w)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
            }
            services.GetRequiredService<INavigationService>().Open(n.Target);
        };
        card.SnoozeRequested += n =>
        {
            center.Remove(n);
            services.GetRequiredService<ReminderService>().Snooze(n, SnoozeFor);
        };
        card.Dismissed += center.Remove;
        return card;
    }

    private void FlashTaskbar()
    {
        try
        {
            if (Application.Current?.MainWindow is not { IsActive: false } w) return;
            var info = new FlashInfo { cbSize = (uint)Marshal.SizeOf<FlashInfo>(), hwnd = new WindowInteropHelper(w).Handle, dwFlags = 3 | 12, uCount = 3 };
            FlashWindowEx(ref info);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Flashing the taskbar failed"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
