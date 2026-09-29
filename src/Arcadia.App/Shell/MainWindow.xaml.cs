using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Arcadia.App.Localization;
using Arcadia.App.Services;
using Arcadia.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Shell;

/// <summary>
/// Custom-chrome main window. Code-behind is limited to window mechanics (caption buttons, maximize
/// compensation, DWM styling, size-dependent layout modes); application behaviour lives in <see cref="ShellViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private const double CompactWidthThreshold = 1280;
    private const double FooterHeightThreshold = 860;
    private const double MottoWidthThreshold = 1440;

    private readonly Arcadia.Core.Settings.ISettingsService _settings;

    public MainWindow(ShellViewModel viewModel, Arcadia.Core.Settings.ISettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = viewModel;
        FocusSearchCommand = new RelayCommand(FocusSearch);
        viewModel.SearchFocusRequested += (_, _) => Dispatcher.BeginInvoke(FocusSearch, System.Windows.Threading.DispatcherPriority.Loaded);
        viewModel.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && viewModel.Palette.IsOpen)
                Dispatcher.BeginInvoke(() => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); PaletteBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedIndex) && PaletteList.SelectedItem != null)
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        };

        viewModel.QuickCapture.FocusRequested += (_, _) =>
            Dispatcher.BeginInvoke(() => { CaptureBox.Focus(); Keyboard.Focus(CaptureBox); CaptureBox.CaretIndex = CaptureBox.Text.Length; }, System.Windows.Threading.DispatcherPriority.Loaded);

        PreviewKeyDown += OnOracleShortcut;
        // like the Oracle page: a quiet overlay prompt hands focus to its layer so the caret animation stops
        _oraclePark = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _oraclePark.Tick += (_, _) => { _oraclePark.Stop(); if (OracleBox.IsKeyboardFocused && OracleBox.Text.Length == 0) Keyboard.Focus(OracleLayer); };
        OracleLayer.Focusable = true;
        OracleLayer.FocusVisualStyle = null;
        OracleBox.GotKeyboardFocus += (_, _) => _oraclePark.Start();
        OracleBox.LostKeyboardFocus += (_, _) => _oraclePark.Stop();
        OracleBox.PreviewKeyDown += (_, _) => { _oraclePark.Stop(); _oraclePark.Start(); };
        OracleLayer.PreviewTextInput += (_, e) =>
        {
            if (!ReferenceEquals(Keyboard.FocusedElement, OracleLayer)) return;
            Keyboard.Focus(OracleBox);
            OracleBox.Text += e.Text;
            OracleBox.CaretIndex = OracleBox.Text.Length;
            e.Handled = true;
        };
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => ApplyWindowState();
        RootBorder.SizeChanged += (_, _) => ApplyResponsiveLayout();
        LocalizationManager.Instance.CultureChanged += (_, _) => ApplyWindowState();
        FitToWorkArea();
        if (!App.IsCaptureMode) RestorePlacement();
        Closing += (_, _) => SavePlacement();
    }

    // ── window placement: remembered, but never restored onto a monitor that is no longer there ──

    private void RestorePlacement()
    {
        if (_settings.Current.Windows.Placement is not { } b || b.Width < 400 || b.Height < 300) return;
        var area = SystemParameters.WorkArea;
        var bounds = new Rect(b.Left, b.Top, Math.Min(b.Width, Math.Max(area.Width, SystemParameters.VirtualScreenWidth)), Math.Min(b.Height, Math.Max(area.Height, SystemParameters.VirtualScreenHeight)));
        if (!WindowPlacement.IsTitleBarVisible(bounds)) return; // stays centred on the primary screen
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (App.IsCaptureMode) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || r.Width < 400 || r.Height < 300) return;
        _settings.Current.Windows.Placement = new Arcadia.Core.Settings.WindowBounds
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized,
        };
    }

    // ── Oracle overlay (Ctrl+Space by default; configurable in Settings) ──

    private bool _oracleHooked;
    private readonly System.Windows.Threading.DispatcherTimer _oraclePark;
    private string? _gestureText;
    private KeyGesture? _gesture;

    private KeyGesture Gesture(string text)
    {
        if (_gesture != null && _gestureText == text) return _gesture;
        _gestureText = text;
        try { _gesture = (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(text); }
        catch (Exception) { _gesture = null; }
        return _gesture ??= new KeyGesture(Key.Space, ModifierKeys.Control);
    }

    /// <summary>
    /// The shortcut manager's in-window gestures (Settings → Shortcuts): palette, Quick Capture, Gaming Mode and Oracle.
    /// Conflicting assignments are refused in Settings, so at most one action owns a gesture.
    /// </summary>
    private void OnOracleShortcut(object sender, KeyEventArgs e)
    {
        var shell = (ShellViewModel)DataContext;
        var keys = _settings.Current.Shortcuts;
        if (ShortcutManager.Matches(ShortcutManager.Parse(keys.Palette), e)) { e.Handled = true; shell.OpenPaletteCommand.Execute(null); return; }
        if (ShortcutManager.Matches(ShortcutManager.Parse(keys.QuickCapture), e)) { e.Handled = true; shell.OpenQuickCaptureCommand.Execute(null); return; }
        if (ShortcutManager.Matches(ShortcutManager.Parse(keys.GamingMode), e))
        {
            e.Handled = true;
            var perf = App.Services.GetRequiredService<PerformanceService>();
            perf.SetGamingMode(!perf.IsGamingMode, "manual");
            return;
        }
        var prefs = _settings.Current.Oracle;
        if (!prefs.Enabled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var g = Gesture(prefs.Shortcut);
        if (key != g.Key || Keyboard.Modifiers != g.Modifiers) return;
        e.Handled = true;
        if (!_oracleHooked)
        {
            _oracleHooked = true;
            shell.Oracle.FocusRequested += (_, _) =>
            {
                if (shell.Oracle.IsOverlayOpen)
                    Dispatcher.BeginInvoke(() => { OracleBox.Focus(); Keyboard.Focus(OracleBox); OracleBox.CaretIndex = OracleBox.Text.Length; }, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }
        shell.ToggleOracleCommand.Execute(null);
    }

    private void OnOracleBackdrop(object sender, MouseButtonEventArgs e) => ((ShellViewModel)DataContext).Oracle.CloseOverlay();

    private void OnOracleBackdropClick(object sender, RoutedEventArgs e) => ((ShellViewModel)DataContext).Oracle.CloseOverlay();

    /// <summary>Enter runs, Up/Down walk the history, Tab jumps into the result, Escape closes.</summary>
    private void OnOracleKey(object sender, KeyEventArgs e)
    {
        var oracle = ((ShellViewModel)DataContext).Oracle;
        switch (e.Key)
        {
            case Key.Enter:
                oracle.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                oracle.HistoryUpCommand.Execute(null);
                OracleBox.CaretIndex = OracleBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                oracle.HistoryDownCommand.Execute(null);
                OracleBox.CaretIndex = OracleBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = OracleResult.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                break;
        }
    }

    private void OnOracleLayerKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var oracle = ((ShellViewModel)DataContext).Oracle;
        if (!OracleBox.IsKeyboardFocused && !ReferenceEquals(Keyboard.FocusedElement, OracleLayer)) { OracleBox.Focus(); Keyboard.Focus(OracleBox); }
        else oracle.CloseOverlay();
        e.Handled = true;
    }

    /// <summary>Icon-only sidebar for narrow windows.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    public ICommand FocusSearchCommand { get; }

    /// <summary>Ctrl+F: the current module's search box if it has one, otherwise the header search.</summary>
    private void FocusSearch()
    {
        TextBox target = Views.Controls.Shortcuts.FindSearchBox(RootBorder) is { } local ? local : SearchBox;
        target.Focus();
        Keyboard.Focus(target);
        target.SelectAll();
    }

    private void OnPaletteBackdrop(object sender, MouseButtonEventArgs e) => ((ShellViewModel)DataContext).Palette.Close();

    private void OnCaptureBackdrop(object sender, MouseButtonEventArgs e) => ((ShellViewModel)DataContext).QuickCapture.CloseCommand.Execute(null);

    /// <summary>Quick capture keys: Enter saves, Shift+Enter adds a line, Tab switches Task → Note → Event → Expense, Esc closes.</summary>
    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        var capture = ((ShellViewModel)DataContext).QuickCapture;
        switch (e.Key)
        {
            case Key.Escape:
                capture.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                var i = CaptureBox.CaretIndex;
                CaptureBox.Text = CaptureBox.Text.Insert(i, "\n");
                CaptureBox.CaretIndex = i + 1;
                e.Handled = true;
                break;
            case Key.Enter:
                capture.SaveCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab:
                var next = capture.Kind switch
                {
                    Arcadia.Core.Organizer.CaptureKind.Task => "Note",
                    Arcadia.Core.Organizer.CaptureKind.Note => "Event",
                    Arcadia.Core.Organizer.CaptureKind.Event => "Expense",
                    _ => "Task",
                };
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    next = capture.Kind switch { Arcadia.Core.Organizer.CaptureKind.Task => "Expense", Arcadia.Core.Organizer.CaptureKind.Expense => "Event", Arcadia.Core.Organizer.CaptureKind.Event => "Note", _ => "Task" };
                capture.SetKindCommand.Execute(next);
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: PaletteItem item }) _ = ((ShellViewModel)DataContext).Palette.ExecuteAsync(item);
    }

    /// <summary>
    /// Visual QA: lays the content out at an exact size (which may exceed the monitor) so it can be rendered
    /// with RenderTargetBitmap. The window itself stays on-screen sized.
    /// </summary>
    public void PrepareForCapture(double width, double height)
    {
        ShowActivated = false;
        RootBorder.Width = width;
        RootBorder.Height = height;
        // A Canvas never clips or constrains its children, so the root can be larger than the window.
        Content = null;
        var host = new System.Windows.Controls.Canvas();
        host.Children.Add(RootBorder);
        Content = host;
    }

    /// <summary>The element rendered by capture mode.</summary>
    public FrameworkElement CaptureRoot => RootBorder;

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Clamp(area.Width * 0.92, MinWidth, 1680);
        Height = Math.Clamp(area.Height * 0.92, MinHeight, 1000);
    }

    private void ApplyResponsiveLayout()
    {
        double width = RootBorder.ActualWidth, height = RootBorder.ActualHeight;
        IsCompact = width < CompactWidthThreshold;
        SidebarColumn.Width = new GridLength(IsCompact ? 76 : 214);
        SidebarFooter.Visibility = height >= FooterHeightThreshold && !IsCompact ? Visibility.Visible : Visibility.Collapsed;
        Motto.Visibility = width >= MottoWidthThreshold ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyWindowState()
    {
        var loc = LocalizationManager.Instance;
        if (WindowState == WindowState.Maximized)
        {
            // A maximized WindowChrome window overhangs the monitor by the resize frame; compensate.
            var frame = SystemParameters.WindowResizeBorderThickness;
            RootBorder.Margin = new Thickness(frame.Left + 1, frame.Top + 1, frame.Right + 1, frame.Bottom + 1);
            RootBorder.BorderThickness = new Thickness(0);
            MaximizeIcon.Kind = "WindowRestore";
            MaximizeButton.ToolTip = loc["Window_Restore"];
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, loc["Window_Restore"]);
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            MaximizeIcon.Kind = "WindowMaximize";
            MaximizeButton.ToolTip = loc["Window_Maximize"];
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, loc["Window_Maximize"]);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    // ── Native integration ───────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.ApplyDwmStyling(hwnd);
        ApplyWindowState();
        ApplyResponsiveLayout();
    }

    private static class Native
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Windows 11 rounded corners and a dark, Arcadia-tinted frame. Ignored on older Windows.</summary>
        public static void ApplyDwmStyling(IntPtr hwnd)
        {
            int dark = 1, corner = DWMWCP_ROUND, border = 0x00382A1C; // COLORREF 0x00BBGGRR → #1C2A38
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
    }
}
