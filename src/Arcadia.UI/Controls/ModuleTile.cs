using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// Illustrated entry point to a module on the Home screen (IT Tools, Projects, ...).
/// </summary>
public class ModuleTile : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ModuleTile));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ModuleTile));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(ModuleTile));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ModuleTile));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(ModuleTile));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ModuleTile));
    public static readonly DependencyProperty SecondaryIconProperty = DependencyProperty.Register(nameof(SecondaryIcon), typeof(string), typeof(ModuleTile));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Color), typeof(ModuleTile), new PropertyMetadata(Tones.Blue));
    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(nameof(Seed), typeof(int), typeof(ModuleTile), new PropertyMetadata(1));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? SecondaryIcon { get => (string?)GetValue(SecondaryIconProperty); set => SetValue(SecondaryIconProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }

    static ModuleTile()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ModuleTile), new FrameworkPropertyMetadata(typeof(ModuleTile)));
        FocusableProperty.OverrideMetadata(typeof(ModuleTile), new FrameworkPropertyMetadata(false));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (e.Handled) return;
        // The whole illustrated tile is a click target; the inner button remains the keyboard target.
        if (Command?.CanExecute(CommandParameter) == true)
        {
            Command.Execute(CommandParameter);
            e.Handled = true;
        }
    }
}
