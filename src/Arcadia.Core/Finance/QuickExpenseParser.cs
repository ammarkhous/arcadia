using System.Text.RegularExpressions;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Finance;

/// <summary>What "€35 groceries" means: an amount, maybe a currency, the words, maybe a date.</summary>
/// <param name="IsClear">False when the text does not follow one of the clear patterns; the caller then opens the editor.</param>
public sealed record QuickExpense(bool IsClear, decimal? Amount, string? Currency, string Description, DateOnly? Date, bool DateAmbiguous);

/// <summary>
/// Deterministic quick expense capture. Only clear patterns are accepted: one amount at the start or the end of the text
/// (optionally with a currency symbol or ISO code) and a description: "35 groceries", "35.50 groceries",
/// "€35 groceries", "groceries 12,90 €", "12 EUR Kino gestern". A date word in the description is read by
/// <see cref="QuickDateParser"/>. Anything else (several numbers, no description) is not guessed at; "1,234" follows the
/// user's number style.
/// </summary>
public static partial class QuickExpenseParser
{
    // amount token: optional symbol/code, digits with at most one decimal separator (1–2 decimals) or grouped thousands
    [GeneratedRegex(@"^(?<cur1>[€$£¥₹₺]|[A-Za-z]{3}(?=\s*\d))?\s*(?<num>\d{1,3}(?:[.,' ]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)\s*(?<cur2>[€$£¥₹₺]|[A-Za-z]{3}\b)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountToken();

    public static QuickExpense Parse(string? text, DateTime now, DecimalStyle style)
    {
        var raw = (text ?? string.Empty).Trim();
        var unclear = new QuickExpense(false, null, null, raw, null, false);
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count < 2) return unclear;

        // the amount first (1–2 tokens: "€ 35" / "35 €") at the start, then at the end; "12.30" must stay an amount,
        // so date words are only looked for in the remaining description
        foreach (var fromStart in new[] { true, false })
        {
            for (var take = 2; take >= 1; take--)
            {
                if (words.Count <= take) continue;
                var tokens = fromStart ? words.Take(take) : words.Skip(words.Count - take);
                var m = AmountToken().Match(string.Join(' ', tokens));
                if (!m.Success) continue;
                var code = m.Groups["cur1"].Success ? m.Groups["cur1"].Value : m.Groups["cur2"].Success ? m.Groups["cur2"].Value : null;
                string? currency = null;
                if (code != null)
                {
                    currency = Arcadia.Core.Finance.Currency.Detect(code);
                    if (currency == null) continue; // "abc 12" is not a currency
                }
                var rest = string.Join(' ', fromStart ? words.Skip(take) : words.Take(words.Count - take)).Trim();
                if (rest.Length == 0 || ContainsNumber(rest)) return unclear; // a second number: not a clear pattern
                var outcome = AmountParser.TryParse(m.Groups["num"].Value, Resolve(m.Groups["num"].Value, style), out var amount);
                if (outcome != AmountParser.Outcome.Ok || amount <= 0) return unclear;
                var dated = QuickDateParser.Parse(rest, now);
                var description = dated.Date != null && dated.Title.Length > 0 ? dated.Title : rest;
                return new QuickExpense(true, amount, currency, description, dated.Title.Length > 0 ? dated.Date : null, dated.Ambiguous);
            }
        }
        return unclear;
    }

    /// <summary>"35,5" and "12.90" are clear either way; only "1,234"-style numbers depend on the user's style.</summary>
    private static DecimalStyle? Resolve(string number, DecimalStyle style)
    {
        var seps = number.Count(c => c is '.' or ',');
        if (seps == 1)
        {
            var at = number.IndexOfAny(['.', ',']);
            var after = number.Length - at - 1;
            if (after != 3) return number[at] == '.' ? DecimalStyle.Point : DecimalStyle.Comma;
        }
        return style;
    }

    private static bool ContainsNumber(string s) => s.Any(char.IsDigit);
}
