using System.Globalization;
using System.Text;

namespace Arcadia.Core.Text;

/// <summary>
/// Folds text for forgiving matching: lower case, no accents ("Zürich" = "zurich"), "ß" = "ss", and the common
/// Arabic spelling variants (hamza forms of alef, tashkeel, final yā/alef maqsūra, tā marbūta).
/// </summary>
public static class TextFold
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            switch (ch)
            {
                case 'ß': sb.Append("ss"); break;
                case 'أ' or 'إ' or 'آ' or 'ٱ': sb.Append('ا'); break;
                case 'ى': sb.Append('ي'); break;
                case 'ة': sb.Append('ه'); break;
                case 'ـ': break; // tatweel
                case '’' or '\'' or '`': break;
                case '-' or '‐' or '–': sb.Append(' '); break;
                default: sb.Append(char.ToLowerInvariant(ch)); break;
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }
}
