using System.Globalization;

namespace Arcadia.Core.Localization;

/// <summary>Resolves localized strings for view models and services.</summary>
public interface ILocalizer
{
    CultureInfo Culture { get; }
    bool IsRightToLeft { get; }
    string this[string key] { get; }
    string Format(string key, params object[] args);
    event EventHandler? CultureChanged;
}
