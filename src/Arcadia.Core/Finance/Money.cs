using System.Globalization;
using System.Text;

namespace Arcadia.Core.Finance;

/// <summary>
/// ISO 4217 currency codes. Arcadia never converts between currencies: every amount belongs to one currency and totals
/// are always kept per currency.
/// </summary>
public static class Currency
{
    /// <summary>Offered first in pickers; any other known ISO code can be typed.</summary>
    public static readonly IReadOnlyList<string> Common = ["EUR", "USD", "GBP", "CHF", "JPY", "SEK", "NOK", "DKK", "PLN", "CZK", "CAD", "AUD", "TRY", "AED", "SAR", "EGP", "JOD", "KWD", "QAR", "INR", "CNY"];

    // currencies whose minor unit is not 2 digits (ISO 4217)
    private static readonly Dictionary<string, int> Minor = new(StringComparer.Ordinal)
    {
        ["JPY"] = 0, ["KRW"] = 0, ["ISK"] = 0, ["CLP"] = 0, ["VND"] = 0, ["XOF"] = 0, ["XAF"] = 0, ["XPF"] = 0, ["UGX"] = 0, ["PYG"] = 0, ["RWF"] = 0, ["KMF"] = 0, ["GNF"] = 0, ["DJF"] = 0, ["VUV"] = 0,
        ["BHD"] = 3, ["KWD"] = 3, ["OMR"] = 3, ["JOD"] = 3, ["TND"] = 3, ["LYD"] = 3, ["IQD"] = 3,
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["EUR"] = "€", ["USD"] = "$", ["GBP"] = "£", ["JPY"] = "¥", ["INR"] = "₹", ["TRY"] = "₺", ["KRW"] = "₩", ["ILS"] = "₪", ["UAH"] = "₴", ["PHP"] = "₱", ["NGN"] = "₦",
        ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["HKD"] = "HK$", ["SGD"] = "S$", ["MXN"] = "MX$", ["BRL"] = "R$", ["CNY"] = "CN¥",
    };

    private static readonly Lazy<HashSet<string>> Known = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "XAU", "XAG", "XPT", "XPD", "XDR", "XCD", "XOF", "XAF", "XPF", "EUR", "USD" };
        foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var code = new RegionInfo(c.Name).ISOCurrencySymbol;
                if (code.Length == 3) set.Add(code.ToUpperInvariant());
            }
            catch (ArgumentException) { /* cultures without a region */ }
        }
        foreach (var code in Common) set.Add(code);
        foreach (var code in Minor.Keys) set.Add(code);
        return set;
    });

    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Three letters that name a currency Windows knows (all ISO 4217 codes in use).</summary>
    public static bool IsSupported(string? code)
    {
        var c = Normalize(code);
        return c.Length == 3 && c.All(ch => ch is >= 'A' and <= 'Z') && Known.Value.Contains(c);
    }

    public static IReadOnlyCollection<string> All => Known.Value;

    /// <summary>Digits after the decimal point for display (2 unless ISO says otherwise).</summary>
    public static int DecimalsOf(string code) => Minor.TryGetValue(Normalize(code), out var d) ? d : 2;

    /// <summary>"€", "$", "£"… for common currencies; otherwise the ISO code ("CHF", "SEK").</summary>
    public static string SymbolOf(string code)
    {
        var c = Normalize(code);
        return Symbols.TryGetValue(c, out var s) ? s : c;
    }

    /// <summary>
    /// Finds a currency in text: a symbol (€ $ £ ¥ ₹ ₺) or a known ISO code as a separate word. "$" means USD.
    /// Returns null when none (or more than one) is present.
    /// </summary>
    public static string? Detect(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in text)
        {
            var code = ch switch { '€' => "EUR", '$' => "USD", '£' => "GBP", '¥' => "JPY", '₹' => "INR", '₺' => "TRY", '₩' => "KRW", '₪' => "ILS", _ => null };
            if (code != null) found.Add(code);
        }
        foreach (var word in text.Split([' ', '\t', ',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Trim('.', ':').ToUpperInvariant();
            // a code glued to the amount: "35EUR", "EUR35"
            var letters = new string(w.Where(char.IsAsciiLetterUpper).ToArray());
            if (letters.Length == 3 && (letters == w || w.StartsWith(letters, StringComparison.Ordinal) || w.EndsWith(letters, StringComparison.Ordinal)) && Known.Value.Contains(letters))
                found.Add(letters);
        }
        return found.Count == 1 ? found.First() : null;
    }
}

