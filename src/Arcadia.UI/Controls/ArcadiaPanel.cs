using System.Windows;
using System.Windows.Controls;

namespace Arcadia.UI.Controls;

/// <summary>Panel caption strip: icon, title and an optional trailing action (e.g. "View All").</summary>
public class ArcadiaSectionHeader : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ArcadiaSectionHeader));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ArcadiaSectionHeader));
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(ArcadiaSectionHeader));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public object? ActionContent { get => GetValue(ActionContentProperty); set => SetValue(ActionContentProperty, value); }

    static ArcadiaSectionHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaSectionHeader), new FrameworkPropertyMetadata(typeof(ArcadiaSectionHeader)));
        FocusableProperty.OverrideMetadata(typeof(ArcadiaSectionHeader), new FrameworkPropertyMetadata(false));
    }
}

/// <summary>
/// Framed console panel: dark bevelled body with a caption strip. Deliberately only one of several
/// surface treatments — stats, modules and artwork use their own framing.
/// </summary>
public class ArcadiaPanel : HeaderedContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ArcadiaPanel));
    public static readonly DependencyProperty HeaderActionProperty = DependencyProperty.Register(nameof(HeaderAction), typeof(object), typeof(ArcadiaPanel));

    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public object? HeaderAction { get => GetValue(HeaderActionProperty); set => SetValue(HeaderActionProperty, value); }

    static ArcadiaPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaPanel), new FrameworkPropertyMetadata(typeof(ArcadiaPanel)));
        FocusableProperty.OverrideMetadata(typeof(ArcadiaPanel), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Screen readers see each panel as a group named after its caption.</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PanelAutomationPeer(this);

    private sealed class PanelAutomationPeer(ArcadiaPanel owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ArcadiaPanel);
        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } explicitName ? explicitName : ((ArcadiaPanel)Owner).Header?.ToString() ?? string.Empty;
    }
}
