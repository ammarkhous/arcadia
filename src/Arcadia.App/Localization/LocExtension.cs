using System.Windows.Data;
using System.Windows.Markup;

namespace Arcadia.App.Localization;

/// <summary>
/// <c>Text="{l:Loc Nav_Home}"</c> — a live binding to a localized string that updates when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
