using System.Globalization;
using System.Text;

namespace Arcadia.Core.Finance;

/// <summary>RFC 4180 CSV reading and writing (quotes, embedded separators and line breaks), with delimiter and encoding detection.</summary>
public static class CsvText
{
    public static readonly char[] Delimiters = [',', ';', '\t', '|'];

    /// <summary>Largest file the import wizard reads (bank exports are far smaller).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    static CsvText()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* Latin-1 remains as fallback */ }
    }

    /// <summary>
    /// Text from file bytes: a BOM decides (UTF-8/UTF-16); otherwise valid UTF-8 is UTF-8, and anything else is read as
    /// Windows-1252 (older German/English bank exports). Reports which encoding was used.
    /// </summary>
    public static string Decode(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encodingName = "UTF-8"; return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3); }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encodingName = "UTF-16 LE"; return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encodingName = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            encodingName = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            Encoding legacy;
            try { legacy = Encoding.GetEncoding(1252); encodingName = "Windows-1252"; }
            catch { legacy = Encoding.Latin1; encodingName = "ISO-8859-1"; }
            return legacy.GetString(bytes);
        }
    }

    /// <summary>The delimiter that splits the first lines into the same number (&gt; 1) of fields; comma when unsure.</summary>
    public static char DetectDelimiter(string text)
    {
        var sample = string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).Take(12));
        var best = ',';
        var bestScore = -1;
        foreach (var d in Delimiters)
        {
            var rows = Parse(sample, d);
            if (rows.Count == 0) continue;
            var counts = rows.Select(r => r.Length).ToList();
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            if (mode.Key < 2) continue;
            var score = mode.Count() * 100 + mode.Key; // consistency first, then field count
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    /// <summary>Splits CSV text into rows of fields. Blank lines are skipped; a quote inside a quoted field is doubled.</summary>
    public static List<string[]> Parse(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && !fieldStarted) { inQuotes = true; fieldStarted = true; }
            else if (ch == delimiter) { fields.Add(field.ToString()); field.Clear(); fieldStarted = false; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                if (fields.Count > 1 || fields[0].Trim().Length > 0) rows.Add([.. fields]);
                fields.Clear();
            }
            else { field.Append(ch); fieldStarted = true; }
        }
        fields.Add(field.ToString());
        if (fields.Count > 1 || fields[0].Trim().Length > 0) rows.Add([.. fields]);
        return rows;
    }

    /// <summary>One CSV line (comma-separated, quoted where needed, spreadsheet formulas neutralised).</summary>
    public static string Line(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));

    /// <summary>
    /// Quotes a cell when needed. Text starting with = + - @ would run as a formula in spreadsheet programs, so such cells
    /// get a leading apostrophe (the importer removes it again).
    /// </summary>
    public static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    /// <summary>Undoes <see cref="Cell"/>'s formula guard.</summary>
    public static string Unguard(string value) =>
        value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
}

/// <summary>What a CSV column means to the importer.</summary>
public enum ImportField { None, Date, Time, Amount, Debit, Credit, Type, Currency, Description, Payee, Category, Notes, Tags, Account, ToAccount, ToAmount }

/// <summary>How the sign of a single amount column is read (banks differ; credit-card exports often show purchases as positive).</summary>
public enum SignConvention { NegativeIsExpense, PositiveIsExpense }

public sealed record ImportMapping(
    IReadOnlyDictionary<ImportField, int> Columns,
    string DateFormat,
    DecimalStyle Style,
    SignConvention Sign,
    int AccountId,
    bool HasHeader);

/// <summary>What the parser needs to know about the library (accounts and categories by name).</summary>
public sealed record ImportContext(
    IReadOnlyDictionary<int, string> AccountCurrencies,
    Func<string, int?> AccountByName,
    Func<string, int?> CategoryByName);

/// <summary>One CSV line after parsing: a draft, or the reason (a resource key) it cannot be imported.</summary>
public sealed record ParsedImportRow(int Line, string[] Cells, TransactionDraft? Draft, string? ErrorKey)
{
    public bool IsValid => Draft != null;
}

/// <summary>
/// Turns mapped CSV rows into transaction drafts. Nothing is guessed: dates use the chosen format, amounts the chosen
/// decimal style, and every row that does not fit is reported (never altered).
/// </summary>
public static class CsvImport
{
    public static readonly IReadOnlyList<string> DateFormats =
    [
        "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy", "dd-MM-yyyy", "MM-dd-yyyy",
        "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm",
    ];

