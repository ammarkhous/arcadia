using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arcadia.UI.Controls;

/// <summary>ContentControl that fades new content in with a short upward settle.</summary>
public class TransitioningContentControl : ContentControl
{
    static TransitioningContentControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TransitioningContentControl), new FrameworkPropertyMetadata(typeof(TransitioningContentControl)));
        FocusableProperty.OverrideMetadata(typeof(TransitioningContentControl), new FrameworkPropertyMetadata(false));
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (!Motion.AnimationsEnabled || oldContent == null) return;

        var translate = new TranslateTransform(0, 10);
        RenderTransform = translate;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Slow) { EasingFunction = Motion.EaseOut });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Motion.Slow) { EasingFunction = Motion.EaseOut });
    }
}
