using System.Windows;
using System.Windows.Controls;

namespace Arcadia.App.Dialogs;

public partial class FindOnlineView : UserControl
{
    public FindOnlineView() => InitializeComponent();
}

/// <summary>Thumbnail frame sizes that respect each art kind's proportions.</summary>
public static class ArtSizes
{
    public static readonly Size BoxArt = new(120, 180);
    public static readonly Size Capsule = new(230, 108);
    public static readonly Size Hero = new(300, 97);
    public static readonly Size Logo = new(230, 100);
}