    /// <summary>
    /// The formats that read the most non-empty samples (in the order above); a few broken rows (e.g. "31.09.") do not
    /// hide the file's format — they are reported as invalid later. More than one result means the user must choose.
    /// </summary>
    public static IReadOnlyList<string> DetectDateFormats(IEnumerable<string?> samples)
    {
        var values = samples.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Take(200).ToList();
        if (values.Count == 0) return [];
        var counts = DateFormats.Select(f => (Format: f, Count: values.Count(v => TryDate(v, f, out _)))).ToList();
        var best = counts.Max(c => c.Count);
        // a format has to read most of the column to count as detected
        if (best == 0 || best * 2 < values.Count) return [];
        return counts.Where(c => c.Count == best).Select(c => c.Format).ToList();
    }

    public static bool TryDate(string text, string format, out DateOnly date)
    {
        var t = text.Trim();
        if (DateTime.TryParseExact(t, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)
            || (format.Contains("MMM", StringComparison.Ordinal) && DateTime.TryParseExact(t, format, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.AllowWhiteSpaces, out dt)))
        {
            date = DateOnly.FromDateTime(dt);
            return date.Year is >= 1900 and <= 2200;
        }
        date = default;
        return false;
    }

    private static readonly (ImportField Field, string[] Names)[] HeaderNames =
    [
        (ImportField.Date, ["date", "datum", "buchungstag", "buchungsdatum", "valuta", "wertstellung", "transaction date", "booking date", "value date", "التاريخ", "تاريخ"]),
        (ImportField.Time, ["time", "uhrzeit", "الوقت"]),
        (ImportField.Amount, ["amount", "betrag", "umsatz", "betrag (eur)", "value", "sum", "المبلغ", "القيمة"]),
        (ImportField.Debit, ["debit", "soll", "withdrawal", "withdrawals", "paid out", "ausgang", "مدين", "سحب"]),
        (ImportField.Credit, ["credit", "haben", "deposit", "deposits", "paid in", "eingang", "دائن", "إيداع"]),
        (ImportField.Type, ["type", "typ", "art", "النوع"]),
        (ImportField.Currency, ["currency", "währung", "waehrung", "العملة"]),
        (ImportField.Description, ["description", "verwendungszweck", "buchungstext", "memo", "details", "reference", "text", "الوصف", "البيان"]),
        (ImportField.Payee, ["payee", "merchant", "empfänger", "empfaenger", "auftraggeber", "beguenstigter/zahlungspflichtiger", "begünstigter/zahlungspflichtiger", "name", "counterparty", "المستفيد", "التاجر"]),
        (ImportField.Category, ["category", "kategorie", "الفئة", "التصنيف"]),
        (ImportField.Notes, ["notes", "note", "notiz", "notizen", "ملاحظات"]),
        (ImportField.Tags, ["tags", "schlagwörter", "الوسوم"]),
        (ImportField.Account, ["account", "konto", "الحساب"]),
        (ImportField.ToAccount, ["to account", "zielkonto", "إلى الحساب"]),
        (ImportField.ToAmount, ["to amount", "zielbetrag", "المبلغ المستلم"]),
    ];

