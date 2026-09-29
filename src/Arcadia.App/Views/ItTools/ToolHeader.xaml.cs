using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Arcadia.App.Views.ItTools;

/// <summary>Header shared by every tool. The busy scanner only animates while it is visible.</summary>
public partial class ToolHeader : UserControl
{
    public ToolHeader()
    {
        InitializeComponent();
        Scanner.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && Arcadia.UI.Motion.AnimationsEnabled)
            {
                var sweep = new DoubleAnimation(-160, Math.Max(200, BusyTrack.ActualWidth), TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
                ScanShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, sweep);
            }
            else
            {
                ScanShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            }
        };
    }
}
