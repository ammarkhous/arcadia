using System.Windows;
using System.Windows.Controls;

namespace Arcadia.UI.Controls;

/// <summary>
/// <c>ui:Bidi.Text="{Binding Title}"</c> — for user-entered text (game titles, notes) whose direction is
/// independent of the UI language. The TextBlock takes the direction of its first strong character, so
/// "The Witcher 3: Wild Hunt" stays intact inside an Arabic layout (and Arabic notes read correctly in English).
/// HorizontalAlignment is interpreted in the parent's direction, so the text still hugs the reading-start edge.
/// </summary>
public static class Bidi
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Bidi), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = e.NewValue as string ?? string.Empty;
        tb.Text = text;
        tb.FlowDirection = IsLeftToRight(text) ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        if (tb.HorizontalAlignment == HorizontalAlignment.Stretch) tb.HorizontalAlignment = HorizontalAlignment.Left;
    }

    /// <summary>
    /// <c>ui:Bidi.AutoDirection="True"</c> on a TextBox: the box follows the direction of what is being typed
    /// (Arabic notes right-to-left, English/German left-to-right) regardless of the UI language.
    /// </summary>
    public static readonly DependencyProperty AutoDirectionProperty = DependencyProperty.RegisterAttached(
        "AutoDirection", typeof(bool), typeof(Bidi), new PropertyMetadata(false, OnAutoDirectionChanged));

    public static bool GetAutoDirection(DependencyObject d) => (bool)d.GetValue(AutoDirectionProperty);
    public static void SetAutoDirection(DependencyObject d, bool value) => d.SetValue(AutoDirectionProperty, value);

    private static void OnAutoDirectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.TextChanged -= OnBoxTextChanged;
        if (e.NewValue is true)
        {
            box.TextChanged += OnBoxTextChanged;
            Apply(box);
        }
    }

    private static void OnBoxTextChanged(object sender, TextChangedEventArgs e) => Apply((TextBox)sender);

    private static void Apply(TextBox box)
    {
        if (string.IsNullOrEmpty(box.Text))
        {
            box.ClearValue(FrameworkElement.FlowDirectionProperty); // empty: follow the UI
            return;
        }
        var direction = IsLeftToRight(box.Text) ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        if (box.FlowDirection != direction) box.FlowDirection = direction;
    }

    /// <summary>True when the first strongly-directional character is left-to-right (or there is none).</summary>
    public static bool IsLeftToRight(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return false;
            if (char.IsLetter(ch)) return true;
        }
        return true;
    }
}