/// <summary>How amounts are written: which character separates the decimals.</summary>
public enum DecimalStyle
{
    /// <summary>1,234.56</summary>
    Point,
    /// <summary>1.234,56</summary>
    Comma,
}

/// <summary>
/// Reads amounts typed by people or exported by banks. Never guesses: "1,234" is ambiguous (one thousand two hundred
/// thirty-four, or one point two three four) unless the decimal style is known, and is then rejected.
/// Accepts currency symbols and codes around the number, spaces, NBSP and apostrophes as thousands separators, a leading
/// or trailing minus (also U+2212) and accounting parentheses for negatives. At most four decimals (what Arcadia stores).
/// </summary>
public static class AmountParser
{
    /// <summary>Largest amount accepted for one value (a trillion). Totals are summed in decimal, which cannot overflow at this size.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    public const int MaxDecimals = 4;

    public enum Outcome { Ok, Empty, Invalid, Ambiguous, TooPrecise, TooLarge }

    /// <summary>Parses with a known style (null = decide from the text alone, rejecting ambiguous input).</summary>
    public static Outcome TryParse(string? text, DecimalStyle? style, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return Outcome.Empty;
        if (!Clean(text, out var digits, out var negative) || digits.Length == 0) return Outcome.Invalid;

        var styles = Possible(digits);
        if (styles == Styles.None) return Outcome.Invalid;
        DecimalStyle chosen;
        if (style is { } s)
        {
            if ((styles & (s == DecimalStyle.Point ? Styles.Point : Styles.Comma)) == 0) return Outcome.Invalid;
            chosen = s;
        }
        else if (styles == Styles.Both)
        {
            // both readings agree when there is no separator or it is unambiguous; otherwise refuse to guess
            if (digits.IndexOfAny(['.', ',']) >= 0) return Outcome.Ambiguous;
            chosen = DecimalStyle.Point;
        }
        else chosen = styles == Styles.Point ? DecimalStyle.Point : DecimalStyle.Comma;

        var dec = chosen == DecimalStyle.Point ? '.' : ',';
        var group = chosen == DecimalStyle.Point ? ',' : '.';
        var normalized = digits.Replace(group.ToString(), string.Empty, StringComparison.Ordinal).Replace(dec, '.');
        var point = normalized.IndexOf('.');
        if (point >= 0 && normalized.Length - point - 1 > MaxDecimals) return Outcome.TooPrecise;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return Outcome.Invalid;
        if (v > MaxAmount) return Outcome.TooLarge;
        value = negative ? -v : v;
        return Outcome.Ok;
    }

    /// <summary>
    /// The decimal style that reads every sample (for a CSV column). Null when no sample decides it (all could be
    /// either, e.g. only whole numbers or only "1,234") — the caller then asks the user.
    /// </summary>
    public static DecimalStyle? Detect(IEnumerable<string?> samples)
    {
        int point = 0, comma = 0;
        foreach (var text in samples)
        {
            if (string.IsNullOrWhiteSpace(text) || !Clean(text, out var digits, out _)) continue;
            var s = Possible(digits);
            if (s == Styles.Point) point++;
            else if (s == Styles.Comma) comma++;
        }
        if (point > 0 && comma == 0) return DecimalStyle.Point;
        if (comma > 0 && point == 0) return DecimalStyle.Comma;
        if (point == 0 && comma == 0) return null;
        return point >= comma ? DecimalStyle.Point : DecimalStyle.Comma; // mixed: the majority; rows that disagree become invalid
    }

    /// <summary>True when the samples contain a value only the chosen style can explain differently (the user must decide).</summary>
    public static bool NeedsDecision(IEnumerable<string?> samples)
    {
        foreach (var text in samples)
            if (!string.IsNullOrWhiteSpace(text) && Clean(text, out var digits, out _) && Possible(digits) == Styles.Both && digits.IndexOfAny(['.', ',']) >= 0) return true;
        return false;
    }

