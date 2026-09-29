using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Arcadia.App.ViewModels.Oracle;

namespace Arcadia.App.Views.Oracle;

public partial class OracleView : UserControl
{
    private OracleConsoleViewModel? _vm;
    private readonly DispatcherTimer _park;

    public OracleView()
    {
        InitializeComponent();
        // A focused text box keeps WPF's caret animation (and so the render loop) running. After a quiet spell the prompt
        // hands focus to the page; the next key goes straight back into the prompt, so nothing typed is lost.
        _park = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _park.Tick += (_, _) =>
        {
            _park.Stop();
            if (InputBox.IsKeyboardFocused && InputBox.Text.Length == 0) Keyboard.Focus(this);
        };
        Focusable = true;
        FocusVisualStyle = null;
        InputBox.GotKeyboardFocus += (_, _) => _park.Start();
        InputBox.LostKeyboardFocus += (_, _) => _park.Stop();
        InputBox.PreviewKeyDown += (_, _) => { _park.Stop(); _park.Start(); };
        PreviewTextInput += (_, e) =>
        {
            if (!ReferenceEquals(Keyboard.FocusedElement, this)) return;
            FocusInput();
            InputBox.Text += e.Text;
            InputBox.CaretIndex = InputBox.Text.Length;
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (ReferenceEquals(Keyboard.FocusedElement, this) && e.Key is Key.Up or Key.Down or Key.Enter or Key.Back) { FocusInput(); if (e.Key != Key.Back) OnInputKey(InputBox, e); }
        };
        DataContextChanged += (_, _) => Attach(DataContext as OracleConsoleViewModel);
        Loaded += (_, _) =>
        {
            Attach(DataContext as OracleConsoleViewModel); // re-attach after the page was left (Unloaded detaches)
            Dispatcher.BeginInvoke(() => { TranscriptScroll.ScrollToEnd(); FocusInput(); }, DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) => { _park.Stop(); Attach(null); };
    }

    private void Attach(OracleConsoleViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.Transcript.CollectionChanged -= OnTranscriptChanged;
            _vm.FocusRequested -= OnFocusRequested;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.Transcript.CollectionChanged += OnTranscriptChanged;
            _vm.FocusRequested += OnFocusRequested;
        }
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        if (IsVisible && _vm is { IsOverlayOpen: false }) Dispatcher.BeginInvoke(FocusInput, DispatcherPriority.Loaded);
    }

    private void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    /// <summary>Enter runs, Up/Down walk the history, Tab jumps into the latest result, Escape clears.</summary>
    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryUpCommand.Execute(null);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryDownCommand.Execute(null);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Input = string.Empty;
                e.Handled = true;
                break;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = FocusLatestResult(TranscriptList);
                break;
        }
    }

    /// <summary>Moves keyboard focus to the first row or button of the newest result.</summary>
    internal static bool FocusLatestResult(ItemsControl list)
    {
        if (list.Items.Count == 0) return false;
        if (list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is not FrameworkElement container) return false;
        return container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