    /// <summary>Suggests a mapping from header names (English, German, Arabic and Arcadia's own export). Each field is used once.</summary>
    public static Dictionary<ImportField, int> GuessColumns(IReadOnlyList<string> header)
    {
        var map = new Dictionary<ImportField, int>();
        var used = new HashSet<int>();
        foreach (var (field, names) in HeaderNames)
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (used.Contains(i)) continue;
                var h = header[i].Trim().Trim('"').ToLowerInvariant();
                if (names.Contains(h))
                {
                    map[field] = i;
                    used.Add(i);
                    break;
                }
            }
        }
        // debit/credit columns replace a single amount only when there is no amount column
        if (map.ContainsKey(ImportField.Amount)) { map.Remove(ImportField.Debit); map.Remove(ImportField.Credit); }
        return map;
    }

    private static readonly Dictionary<string, TransactionType> TypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["expense"] = TransactionType.Expense, ["income"] = TransactionType.Income, ["transfer"] = TransactionType.Transfer,
        ["ausgabe"] = TransactionType.Expense, ["einnahme"] = TransactionType.Income, ["umbuchung"] = TransactionType.Transfer, ["überweisung"] = TransactionType.Transfer,
        ["مصروف"] = TransactionType.Expense, ["دخل"] = TransactionType.Income, ["تحويل"] = TransactionType.Transfer,
        ["debit"] = TransactionType.Expense, ["credit"] = TransactionType.Income,
    };

    public static IReadOnlyList<ParsedImportRow> Parse(IReadOnlyList<string[]> rows, ImportMapping mapping, ImportContext context)
    {
        var result = new List<ParsedImportRow>(rows.Count);
        for (var i = mapping.HasHeader ? 1 : 0; i < rows.Count; i++)
            result.Add(ParseRow(i + 1, rows[i], mapping, context));
        return result;
    }

    private static string? Get(string[] cells, IReadOnlyDictionary<ImportField, int> map, ImportField field)
    {
        if (!map.TryGetValue(field, out var i) || i < 0 || i >= cells.Length) return null;
        var v = CsvText.Unguard(cells[i].Trim());
        return v.Length == 0 ? null : v;
    }

    private static string? AmountError(AmountParser.Outcome o) => o switch
    {
        AmountParser.Outcome.Ok => null,
        AmountParser.Outcome.Ambiguous => "Imp_ErrAmbiguous",
        AmountParser.Outcome.TooPrecise => "Imp_ErrPrecision",
        AmountParser.Outcome.TooLarge => "Imp_ErrTooLarge",
        _ => "Imp_ErrAmount",
    };

    public static ParsedImportRow ParseRow(int line, string[] cells, ImportMapping mapping, ImportContext ctx)
    {
        var map = mapping.Columns;
        ParsedImportRow Fail(string key) => new(line, cells, null, key);

        // date
        var dateText = Get(cells, map, ImportField.Date);
        if (dateText == null || !TryDate(dateText, mapping.DateFormat, out var date)) return Fail("Imp_ErrDate");
        TimeOnly? time = null;
        if (Get(cells, map, ImportField.Time) is { } tt)
        {
            if (!TimeOnly.TryParse(tt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return Fail("Imp_ErrTime");
            time = new TimeOnly(t.Hour, t.Minute);
        }
        else if (dateText.Contains(':') && DateTime.TryParseExact(dateText.Trim(), mapping.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withTime)
                 && withTime.TimeOfDay != TimeSpan.Zero)
            time = new TimeOnly(withTime.Hour, withTime.Minute);

        // account (a column, or the one chosen for the whole file)
        var accountId = mapping.AccountId;
        if (Get(cells, map, ImportField.Account) is { } accountName)
        {
            if (ctx.AccountByName(accountName) is not { } id) return Fail("Imp_ErrAccount");
            accountId = id;
        }
        if (!ctx.AccountCurrencies.TryGetValue(accountId, out var currency)) return Fail("Imp_ErrAccount");
        if (Get(cells, map, ImportField.Currency) is { } cur && !string.Equals(Currency.Normalize(cur), currency, StringComparison.Ordinal)) return Fail("Imp_ErrCurrency");

        // type and amount
        TransactionType type;
        decimal amount;
        TransactionType? explicitType = null;
        if (Get(cells, map, ImportField.Type) is { } typeText)
        {
            if (!TypeWords.TryGetValue(typeText.Trim(), out var tp)) return Fail("Imp_ErrType");
            explicitType = tp;
        }
        if (map.ContainsKey(ImportField.Amount))
        {
            var o = AmountParser.TryParse(Get(cells, map, ImportField.Amount), mapping.Style, out var v);
            if (AmountError(o) is { } err) return Fail(err);
            if (v == 0) return Fail("Imp_ErrZero");
            if (explicitType is { } et) { type = et; amount = Math.Abs(v); }
            else
            {
                var expense = mapping.Sign == SignConvention.NegativeIsExpense ? v < 0 : v > 0;
                type = expense ? TransactionType.Expense : TransactionType.Income;
                amount = Math.Abs(v);
            }
        }
        else
        {
            var debitText = Get(cells, map, ImportField.Debit);
            var creditText = Get(cells, map, ImportField.Credit);
            decimal debit = 0, credit = 0;
            if (debitText != null && AmountError(AmountParser.TryParse(debitText, mapping.Style, out debit)) is { } e1) return Fail(e1);
            if (creditText != null && AmountError(AmountParser.TryParse(creditText, mapping.Style, out credit)) is { } e2) return Fail(e2);
            debit = Math.Abs(debit);
            credit = Math.Abs(credit);
            if (debit != 0 && credit != 0) return Fail("Imp_ErrDebitCredit");
            if (debit == 0 && credit == 0) return Fail(debitText == null && creditText == null ? "Imp_ErrAmount" : "Imp_ErrZero");
            type = explicitType ?? (debit != 0 ? TransactionType.Expense : TransactionType.Income);
            amount = debit != 0 ? debit : credit;
        }

        // transfers need their destination
        int? toAccount = null;
        decimal? toAmount = null;
        if (type == TransactionType.Transfer)
        {
            if (Get(cells, map, ImportField.ToAccount) is not { } toName || ctx.AccountByName(toName) is not { } toId || toId == accountId) return Fail("Imp_ErrToAccount");
            toAccount = toId;
            if (Get(cells, map, ImportField.ToAmount) is { } ta)
            {
                if (AmountError(AmountParser.TryParse(ta, mapping.Style, out var tav)) is { } e3) return Fail(e3);
                toAmount = Math.Abs(tav);
            }
            if (toAmount == null && ctx.AccountCurrencies.TryGetValue(toId, out var toCur) && toCur != currency) return Fail("Imp_ErrToAmount");
        }

        var description = Get(cells, map, ImportField.Description);
        var payee = Get(cells, map, ImportField.Payee);
        int? category = type != TransactionType.Transfer && Get(cells, map, ImportField.Category) is { } catName ? ctx.CategoryByName(catName) : null;
        var draft = new TransactionDraft(type, date, amount, accountId, category, Truncate(description ?? payee ?? string.Empty, 300) ?? string.Empty, Truncate(payee, 200), time,
            toAccount, toAmount, Truncate(Get(cells, map, ImportField.Tags), 500) ?? string.Empty, Truncate(Get(cells, map, ImportField.Notes), 4000));
        return new ParsedImportRow(line, cells, draft, null);
    }

    private static string? Truncate(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>Duplicate detection for imports: never deletes anything, only says how alike two transactions look.</summary>
public static class DuplicateRules
{
    /// <summary>Lower case, letters and digits only, single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Same words (or one text containing the other); empty texts count as similar.</summary>
    public static bool Similar(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return true;
        if (x == y) return true;
        if (Math.Min(x.Length, y.Length) >= 4 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal))) return true;
        var wa = x.Split(' ').ToHashSet();
        var wb = y.Split(' ').ToHashSet();
        var common = wa.Intersect(wb).Count();
        return common > 0 && common >= Math.Max(wa.Count, wb.Count) * 0.5;
    }

    /// <summary>
    /// Likely: same account, type, date and amount and similar text. Possible: same account, type and amount within a
    /// day with similar text, or same date and amount with different text.
    /// </summary>
    public static DuplicateMatch Compare(TransactionDraft candidate, int existingAccountId, TransactionType existingType, DateOnly existingDate, decimal existingAmount, string? existingText)
    {
        if (candidate.AccountId != existingAccountId || candidate.Type != existingType || candidate.Amount != existingAmount) return DuplicateMatch.None;
        var days = Math.Abs(candidate.Date.DayNumber - existingDate.DayNumber);
        if (days > 1) return DuplicateMatch.None;
        var similar = Similar(candidate.Description.Length > 0 ? candidate.Description : candidate.Payee, existingText);
        if (days == 0) return similar ? DuplicateMatch.Likely : DuplicateMatch.Possible;
        return similar ? DuplicateMatch.Possible : DuplicateMatch.None;
    }
}

/// <summary>Transactions as CSV: UTF-8, comma-separated, ISO dates and invariant amounts (1234.56) so any program reads them back exactly.</summary>
public static class FinanceCsvExport
{
    public static readonly string[] Header = ["Date", "Time", "Type", "Amount", "Currency", "Account", "To account", "To amount", "Category", "Description", "Payee", "Tags", "Notes", "Project", "Trip"];

    public static string Build(IEnumerable<TransactionRow> rows, Func<int, string?> notesOf, Func<int?, string?> projectName, Func<int?, string?> tripName)
    {
        var sb = new StringBuilder();
        sb.Append(CsvText.Line(Header)).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(CsvText.Line(
            [
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Time?.ToString("HH:mm", CultureInfo.InvariantCulture),
                r.Type.ToString().ToLowerInvariant(),
                r.Amount.ToString("0.####", CultureInfo.InvariantCulture),
                r.Currency,
                r.AccountName,
                r.ToAccountName,
                r.ToAmount?.ToString("0.####", CultureInfo.InvariantCulture),
                r.CategoryName,
                r.Description,
                r.Payee,
                r.Tags,
                notesOf(r.Id),
                projectName(r.ProjectId),
                tripName(r.TripId),
            ])).Append("\r\n");
        }
        return sb.ToString();
    }
}