    [Flags]
    private enum Styles { None = 0, Point = 1, Comma = 2, Both = 3 }

    /// <summary>Which styles can read the separator pattern of <paramref name="s"/> (digits, '.' and ',' only).</summary>
    private static Styles Possible(string s)
    {
        int dots = s.Count(c => c == '.'), commas = s.Count(c => c == ',');
        if (dots == 0 && commas == 0) return Styles.Both;
        if (s[0] is '.' or ',' && s.Length > 1 && dots + commas == 1)
            return s[0] == '.' ? Styles.Point : Styles.Comma; // ".5" / ",5"
        if (dots > 0 && commas > 0)
        {
            var last = Math.Max(s.LastIndexOf('.'), s.LastIndexOf(','));
            var dec = s[last];
            var group = dec == '.' ? ',' : '.';
            if (s.Count(c => c == dec) != 1 || !GroupsValid(s[..last], group)) return Styles.None;
            return dec == '.' ? Styles.Point : Styles.Comma;
        }
        var sep = dots > 0 ? '.' : ',';
        var asGroup = GroupsValid(s, sep);
        var count = dots + commas;
        if (count > 1) return asGroup ? (sep == '.' ? Styles.Comma : Styles.Point) : Styles.None;
        // one separator: decimals, or thousands when exactly three digits follow and the integer part is a valid group head
        var at = s.IndexOf(sep);
        var after = s.Length - at - 1;
        var asDecimal = after > 0;
        var both = asDecimal && asGroup && after == 3;
        if (both) return Styles.Both;
        if (asDecimal) return sep == '.' ? Styles.Point : Styles.Comma;
        return Styles.None;
    }

    /// <summary>"1,234,567" style grouping: 1–3 digits, then groups of exactly three.</summary>
    private static bool GroupsValid(string s, char group)
    {
        var parts = s.Split(group);
        if (parts.Length == 1) return true;
        if (parts[0].Length is < 1 or > 3 || parts[0] == "0") return false;
        for (var i = 1; i < parts.Length; i++) if (parts[i].Length != 3) return false;
        return parts.All(p => p.All(char.IsAsciiDigit));
    }

    private static bool IsSpaceLike(char ch) => ch is ' ' or ' ' or ' ' or ' ' or '\'' or '’' or '\t';

    /// <summary>
    /// Strips currency marks, sign notation and thousands spacing ("1 234", "1'234"); leaves digits and '.'/','.
    /// The number must be one contiguous run: "12 EUR 5" or "1e5" are rejected.
    /// </summary>
    private static bool Clean(string text, out string digits, out bool negative)
    {
        negative = false;
        digits = string.Empty;
        var t = text.Trim();
        if (t.Length is 0 or > 64) return false;
        var parens = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { parens = true; t = t[1..^1]; }
        var sb = new StringBuilder(t.Length);
        int signs = 0;
        bool started = false, ended = false;
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            if (char.IsAsciiDigit(ch) || ch is '.' or ',')
            {
                if (ended) return false;
                sb.Append(ch);
                started = true;
            }
            else if (ch is '-' or '−' or '–' or '+')
            {
                if (ch != '+') negative = true;
                signs++;
                if (started) ended = true;
            }
            else if (IsSpaceLike(ch))
            {
                // a space between digits groups thousands; anywhere else it ends the number
                if (started && !ended && i + 1 < t.Length && char.IsAsciiDigit(t[i + 1]) && char.IsAsciiDigit(sb[^1])) sb.Append('_');
                else if (started) ended = true;
            }
            else if (char.IsLetter(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.CurrencySymbol || ch is '‏' or '‎' or '؜')
            {
                if (started) ended = true;
            }
            else return false;
        }
        if (signs > 1 || (parens && signs > 0)) return false;
        negative |= parens;
        var s = sb.ToString();
        if (s.Contains('_'))
        {
            var parts = s.Split('_');
            if (parts[0].Length is < 1 or > 3 || !parts[0].All(char.IsAsciiDigit)) return false;
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length < 3 || !p[..3].All(char.IsAsciiDigit) || (p.Length > 3 && (i < parts.Length - 1 || p[3] is not '.' and not ','))) return false;
            }
            s = s.Replace("_", string.Empty, StringComparison.Ordinal);
        }
        // a trailing separator ("12." or "12,") is not a number
        if (s.Length > 0 && s[^1] is '.' or ',') return false;
        digits = s;
        return true;
    }

}

