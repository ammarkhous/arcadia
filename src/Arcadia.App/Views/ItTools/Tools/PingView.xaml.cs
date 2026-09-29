using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using Arcadia.App.ViewModels.ItTools;

namespace Arcadia.App.Views.ItTools.Tools;

/// <summary>Keeps the console scrolled to the newest reply.</summary>
public partial class PingView : UserControl
{
    public PingView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PingToolViewModel old) old.Output.CollectionChanged -= OnOutput;
            if (e.NewValue is PingToolViewModel vm) vm.Output.CollectionChanged += OnOutput;
        };
    }

    private void OnOutput(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ConsoleScroll.ScrollToEnd, DispatcherPriority.Background);
}
