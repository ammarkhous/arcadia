using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.App.Converters;

/// <summary>Maps a tone name from a view model ("Green", "Amber" …) to the palette colour.</summary>
public sealed class ToneToColorConverter : IValueConverter
{
    public static Color Resolve(string? tone) => tone switch
    {
        "Green" => Tones.Green,
        "Amber" => Tones.Amber,
        "Purple" => Tones.Purple,
        "Red" => Tones.Red,
        "Cyan" => Tones.Cyan,
        "Gold" => Color.FromRgb(0xF4, 0xC4, 0x51),
        "Steel" => Tones.Steel,
        _ => Tones.Blue,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = Resolve(value as string);
        return targetType == typeof(Brush) ? Tones.Frozen(new SolidColorBrush(color)) : color;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is non-null / non-empty. Pass "invert" to flip.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}


/// <summary>Visible when every bound value is true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>0..1 fraction → 0..100 for progress bars.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d ? d * 100 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value == null) != (parameter as string == "invert");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"12 Sep 2026 14:05 · 3 results · 1 note" for a diagnostic session row.</summary>
public sealed class SessionMetaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Arcadia.Core.ItTools.Diagnostics.DiagnosticSessionSummary s) return string.Empty;
        var loc = Arcadia.App.Localization.LocalizationManager.Instance;
        var when = DateTime.SpecifyKind(s.UpdatedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("g", loc.Culture);
        var text = loc.Format("Diag_Meta", when, s.ResultCount, s.NoteCount);
        if (!string.IsNullOrWhiteSpace(s.Reference)) text = s.Reference + " · " + text;
        return s.ClosedAtUtc != null ? text + " · " + loc["Diag_ClosedShort"] : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when both bound values are equal (by string form) — drives option chips bound to a selected value.</summary>
public sealed class SameValueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && string.Equals(System.Convert.ToString(values[0], CultureInfo.InvariantCulture), System.Convert.ToString(values[1], CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [Binding.DoNothing, Binding.DoNothing];
}

/// <summary>Tone name → deep tinted slab brush (result plates).</summary>
public sealed class ToneToSlabConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tones.Slab(ToneToColorConverter.Resolve(value as string));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Localized label via a resource key pattern: ConverterParameter "Prefix_{0}" or a fixed map.</summary>
public sealed class LocKeyConverter : IValueConverter
{
    public string Pattern { get; set; } = "{0}";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = string.Format(CultureInfo.InvariantCulture, Pattern, value);
        var text = Arcadia.App.Localization.LocalizationManager.Instance[key];
        return text.StartsWith('[') ? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>100 → "100 MB", 1024 → "1 GB".</summary>
public sealed class MegabytesLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long mb ? mb >= 1024 && mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB" : string.Empty;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>(preset, selected, useCustom) → checked.</summary>
public sealed class PresetCheckedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 3 && values[2] is false && Equals(values[0], values[1]);
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

public sealed class HashNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Arcadia.Core.ItTools.Files.HashAlgorithmKind k ? Arcadia.App.ViewModels.ItTools.HashToolViewModel.Name(k) : string.Empty;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
/// <summary>Enum value ⇄ bool (radio tabs) or Visibility (panels): ConverterParameter is the enum member name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var same = value != null && string.Equals(value.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);
        return targetType == typeof(Visibility) ? (same ? Visibility.Visible : Visibility.Collapsed) : same;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && targetType.IsEnum ? Enum.Parse(targetType, name, true) : Binding.DoNothing;
}