/// <summary>
/// Formats money for display. Values are stored exactly; rounding happens here only, to the currency's minor unit
/// (half away from zero). The number style (separators, symbol position) comes from the chosen culture; the symbol from
/// the amount's own currency.
/// </summary>
public sealed class MoneyFormatter
{
    private readonly Dictionary<string, NumberFormatInfo> _formats = new(StringComparer.Ordinal);

    public MoneyFormatter(CultureInfo culture)
    {
        Culture = culture;
    }

    public CultureInfo Culture { get; }

    /// <summary>The style the culture writes decimals in (for reading typed amounts the same way).</summary>
    public DecimalStyle DecimalStyle => Culture.NumberFormat.NumberDecimalSeparator == "," ? DecimalStyle.Comma : DecimalStyle.Point;

    public static decimal Round(decimal amount, string currency) => Math.Round(amount, Currency.DecimalsOf(currency), MidpointRounding.AwayFromZero);

    /// <summary>"€1,250.00", "1.250,00 €", "-€12.00" (negative values keep their sign).</summary>
    public string Format(decimal amount, string currency) => Round(amount, currency).ToString("C", For(currency));

    /// <summary>With an explicit plus for positive values ("+€3,000.00"): income and balance changes.</summary>
    public string FormatSigned(decimal amount, string currency) => amount > 0 ? "+" + Format(amount, currency) : Format(amount, currency);

    /// <summary>The number without a currency ("1,250.00").</summary>
    public string FormatNumber(decimal amount, string currency) => Round(amount, currency).ToString("N" + Currency.DecimalsOf(currency), Culture);

    /// <summary>For editing: the exact value without grouping ("1250.5" / "1250,5").</summary>
    public string FormatInput(decimal amount) => amount.ToString("0.####", Culture);

    /// <summary>Short axis labels: "€1.2k", "€15k", "€1.5M".</summary>
    public string FormatCompact(decimal amount, string currency)
    {
        var abs = Math.Abs(amount);
        var nf = For(currency);
        string number = abs switch
        {
            >= 1_000_000m => (abs / 1_000_000m).ToString(abs >= 10_000_000m ? "0" : "0.#", Culture) + "M",
            >= 1_000m => (abs / 1_000m).ToString(abs >= 10_000m ? "0" : "0.#", Culture) + "k",
            _ => abs.ToString("0", Culture),
        };
        var symbol = nf.CurrencySymbol;
        var text = nf.CurrencyPositivePattern is 0 or 2 ? symbol + (nf.CurrencyPositivePattern == 2 ? " " : string.Empty) + number : number + (nf.CurrencyPositivePattern == 3 ? " " : string.Empty) + symbol;
        return amount < 0 ? "-" + text : text;
    }

    private NumberFormatInfo For(string currency)
    {
        var code = Currency.Normalize(currency);
        if (_formats.TryGetValue(code, out var nf)) return nf;
        nf = (NumberFormatInfo)Culture.NumberFormat.Clone();
        nf.CurrencySymbol = Currency.SymbolOf(code);
        nf.CurrencyDecimalDigits = Currency.DecimalsOf(code);
        // letter codes read better with a space ("CHF 12.00", "12,00 CHF")
        if (nf.CurrencySymbol.Length == 3 && nf.CurrencySymbol.All(char.IsAsciiLetterUpper))
        {
            if (nf.CurrencyPositivePattern == 0) nf.CurrencyPositivePattern = 2;
            if (nf.CurrencyPositivePattern == 1) nf.CurrencyPositivePattern = 3;
            nf.CurrencyNegativePattern = nf.CurrencyPositivePattern == 2 ? 12 : 8; // "-CHF 12.00" / "-12,00 CHF"
        }
        else if (nf.CurrencyNegativePattern == 0) nf.CurrencyNegativePattern = 1; // "-€12.00" rather than "(€12.00)"
        nf = NumberFormatInfo.ReadOnly(nf);
        _formats[code] = nf;
        return nf;
    }
}
