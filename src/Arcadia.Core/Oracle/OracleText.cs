using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arcadia.Core.Oracle;

public enum OracleTokenKind { Word, Number, Money, Ip, Host, Date, Time, Percent }

/// <summary>A token: the original text (never altered) and its folded form used for matching.</summary>
public sealed record OracleToken(string Text, string Norm, OracleTokenKind Kind, int Index)
{
    public override string ToString() => Text;
}

/// <summary>Normalised input: tokens plus an exact payload (quoted text or everything after a colon).</summary>
public sealed record OracleInput(string Original, string Command, IReadOnlyList<OracleToken> Tokens, string? Payload)
{
    public string Normalized => string.Join(' ', Tokens.Select(t => t.Norm)) + (Payload != null ? " : «" + Payload + "»" : string.Empty);
}

/// <summary>
/// Stage 1–2 of the pipeline. Normalises safely (case, spacing, punctuation, Unicode variants) for matching only:
/// the original words, quoted text, paths, URLs, amounts, dates and names are kept as typed.
/// </summary>
public static partial class OracleText
{
    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")] private static partial Regex Ipv4();
    [GeneratedRegex(@"^(?=.*[a-z])[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase)] private static partial Regex HostName();
    [GeneratedRegex(@"^(\d{4}-\d{1,2}-\d{1,2}|\d{1,2}\.\d{1,2}\.(\d{2,4})?|\d{1,2}/\d{1,2}(/\d{2,4})?)$")] private static partial Regex DateToken();
    [GeneratedRegex(@"^([01]?\d|2[0-3])[:.][0-5]\d$|^(1[0-2]|0?[1-9])(am|pm)$", RegexOptions.IgnoreCase)] private static partial Regex TimeToken();
    [GeneratedRegex(@"^[€$£¥]?\d[\d.,']*([€$£¥]|eur|usd|gbp|chf)?$", RegexOptions.IgnoreCase)] private static partial Regex MoneyLike();

    private static readonly char[] Trim = [',', ';', '!', '?', '(', ')', '[', ']', '{', '}', '،', '؟', '"', '\'', '.'];

    public static OracleInput Parse(string? text)
    {
        var original = text ?? string.Empty;
        var s = Clean(original);
        string? payload = null;

        // an exact payload: quoted text…
        var q = FirstQuoted(s);
        if (q is { } quoted)
        {
            payload = quoted.Content;
            s = (s[..quoted.Start] + " " + s[(quoted.End + 1)..]).Trim();
        }
        // …or everything after a colon that follows a word ("add note: finish Panam missions")
        else if (ColonIndex(s) is { } colon)
        {
            payload = s[(colon + 1)..].Trim();
            s = s[..colon].Trim();
            if (payload.Length == 0) payload = null;
        }

        var tokens = new List<OracleToken>();
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim(Trim);
            if (raw.EndsWith('.') && DateToken().IsMatch(raw)) word = raw; // "12.10."
            if (word.Length == 0) continue;
            // host:port → two tokens
            if (word.Count(c => c == ':') == 1 && word.IndexOf(':') is var i && i > 0 && int.TryParse(word[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && (Ipv4().IsMatch(word[..i]) || HostName().IsMatch(word[..i]) || word[..i].Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                tokens.Add(Make(word[..i], tokens.Count));
                tokens.Add(new OracleToken(word[(i + 1)..], word[(i + 1)..], OracleTokenKind.Number, tokens.Count));
                continue;
            }
            tokens.Add(Make(word, tokens.Count));
        }
        return new OracleInput(original, s, tokens, payload);
    }

    private static OracleToken Make(string word, int index)
    {
        OracleTokenKind kind;
        if (Ipv4().IsMatch(word)) kind = OracleTokenKind.Ip;
        else if (DateToken().IsMatch(word)) kind = OracleTokenKind.Date;
        else if (TimeToken().IsMatch(word)) kind = OracleTokenKind.Time;
        else if (word.EndsWith('%') && decimal.TryParse(word[..^1], NumberStyles.Number, CultureInfo.InvariantCulture, out _)) kind = OracleTokenKind.Percent;
        else if (word.All(char.IsAsciiDigit)) kind = OracleTokenKind.Number;
        else if (MoneyLike().IsMatch(word) && word.Any(char.IsAsciiDigit) && (word.Any(c => "€$£¥".Contains(c)) || char.IsLetter(word[^1]))) kind = OracleTokenKind.Money;
        else if (word.Any(char.IsAsciiDigit) && word.All(c => char.IsAsciiDigit(c) || c is '.' or ',')) kind = OracleTokenKind.Number;
        else if (HostName().IsMatch(word) && word.Contains('.')) kind = OracleTokenKind.Host;
        else kind = OracleTokenKind.Word;
        return new OracleToken(word, kind == OracleTokenKind.Word ? Fold(word) : word.ToLowerInvariant(), kind, index);
    }

    /// <summary>Unicode variants, typographic quotes, Arabic punctuation, zero-width characters, repeated spaces.</summary>
    public static string Clean(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\uFEFF' or '\u0640': continue; // zero-width, direction marks, tatweel
                case '“' or '”' or '„' or '‟': sb.Append('"'); break;
                case '‘' or '’' or '‚': sb.Append('\''); break;
                case '؟': sb.Append('?'); break;
                case '،': sb.Append(','); break;
                case '\t' or '\r' or '\n' or '\u00A0': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static (int Start, int End, string Content)? FirstQuoted(string s)
    {
        foreach (var (open, close) in new[] { ('"', '"'), ('«', '»'), ('\'', '\'') })
        {
            var a = s.IndexOf(open);
            if (a < 0) continue;
            // an apostrophe inside a word ("what's", "Mum's") is not a quote
            if (open == '\'' && a > 0 && char.IsLetter(s[a - 1])) continue;
            var b = s.IndexOf(close, a + 1);
            if (b > a + 1) return (a, b, s[(a + 1)..b]);
        }
        return null;
    }

    /// <summary>A colon that introduces content: after a letter, not in a time (9:30), URL (http://) or host:port.</summary>
    private static int? ColonIndex(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] != ':') continue;
            if (!char.IsLetter(s[i - 1]) && s[i - 1] != ' ') continue;
            if (i + 1 < s.Length && s[i + 1] == '/') continue;
            if (i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) continue;
            // "C:\…" drive letters
            if (i + 1 < s.Length && s[i + 1] == '\\') continue;
            return i;
        }
        return null;
    }

    /// <summary>
    /// The matching form of a word: lower case, Latin accents and German umlauts removed (ä→a, ß→ss), Arabic
    /// diacritics removed and letter variants unified (أ إ آ → ا, ى → ي, ة → ه).
    /// </summary>
    public static string Fold(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var ch in word.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // Latin accents, Arabic harakat
            switch (ch)
            {
                case 'ß': sb.Append("ss"); break;
                case 'أ' or 'إ' or 'آ' or 'ٱ': sb.Append('ا'); break;
                case 'ى': sb.Append('ي'); break;
                case 'ة': sb.Append('ه'); break;
                case 'ؤ': sb.Append('و'); break;
                case 'ئ': sb.Append('ي'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string FoldPhrase(string phrase) => string.Join(' ', Clean(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Fold));

    public static bool IsArabic(string text) => text.Any(c => c is >= '\u0600' and <= '\u06FF');
}

/// <summary>Conservative typo tolerance for names ("cyberpank" → Cyberpunk 2077). Scores 0–1.</summary>
public static class OracleFuzzy
{
    /// <summary>At or above: a strong match (may be acted on when it is the only one).</summary>
    public const double Strong = 0.85;
    /// <summary>At or above: plausible (offered as a choice, never acted on alone for changes).</summary>
    public const double Plausible = 0.6;

    public static double Similarity(string query, string candidate)
    {
        var q = OracleText.FoldPhrase(query);
        var c = OracleText.FoldPhrase(candidate);
        if (q.Length == 0 || c.Length == 0) return 0;
        if (q == c) return 1.0;
        if (c.StartsWith(q + " ", StringComparison.Ordinal) || c.StartsWith(q, StringComparison.Ordinal) && q.Length >= 4) return 0.95;
        if ((" " + c + " ").Contains(" " + q + " ", StringComparison.Ordinal)) return 0.9;
        var qw = q.Split(' ');
        var cw = c.Split(' ');
        double total = 0;
        foreach (var w in qw)
        {
            var best = 0.0;
            foreach (var x in cw)
            {
                double s;
                if (x == w) s = 1;
                else if (w.Length >= 3 && x.StartsWith(w, StringComparison.Ordinal)) s = 0.9;
                else
                {
                    var d = Levenshtein(w, x, 3);
                    var allowed = Math.Max(1, Math.Min(w.Length, x.Length) / 4);
                    s = d <= allowed && Math.Min(w.Length, x.Length) >= 4 ? 1.0 - 0.1 * d - 0.05 : 0;
                }
                best = Math.Max(best, s);
            }
            total += best;
        }
        var score = total / qw.Length;
        // a short query matching one word of a long name is weaker
        if (qw.Length == 1 && cw.Length > 2) score *= 0.95;
        return Math.Round(score, 3);
    }

    /// <summary>Edit distance, giving up above <paramref name="max"/> (returns max + 1).</summary>
    public static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
