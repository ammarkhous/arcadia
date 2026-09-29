using System.Globalization;
using System.Text;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Travel;
using Arcadia.Data;
using Arcadia.Data.Finance;
using Arcadia.Data.Organizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class MoneyTests
{
    private static decimal P(string s, DecimalStyle? style = null)
    {
        Assert.Equal(AmountParser.Outcome.Ok, AmountParser.TryParse(s, style, out var v));
        return v;
    }

    [Theory]
    [InlineData("12.50", 12.50)]
    [InlineData("12,50", 12.50)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1'234.56", 1234.56)]
    [InlineData("1\u00A0234,56 €", 1234.56)]
    [InlineData("€35", 35)]
    [InlineData("CHF 1'250.00", 1250)]
    [InlineData("-42.10", -42.10)]
    [InlineData("42.10-", -42.10)]
    [InlineData("(19.99)", -19.99)]
    [InlineData("\u221212,00 EUR", -12)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("0,123", 0.123)]
    [InlineData("1234,567", 1234.567)]
    [InlineData(".5", 0.5)]
    public void Amounts_are_read_exactly_without_a_style_when_unambiguous(string text, double expected) =>
        Assert.Equal((decimal)expected, P(text));

    [Fact]
    public void Ambiguous_thousands_need_the_users_style_and_are_never_guessed()
    {
        Assert.Equal(AmountParser.Outcome.Ambiguous, AmountParser.TryParse("1,234", null, out _));
        Assert.Equal(AmountParser.Outcome.Ambiguous, AmountParser.TryParse("1.234", null, out _));
        Assert.Equal(1234m, P("1,234", DecimalStyle.Point));
        Assert.Equal(1.234m, P("1,234", DecimalStyle.Comma));
        Assert.Equal(1.234m, P("1.234", DecimalStyle.Point));
        Assert.Equal(1234m, P("1.234", DecimalStyle.Comma));
        // a style that cannot read the text is an error, not a reinterpretation
        Assert.Equal(AmountParser.Outcome.Invalid, AmountParser.TryParse("1.234,56", DecimalStyle.Point, out _));
    }

    [Theory]
    [InlineData("", AmountParser.Outcome.Empty)]
    [InlineData("abc", AmountParser.Outcome.Invalid)]
    [InlineData("1e5", AmountParser.Outcome.Invalid)]
    [InlineData("12 EUR 5", AmountParser.Outcome.Invalid)]
    [InlineData("--5", AmountParser.Outcome.Invalid)]
    [InlineData("12.", AmountParser.Outcome.Invalid)]
    [InlineData("1,2,3", AmountParser.Outcome.Invalid)]
    [InlineData("1.23456", AmountParser.Outcome.TooPrecise)]
    [InlineData("2000000000000", AmountParser.Outcome.TooLarge)]
    [InlineData("12 34", AmountParser.Outcome.Invalid)]
    public void Bad_amounts_are_rejected(string text, AmountParser.Outcome outcome) => Assert.Equal(outcome, AmountParser.TryParse(text, null, out _));

    [Fact]
    public void A_csv_column_decides_its_style_or_asks()
    {
        Assert.Equal(DecimalStyle.Comma, AmountParser.Detect(["-12,50", "1.234,00", "3"]));
        Assert.Equal(DecimalStyle.Point, AmountParser.Detect(["-12.50", "1,234.00"]));
        Assert.Null(AmountParser.Detect(["12", "1,234", "300"]));
        Assert.True(AmountParser.NeedsDecision(["12", "1,234"]));
        Assert.False(AmountParser.NeedsDecision(["12", "7"]));
    }

    [Fact]
    public void Decimal_arithmetic_is_exact()
    {
        Assert.Equal(0.3m, 0.1m + 0.2m);
        var total = Enumerable.Repeat(0.01m, 100_000).Sum();
        Assert.Equal(1000m, total);
        Assert.Equal(33.33m, MoneyFormatter.Round(100m / 3m, "EUR"));
        Assert.Equal(0.13m, MoneyFormatter.Round(0.125m, "EUR")); // half away from zero, display only
        Assert.Equal(-0.13m, MoneyFormatter.Round(-0.125m, "EUR"));
    }

    [Fact]
    public void Money_is_formatted_per_currency_in_the_chosen_number_style()
    {
        var en = new MoneyFormatter(CultureInfo.GetCultureInfo("en-US"));
        var de = new MoneyFormatter(CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("€1,250.00", en.Format(1250m, "EUR"));
        Assert.Equal("-€12.00", en.Format(-12m, "EUR"));
        Assert.Equal("1.250,00 €", de.Format(1250m, "EUR").Replace('\u00A0', ' '));
        Assert.Equal("CHF 12.50", en.Format(12.5m, "CHF").Replace('\u00A0', ' '));
        Assert.Equal("¥1,235", en.Format(1234.6m, "JPY"));
        Assert.Equal("KWD 1.235", en.Format(1.2345m, "KWD").Replace('\u00A0', ' '));
        Assert.Equal("+$3,000.00", en.FormatSigned(3000m, "USD"));
        Assert.Equal("€1.2k", en.FormatCompact(1234m, "EUR"));
        Assert.Equal(DecimalStyle.Comma, de.DecimalStyle);
    }

    [Fact]
    public void Currencies_are_iso_codes_and_detected_only_when_clear()
    {
        Assert.True(Currency.IsSupported("eur"));
        Assert.True(Currency.IsSupported("CHF"));
        Assert.True(Currency.IsSupported("SAR"));
        Assert.False(Currency.IsSupported("XYZ"));
        Assert.False(Currency.IsSupported("EURO"));
        Assert.Equal(2, Currency.DecimalsOf("EUR"));
        Assert.Equal(0, Currency.DecimalsOf("JPY"));
        Assert.Equal(3, Currency.DecimalsOf("KWD"));
        Assert.Equal("EUR", Currency.Detect("€35 groceries"));
        Assert.Equal("CHF", Currency.Detect("12 CHF Kaffee"));
        Assert.Null(Currency.Detect("35 groceries"));
        Assert.Null(Currency.Detect("€ and $"));
    }
}

public sealed class FinanceMathTests
{
    private static TransactionFlow Inc(int acc, decimal a) => new(TransactionType.Income, acc, null, a, null);
    private static TransactionFlow Exp(int acc, decimal a) => new(TransactionType.Expense, acc, null, a, null);
    private static TransactionFlow Tr(int from, int to, decimal a, decimal? toAmount = null) => new(TransactionType.Transfer, from, to, a, toAmount);

    [Fact]
    public void Balance_is_opening_plus_income_minus_expenses_plus_minus_transfers()
    {
        var flows = new[] { Inc(1, 3000m), Exp(1, 900m), Exp(1, 100m), Tr(1, 2, 500m) };
        var b = FinanceMath.Balances([(1, 2000m), (2, 0m)], flows);
        Assert.Equal(3500m, b[1]);
        Assert.Equal(500m, b[2]);
        Assert.Equal(b[1], FinanceMath.Balance(1, 2000m, flows));
    }

    [Fact]
    public void A_transfer_never_changes_total_wealth_in_one_currency_and_removing_it_restores_both()
    {
        var before = FinanceMath.Balances([(1, 1000m), (2, 250m)], [Inc(1, 10m)]);
        var with = FinanceMath.Balances([(1, 1000m), (2, 250m)], [Inc(1, 10m), Tr(1, 2, 100m)]);
        Assert.Equal(before.Values.Sum(), with.Values.Sum());
        Assert.Equal(before[1] - 100m, with[1]);
        Assert.Equal(before[2] + 100m, with[2]);
        // the destination amount counts for cross-currency transfers
        var fx = FinanceMath.Balances([(1, 1000m), (3, 0m)], [Tr(1, 3, 100m, 93.45m)]);
        Assert.Equal((900m, 93.45m), (fx[1], fx[3]));
    }

    [Fact]
    public void Budget_levels_are_factual_thresholds()
    {
        Assert.Equal(BudgetLevel.Within, BudgetMath.LevelOf(84.99m, 100m));
        Assert.Equal(BudgetLevel.Approaching, BudgetMath.LevelOf(85m, 100m));
        Assert.Equal(BudgetLevel.Approaching, BudgetMath.LevelOf(100m, 100m));
        Assert.Equal(BudgetLevel.Over, BudgetMath.LevelOf(100.01m, 100m));
        Assert.Equal(BudgetLevel.Within, BudgetMath.LevelOf(0m, 0m));
    }

    [Fact]
    public void Rollover_carries_only_what_was_left_and_only_when_asked()
    {
        var b = new Budget { Amount = 100m, Currency = "EUR", Period = BudgetPeriod.Monthly, StartDate = new DateOnly(2026, 7, 1), Rollover = true };
        var spent = new Dictionary<int, (decimal, int)>
        {
            [FinanceMath.MonthIndex(new DateOnly(2026, 7, 1))] = (50m, 2),   // 50 left → carried
            [FinanceMath.MonthIndex(new DateOnly(2026, 8, 1))] = (170m, 3),  // 150 available, overspent by 20 → nothing carried
            [FinanceMath.MonthIndex(new DateOnly(2026, 9, 1))] = (30m, 1),
        };
        var aug = BudgetMath.Monthly(b, new DateOnly(2026, 8, 1), spent);
        Assert.Equal((50m, 150m, 170m, -20m, BudgetLevel.Over), (aug.Carried, aug.Available, aug.Spent, aug.Remaining, aug.Level));
        var sep = BudgetMath.Monthly(b, new DateOnly(2026, 9, 1), spent);
        Assert.Equal((0m, 100m, 70m), (sep.Carried, sep.Available, sep.Remaining));
        var oct = BudgetMath.Monthly(b, new DateOnly(2026, 10, 1), spent);
        Assert.Equal(70m, oct.Carried);
        b.Rollover = false;
        Assert.Equal(0m, BudgetMath.Monthly(b, new DateOnly(2026, 10, 1), spent).Carried);
        Assert.False(BudgetMath.AppliesTo(b, new DateOnly(2026, 6, 1)));
        Assert.True(BudgetMath.AppliesTo(b, new DateOnly(2027, 6, 1)));
    }

    [Fact]
    public void Recurring_costs_are_nominal_per_year()
    {
        Assert.Equal(479.88m, RecurringMath.AnnualCost(39.99m, RecurrenceFrequency.Monthly, 1));
        Assert.Equal(39.99m, RecurringMath.MonthlyEquivalent(39.99m, RecurrenceFrequency.Monthly, 1));
        Assert.Equal(10m, RecurringMath.MonthlyEquivalent(120m, RecurrenceFrequency.Yearly, 1));
        Assert.Equal(520m, RecurringMath.AnnualCost(10m, RecurrenceFrequency.Weekly, 1));
        Assert.Equal(240m, RecurringMath.AnnualCost(40m, RecurrenceFrequency.Monthly, 2));
    }

    [Fact]
    public void Open_due_dates_skip_paid_skipped_and_untracked_history()
    {
        var item = new RecurringItem
        {
            Name = "Rent", Amount = 900m, Frequency = RecurrenceFrequency.Monthly, Interval = 1, StartDate = new DateOnly(2025, 1, 31), TrackedFrom = new DateOnly(2026, 8, 1),
            Occurrences = [new() { DueDate = new DateOnly(2026, 8, 31), Status = OccurrenceStatus.Paid }, new() { DueDate = new DateOnly(2026, 10, 31), Status = OccurrenceStatus.Skipped }],
        };
        var open = RecurringMath.OpenDueDates(item, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ToList();
        Assert.Equal(new[] { new DateOnly(2026, 9, 30), new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31) }, open); // month-end clamping from the recurrence engine
        Assert.Equal(new DateOnly(2026, 9, 30), RecurringMath.NextDue(item, new DateOnly(2026, 9, 1)));
        item.IsActive = false;
        Assert.Empty(RecurringMath.OpenDueDates(item, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Goal_progress_is_arithmetic_on_entered_data()
    {
        var g = new SavingsGoal { Name = "New PC", TargetAmount = 2000m, TargetDate = new DateOnly(2026, 12, 20) };
        var p = GoalProgress.Of(g, 500m, new DateOnly(2026, 10, 5));
        Assert.Equal((1500m, 25.0, 3, 500m, false), (p.Remaining, p.Percent, p.MonthsLeft!.Value, p.PerMonthToTarget!.Value, p.IsReached));
        var done = GoalProgress.Of(g, 2100m, new DateOnly(2026, 10, 5));
        Assert.True(done.IsReached);
        Assert.Equal(0m, done.Remaining);
        Assert.Null(done.PerMonthToTarget);
        Assert.Null(GoalProgress.Of(new SavingsGoal { TargetAmount = 10m }, 1m, new DateOnly(2026, 1, 1)).PerMonthToTarget);
    }

    [Fact]
    public void Report_periods_are_calendar_months_and_years()
    {
        var today = new DateOnly(2026, 9, 28);
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), FinancePeriods.Range(FinancePeriod.ThisMonth, today));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)), FinancePeriods.Range(FinancePeriod.LastMonth, today));
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)), FinancePeriods.Range(FinancePeriod.Last3Months, today));
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30)), FinancePeriods.Range(FinancePeriod.Last6Months, today));
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), FinancePeriods.Range(FinancePeriod.LastYear, today));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), FinancePeriods.Range(FinancePeriod.Custom, today, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
    }
}

public sealed class FinanceCsvTests
{
    private static readonly ImportContext Ctx = new(new Dictionary<int, string> { [1] = "EUR", [2] = "EUR", [3] = "USD" },
        name => name.Trim().ToLowerInvariant() switch { "checking" => 1, "savings" => 2, "dollars" => 3, _ => null },
        name => name.Trim().ToLowerInvariant() is "groceries" or "lebensmittel" ? 2 : null);

    private static ImportMapping Map(Dictionary<ImportField, int> cols, string format = "yyyy-MM-dd", DecimalStyle style = DecimalStyle.Point, SignConvention sign = SignConvention.NegativeIsExpense) =>
        new(cols, format, style, sign, 1, true);

    [Fact]
    public void Quoted_fields_embedded_separators_and_line_breaks_survive()
    {
        var rows = CsvText.Parse("Date,Description,Amount\r\n2026-09-01,\"Rent, September\",-900.00\n2026-09-02,\"He said \"\"hi\"\"\nsecond line\",12\n\n", ',');
        Assert.Equal(3, rows.Count);
        Assert.Equal("Rent, September", rows[1][1]);
        Assert.Equal("He said \"hi\"\nsecond line", rows[2][1]);
    }

    [Fact]
    public void Delimiter_and_encoding_are_detected()
    {
        Assert.Equal(';', CsvText.DetectDelimiter("Buchungstag;Verwendungszweck;Betrag\n01.09.2026;Miete, Sept;-900,00\n02.09.2026;REWE;-45,10"));
        Assert.Equal('\t', CsvText.DetectDelimiter("a\tb\tc\n1\t2\t3"));
        var latin = Encoding.GetEncoding(1252).GetBytes("Überweisung;Größe");
        Assert.Equal("Überweisung;Größe", CsvText.Decode(latin, out var enc1));
        Assert.Equal("Windows-1252", enc1);
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("بقالة;Lebensmittel")).ToArray();
        Assert.Equal("بقالة;Lebensmittel", CsvText.Decode(utf8, out var enc2));
        Assert.Equal("UTF-8", enc2);
    }

    [Fact]
    public void Slash_dates_are_ambiguous_until_the_user_chooses()
    {
        var formats = CsvImport.DetectDateFormats(["01/02/2026", "03/04/2026"]);
        Assert.Contains("dd/MM/yyyy", formats);
        Assert.Contains("MM/dd/yyyy", formats);
        Assert.Equal(new[] { "dd/MM/yyyy", "d/M/yyyy" }, CsvImport.DetectDateFormats(["01/02/2026", "25/04/2026"]));
        Assert.Equal("dd.MM.yyyy", CsvImport.DetectDateFormats(["01.09.2026", "30.09.2026"])[0]);
        // one impossible date does not hide the column's format (that row is reported as invalid instead)
        Assert.Equal("dd.MM.yyyy", CsvImport.DetectDateFormats(["28.09.2026", "31.09.2026", "24.09.2026"])[0]);
        Assert.Empty(CsvImport.DetectDateFormats(["hello", "world", "28.09.2026"]));
    }

    [Fact]
    public void Header_names_in_three_languages_are_mapped()
    {
        var map = CsvImport.GuessColumns(["Buchungstag", "Verwendungszweck", "Auftraggeber", "Betrag"]);
        Assert.Equal((0, 1, 2, 3), (map[ImportField.Date], map[ImportField.Description], map[ImportField.Payee], map[ImportField.Amount]));
        var export = CsvImport.GuessColumns(FinanceCsvExport.Header);
        Assert.Equal(FinanceCsvExport.Header.Length - 2, export.Count); // everything except project and trip
        Assert.Equal(1, CsvImport.GuessColumns(["التاريخ", "المبلغ"])[ImportField.Amount]);
    }

    [Fact]
    public void Signs_debit_credit_and_type_columns_decide_the_direction()
    {
        var cols = new Dictionary<ImportField, int> { [ImportField.Date] = 0, [ImportField.Description] = 1, [ImportField.Amount] = 2 };
        List<string[]> rows = [["Date", "Text", "Amount"], ["2026-09-01", "Salary", "3000.00"], ["2026-09-02", "REWE", "-45.10"], ["2026-09-03", "Zero", "0"], ["bad", "x", "1"]];
        var parsed = CsvImport.Parse(rows, Map(cols), Ctx);
        Assert.Equal((TransactionType.Income, 3000m), (parsed[0].Draft!.Type, parsed[0].Draft!.Amount));
        Assert.Equal((TransactionType.Expense, 45.10m), (parsed[1].Draft!.Type, parsed[1].Draft!.Amount));
        Assert.Equal("Imp_ErrZero", parsed[2].ErrorKey);
        Assert.Equal("Imp_ErrDate", parsed[3].ErrorKey);
        // credit-card style: purchases positive
        var card = CsvImport.Parse(rows, Map(cols, sign: SignConvention.PositiveIsExpense), Ctx);
        Assert.Equal(TransactionType.Expense, card[0].Draft!.Type);

        var dc = new Dictionary<ImportField, int> { [ImportField.Date] = 0, [ImportField.Description] = 1, [ImportField.Debit] = 2, [ImportField.Credit] = 3 };
        List<string[]> dcRows = [["d", "t", "debit", "credit"], ["01.09.2026", "Miete", "900,00", ""], ["02.09.2026", "Gehalt", "", "3.000,00"], ["03.09.2026", "Both", "1,00", "2,00"]];
        var dcParsed = CsvImport.Parse(dcRows, Map(dc, "dd.MM.yyyy", DecimalStyle.Comma), Ctx);
        Assert.Equal((TransactionType.Expense, 900m), (dcParsed[0].Draft!.Type, dcParsed[0].Draft!.Amount));
        Assert.Equal((TransactionType.Income, 3000m), (dcParsed[1].Draft!.Type, dcParsed[1].Draft!.Amount));
        Assert.Equal("Imp_ErrDebitCredit", dcParsed[2].ErrorKey);
    }

    [Fact]
    public void Arcadia_exports_round_trip_with_type_accounts_categories_and_transfers()
    {
        var cols = CsvImport.GuessColumns(FinanceCsvExport.Header);
        List<string[]> rows =
        [
            FinanceCsvExport.Header,
            ["2026-09-01", "", "income", "3000", "EUR", "Checking", "", "", "", "Salary", "ACME", "", "", "", ""],
            ["2026-09-05", "14:30", "expense", "100", "EUR", "Checking", "", "", "Lebensmittel", "'-50% sale", "REWE", "food", "", "", ""],
            ["2026-09-06", "", "transfer", "500", "EUR", "Checking", "Savings", "", "", "To savings", "", "", "", "", ""],
            ["2026-09-07", "", "transfer", "100", "EUR", "Checking", "Dollars", "", "", "FX", "", "", "", "", ""],
            ["2026-09-08", "", "expense", "5", "USD", "Checking", "", "", "", "Wrong currency", "", "", "", "", ""],
        ];
        var parsed = CsvImport.Parse(rows, new ImportMapping(cols, "yyyy-MM-dd", DecimalStyle.Point, SignConvention.NegativeIsExpense, 1, true), Ctx);
        Assert.Equal(TransactionType.Income, parsed[0].Draft!.Type);
        Assert.Equal((2, "-50% sale", new TimeOnly(14, 30), "food"), (parsed[1].Draft!.CategoryId!.Value, parsed[1].Draft!.Description, parsed[1].Draft!.Time!.Value, parsed[1].Draft!.Tags));
        Assert.Equal((TransactionType.Transfer, 2), (parsed[2].Draft!.Type, parsed[2].Draft!.ToAccountId!.Value));
        Assert.Equal("Imp_ErrToAmount", parsed[3].ErrorKey); // across currencies the received amount is required
        Assert.Equal("Imp_ErrCurrency", parsed[4].ErrorKey);
    }

    [Fact]
    public void Written_cells_are_quoted_and_formula_safe()
    {
        Assert.Equal("\"Rent, Sept\"", CsvText.Cell("Rent, Sept"));
        Assert.Equal("'=SUM(A1)", CsvText.Cell("=SUM(A1)"));
        Assert.Equal("=SUM(A1)", CsvText.Unguard("'=SUM(A1)"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvText.Cell("say \"hi\""));
    }

    [Fact]
    public void Duplicates_are_graded_never_removed()
    {
        var c = new TransactionDraft(TransactionType.Expense, new DateOnly(2026, 9, 2), 45.10m, 1, Description: "REWE Markt Dresden");
        Assert.Equal(DuplicateMatch.Likely, DuplicateRules.Compare(c, 1, TransactionType.Expense, new DateOnly(2026, 9, 2), 45.10m, "REWE MARKT DRESDEN"));
        Assert.Equal(DuplicateMatch.Possible, DuplicateRules.Compare(c, 1, TransactionType.Expense, new DateOnly(2026, 9, 3), 45.10m, "rewe markt"));
        Assert.Equal(DuplicateMatch.Possible, DuplicateRules.Compare(c, 1, TransactionType.Expense, new DateOnly(2026, 9, 2), 45.10m, "Tankstelle"));
        Assert.Equal(DuplicateMatch.None, DuplicateRules.Compare(c, 1, TransactionType.Expense, new DateOnly(2026, 9, 2), 45.11m, "REWE"));
        Assert.Equal(DuplicateMatch.None, DuplicateRules.Compare(c, 2, TransactionType.Expense, new DateOnly(2026, 9, 2), 45.10m, "REWE"));
    }
}

public sealed class QuickExpenseParserTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0);

    [Theory]
    [InlineData("35 groceries", 35, null, "groceries")]
    [InlineData("35.50 groceries", 35.50, null, "groceries")]
    [InlineData("€35 groceries", 35, "EUR", "groceries")]
    [InlineData("12,90 € Kino", 12.90, "EUR", "Kino")]
    [InlineData("groceries 12.30", 12.30, null, "groceries")]
    [InlineData("CHF 4.50 Kaffee", 4.50, "CHF", "Kaffee")]
    [InlineData("20 بقالة", 20, null, "بقالة")]
    public void Clear_patterns_are_understood(string text, double amount, string? currency, string description)
    {
        var q = QuickExpenseParser.Parse(text, Now, DecimalStyle.Point);
        Assert.True(q.IsClear);
        Assert.Equal((decimal)amount, q.Amount);
        Assert.Equal(currency, q.Currency);
        Assert.Equal(description, q.Description);
    }

    [Fact]
    public void Date_words_in_the_description_set_the_date()
    {
        var q = QuickExpenseParser.Parse("12 EUR Kino gestern", Now, DecimalStyle.Point);
        Assert.Equal((12m, "EUR", "Kino", new DateOnly(2026, 9, 27)), (q.Amount!.Value, q.Currency, q.Description, q.Date!.Value));
    }

    [Theory]
    [InlineData("groceries")]
    [InlineData("35")]
    [InlineData("2 coffees 7.40")]
    [InlineData("groceries 35 and 12")]
    [InlineData("xyz12 groceries")]
    public void Anything_else_goes_to_the_editor(string text) => Assert.False(QuickExpenseParser.Parse(text, Now, DecimalStyle.Point).IsClear);

    [Fact]
    public void Thousands_follow_the_users_number_style()
    {
        Assert.Equal(1234m, QuickExpenseParser.Parse("1,234 laptop", Now, DecimalStyle.Point).Amount);
        Assert.Equal(1.234m, QuickExpenseParser.Parse("1,234 laptop", Now, DecimalStyle.Comma).Amount);
    }
}

public sealed class FinanceStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private readonly Clock _clock = new(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc));
    private FinanceStore _fin = null!;
    private static readonly DateOnly Today = new(2026, 9, 28);

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    internal sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Utc);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _fin = new FinanceStore(_factory, _clock, NullLogger<FinanceStore>.Instance);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private Task<FinanceAccount> Account(string name, decimal opening = 0m, string currency = "EUR", AccountType type = AccountType.Checking) =>
        _fin.CreateAccountAsync(new AccountDraft(name, type, currency, opening));

    private Task<FinanceTransaction> Expense(int account, decimal amount, int? category = null, string text = "", DateOnly? date = null) =>
        _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, date ?? Today, amount, account, category, text));

    private Task<FinanceTransaction> Income(int account, decimal amount, string text = "", DateOnly? date = null, int? category = 16) =>
        _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Income, date ?? Today, amount, account, category, text));

    private Task<FinanceTransaction> Transfer(int from, int to, decimal amount, decimal? toAmount = null, DateOnly? date = null) =>
        _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Transfer, date ?? Today, amount, from, ToAccountId: to, ToAmount: toAmount, Description: "Transfer"));

    private async Task<decimal> Balance(int id) => (await _fin.GetAccountBalancesAsync(true)).Single(b => b.Account.Id == id).Balance;

    [Fact]
    public async Task Default_categories_are_seeded_with_one_level_of_subcategories()
    {
        var cats = await _fin.GetCategoriesAsync("de");
        Assert.Equal(21, cats.Count);
        Assert.Equal("Lebensmittel", cats.Single(c => c.Key == "groceries").Name);
        var transport = cats.Single(c => c.Key == "transport");
        Assert.Equal(4, cats.Count(c => c.ParentId == transport.Id));
        Assert.Equal("fuel", cats[cats.ToList().IndexOf(transport) + 1].Key); // children follow their parent
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateCategoryAsync(new CategoryDraft("Diesel", CategoryKind.Expense, 18))); // no grandchildren
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateCategoryAsync(new CategoryDraft("Bonus", CategoryKind.Income, 4))); // kinds match
        var bonus = await _fin.CreateCategoryAsync(new CategoryDraft("Bonus", CategoryKind.Income, 16));
        Assert.Equal(16, bonus.ParentId);
    }

    [Fact]
    public async Task Balances_follow_income_expenses_transfers_edits_and_deletes()
    {
        var checking = await Account("Checking", 2000m);
        var savings = await Account("Savings", 0m, type: AccountType.Savings);
        await Income(checking.Id, 3000m, "Salary");
        var rent = await Expense(checking.Id, 900m, 1, "Rent");
        await Expense(checking.Id, 100m, 2, "Groceries");
        var tr = await Transfer(checking.Id, savings.Id, 500m);
        Assert.Equal(3500m, await Balance(checking.Id));
        Assert.Equal(500m, await Balance(savings.Id));

        // total wealth in EUR is unchanged by the transfer; deleting it restores both balances
        await _fin.DeleteTransactionAsync(tr.Id);
        Assert.Equal((4000m, 0m), (await Balance(checking.Id), await Balance(savings.Id)));

        // editing an amount recalculates
        await _fin.UpdateTransactionAsync(rent.Id, new TransactionDraft(TransactionType.Expense, Today, 950.55m, checking.Id, 1, "Rent"));
        Assert.Equal(3949.45m, await Balance(checking.Id));
        var page = await _fin.QueryTransactionsAsync(new TransactionQuery(), "en");
        Assert.Equal(3, page.TotalCount);
        var eur = page.Totals.Single();
        Assert.Equal((3000m, 1050.55m, 1949.45m), (eur.Income, eur.Expenses, eur.Net));
    }

    [Fact]
    public async Task The_accounting_model_is_enforced()
    {
        var a = await Account("A", 10m);
        var b = await Account("B");
        var usd = await Account("Dollars", 0m, "USD");
        await Assert.ThrowsAsync<FinanceException>(() => Expense(a.Id, 0m));
        await Assert.ThrowsAsync<FinanceException>(() => Expense(a.Id, -5m));
        await Assert.ThrowsAsync<FinanceException>(() => Expense(a.Id, 1.23456m));
        await Assert.ThrowsAsync<FinanceException>(() => Expense(a.Id, 2_000_000_000_000m));
        await Assert.ThrowsAsync<FinanceException>(() => Transfer(a.Id, a.Id, 5m));
        await Assert.ThrowsAsync<FinanceException>(() => Transfer(a.Id, usd.Id, 5m)); // across currencies the received amount is needed
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateAccountAsync(new AccountDraft("Wallet", AccountType.Cash, "EURO")));
        await Assert.ThrowsAsync<FinanceException>(() => Account("a")); // names are unique (case-insensitive)
        var fx = await Transfer(a.Id, usd.Id, 10m, 10.87m);
        Assert.Equal((0m, 10.87m), (await Balance(a.Id), await Balance(usd.Id)));
        // a transfer never carries a category
        var t = await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Transfer, Today, 1m, b.Id, CategoryId: 2, ToAccountId: a.Id));
        Assert.Null((await _fin.GetTransactionAsync(t.Id))!.CategoryId);
        // an account with transactions cannot be deleted (only archived); its currency is locked
        await Assert.ThrowsAsync<FinanceException>(() => _fin.DeleteAccountAsync(a.Id));
        await Assert.ThrowsAsync<FinanceException>(() => _fin.UpdateAccountAsync(usd.Id, new AccountDraft("Dollars", AccountType.Checking, "GBP")));
        await _fin.SetAccountArchivedAsync(a.Id, true);
        await Assert.ThrowsAsync<FinanceException>(() => Expense(a.Id, 1m)); // nothing new on an archived account
        Assert.DoesNotContain(await _fin.GetAccountBalancesAsync(), x => x.Account.Id == a.Id);
        await _fin.DeleteTransactionAsync(fx.Id);
        var empty = await Account("Empty");
        await _fin.DeleteAccountAsync(empty.Id);
    }

    [Fact]
    public async Task Currencies_are_never_added_together()
    {
        var eur = await Account("Euro", 0m);
        var usd = await Account("Dollar", 0m, "USD");
        await Expense(eur.Id, 10m, 2);
        await Expense(usd.Id, 20m, 2);
        var totals = await _fin.GetMonthlyExpensesAsync(2026, 9);
        Assert.Equal(new[] { new MoneyTotal("USD", 20m), new MoneyTotal("EUR", 10m) }, totals);
        var spending = await _fin.GetCategorySpendingAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "en");
        Assert.Equal(2, spending.Count);
        Assert.All(spending, s => Assert.Equal("Groceries", s.Name));
        var flows = await _fin.GetMonthlyFlowsAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1));
        Assert.Equal(4, flows.Count); // two months × two currencies, zero months included
        Assert.Equal(10m, flows.Single(f => f.Currency == "EUR" && f.Month.Month == 9).Expenses);
    }

    [Fact]
    public async Task Category_totals_roll_subcategories_into_their_parent()
    {
        var a = await Account("Car");
        await Expense(a.Id, 60m, 18, "Fuel");      // transport → fuel
        await Expense(a.Id, 4.5m, 20, "Parking");  // transport → parking
        await Expense(a.Id, 40m, 4, "Train");      // transport itself
        await Expense(a.Id, 12m, null, "Misc");
        var rolled = await _fin.GetCategorySpendingAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "en");
        Assert.Equal((4, 104.5m, 3), (rolled[0].CategoryId!.Value, rolled[0].Amount, rolled[0].Count));
        Assert.Null(rolled[1].CategoryId); // uncategorized stays visible
        var flat = await _fin.GetCategorySpendingAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "en", rollUp: false);
        Assert.Equal(4, flat.Count);
        // filtering the register by a parent includes its subcategories
        Assert.Equal(3, (await _fin.QueryTransactionsAsync(new TransactionQuery(CategoryId: 4), "en")).TotalCount);
    }

    [Fact]
    public async Task Budgets_report_spent_remaining_and_state_with_optional_rollover()
    {
        var a = await Account("Main", 1000m);
        var groceries = await _fin.CreateBudgetAsync(new BudgetDraft(2, 400m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 8, 15), Rollover: true));
        Assert.Equal(new DateOnly(2026, 8, 1), groceries.StartDate); // monthly budgets start on the 1st
        var gaming = await _fin.CreateBudgetAsync(new BudgetDraft(7, 100m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 9, 1)));
        await _fin.CreateBudgetAsync(new BudgetDraft(4, 50m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 9, 1)));
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateBudgetAsync(new BudgetDraft(2, 1m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 9, 1)))); // one per category
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateBudgetAsync(new BudgetDraft(16, 1m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 9, 1)))); // income category
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateBudgetAsync(new BudgetDraft(null, 1m, "EUR", BudgetPeriod.Custom, new DateOnly(2026, 9, 1)))); // custom needs an end

        await Expense(a.Id, 350m, 2, "August groceries", new DateOnly(2026, 8, 20));
        await Expense(a.Id, 380m, 2, "September groceries");
        await Expense(a.Id, 101m, 7, "Game");
        await Expense(a.Id, 60m, 18, "Fuel"); // subcategory counts for the Transportation budget
        var status = await _fin.GetBudgetStatusAsync(new DateOnly(2026, 9, 10));
        var g = status.Single(s => s.Budget.Id == groceries.Id);
        Assert.Equal((50m, 450m, 380m, 70m, BudgetLevel.Within), (g.Carried, g.Available, g.Spent, g.Remaining, g.Level));
        Assert.Equal(BudgetLevel.Over, status.Single(s => s.Budget.Id == gaming.Id).Level);
        Assert.Equal((60m, BudgetLevel.Over), (status.Single(s => s.Budget.CategoryId == 4).Spent, status.Single(s => s.Budget.CategoryId == 4).Level));
        Assert.Equal(BudgetLevel.Over, status[0].Level); // most urgent first
        // other currencies never count
        var usd = await Account("USD", 0m, "USD");
        await Expense(usd.Id, 999m, 2);
        Assert.Equal(380m, (await _fin.GetBudgetStatusAsync(new DateOnly(2026, 9, 1))).Single(s => s.Budget.Id == groceries.Id).Spent);
        // a custom range
        var holiday = await _fin.CreateBudgetAsync(new BudgetDraft(null, 1000m, "EUR", BudgetPeriod.Custom, new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 5), Name: "Holiday weeks"));
        var custom = (await _fin.GetBudgetStatusAsync(new DateOnly(2026, 9, 1))).Single(s => s.Budget.Id == holiday.Id);
        Assert.Equal(380m + 101m + 60m, custom.Spent);
    }

    [Fact]
    public async Task Bills_are_paid_skipped_and_reopened_through_real_transactions()
    {
        var a = await Account("Main", 1000m);
        var internet = await _fin.CreateRecurringItemAsync(new RecurringDraft("Internet", RecurringKind.Subscription, 39.99m, "EUR", RecurrenceFrequency.Monthly, 1,
            new DateOnly(2026, 9, 29), AccountId: a.Id, CategoryId: 12, Payee: "Telekom"));
        var rent = await _fin.CreateRecurringItemAsync(new RecurringDraft("Rent", RecurringKind.Bill, 900m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2024, 10, 1), AccountId: a.Id, CategoryId: 1));
        Assert.Equal(Today, rent.TrackedFrom); // two years of history are not "unpaid"
        await Assert.ThrowsAsync<FinanceException>(() => _fin.CreateRecurringItemAsync(new RecurringDraft("X", RecurringKind.Bill, 1m, "USD", RecurrenceFrequency.Monthly, 1, Today, AccountId: a.Id)));

        var upcoming = await _fin.GetUpcomingBillsAsync(Today, Today.AddDays(10), "en");
        Assert.Equal(new[] { ("Internet", new DateOnly(2026, 9, 29)), ("Rent", new DateOnly(2026, 10, 1)) }, upcoming.Select(b => (b.Item.Name, b.DueDate)));

        var paid = await _fin.MarkPaidAsync(internet.Id, new DateOnly(2026, 9, 29));
        Assert.Equal((TransactionType.Expense, 39.99m, 12, internet.Id), (paid.Type, paid.Amount, paid.CategoryId!.Value, paid.RecurringItemId!.Value));
        Assert.Equal(1000m - 39.99m, await Balance(a.Id));
        await Assert.ThrowsAsync<FinanceException>(() => _fin.MarkPaidAsync(internet.Id, new DateOnly(2026, 9, 29)));
        await Assert.ThrowsAsync<FinanceException>(() => _fin.MarkPaidAsync(internet.Id, new DateOnly(2026, 9, 30))); // not a due date
        await _fin.SkipAsync(rent.Id, new DateOnly(2026, 10, 1));
        upcoming = await _fin.GetUpcomingBillsAsync(Today, Today.AddDays(10), "en");
        Assert.Empty(upcoming);
        await _fin.ReopenAsync(rent.Id, new DateOnly(2026, 10, 1));
        Assert.Single(await _fin.GetUpcomingBillsAsync(Today, Today.AddDays(10), "en"));
        // deleting the payment makes the date due again
        await _fin.DeleteTransactionAsync(paid.Id);
        Assert.Equal(2, (await _fin.GetUpcomingBillsAsync(Today, Today.AddDays(10), "en")).Count);
        // an overdue date is listed as overdue once time passes
        _clock.Utc = _clock.Utc.AddDays(5);
        var later = await _fin.GetUpcomingBillsAsync(Today.AddDays(5), Today.AddDays(6), "en");
        Assert.All(later, b => Assert.True(b.IsOverdue));
    }

    [Fact]
    public async Task Subscription_statistics_are_local_arithmetic()
    {
        var a = await Account("Main");
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Streaming", RecurringKind.Subscription, 12.99m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 10, 3), AccountId: a.Id));
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Cloud", RecurringKind.Subscription, 99m, "EUR", RecurrenceFrequency.Yearly, 1, new DateOnly(2027, 1, 15)));
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Old", RecurringKind.Subscription, 5m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 1, 1), IsActive: false));
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Salary", RecurringKind.Income, 3000m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 10, 1)));
        var subs = await _fin.GetSubscriptionsAsync(Today, "en");
        Assert.Equal(new[] { "Streaming", "Cloud", "Old" }, subs.Select(s => s.Item.Name));
        Assert.Equal(new DateOnly(2026, 10, 3), subs[0].NextDue);
        Assert.Null(subs[2].NextDue);
        var active = subs.Where(s => s.Item.IsActive).ToList();
        Assert.Equal(12.99m * 12 + 99m, active.Sum(s => s.AnnualCost));
        Assert.Equal(12.99m + 8.25m, active.Sum(s => s.MonthlyEquivalent));
        Assert.Single(await _fin.GetSubscriptionsAsync(Today, "en", RecurringKind.Income));
    }

    [Fact]
    public async Task Goals_are_manual_or_follow_an_account()
    {
        var savings = await Account("Savings", 1200m, type: AccountType.Savings);
        var pc = await _fin.CreateGoalAsync(new GoalDraft("New PC", 2000m, "EUR", 300m, new DateOnly(2026, 12, 31)));
        var fund = await _fin.CreateGoalAsync(new GoalDraft("Emergency fund", 5000m, "EUR", AccountId: savings.Id));
        await _fin.AddToGoalAsync(pc.Id, 200m);
        await Assert.ThrowsAsync<FinanceException>(() => _fin.AddToGoalAsync(pc.Id, -900m));
        await Assert.ThrowsAsync<FinanceException>(() => _fin.AddToGoalAsync(fund.Id, 10m));
        var goals = await _fin.GetSavingsGoalsAsync(Today);
        var p = goals.Single(g => g.Goal.Id == pc.Id);
        Assert.Equal((500m, 1500m, 25.0, 4), (p.Saved, p.Remaining, p.Percent, p.MonthsLeft!.Value));
        Assert.Equal(1200m, goals.Single(g => g.Goal.Id == fund.Id).Saved);
        await Income(savings.Id, 300m, "Interest");
        Assert.Equal(1500m, (await _fin.GetSavingsGoalsAsync(Today)).Single(g => g.Goal.Id == fund.Id).Saved);
    }

    [Fact]
    public async Task Monthly_summary_counts_saved_money_and_budgets_over_limit()
    {
        var checking = await Account("Checking", 0m);
        var savings = await Account("Savings", 0m, type: AccountType.Savings);
        await Income(checking.Id, 3200m, "Salary");
        await Expense(checking.Id, 1500m, 1, "Rent");
        await Expense(checking.Id, 650m, 2, "Food");
        await Transfer(checking.Id, savings.Id, 600m);
        await _fin.CreateBudgetAsync(new BudgetDraft(2, 400m, "EUR", BudgetPeriod.Monthly, new DateOnly(2026, 9, 1)));
        var s = (await _fin.GetMonthlySummaryAsync(2026, 9, "en")).Single();
        Assert.Equal((3200m, 2150m, 1050m, 600m, 4, "housing", 1, 1), (s.Income, s.Expenses, s.Net, s.Saved, s.TransactionCount, s.LargestCategory!.Key, s.BudgetsOverLimit, s.BudgetCount));
        var largest = await _fin.GetLargestExpensesAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "en", 1);
        Assert.Equal("Rent", largest.Single().Description);
        var history = (await _fin.GetBalanceHistoryAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), "EUR")).Single(h => h.AccountId == checking.Id);
        Assert.Equal(new[] { 0m, 450m }, history.Points.Select(p => p.Balance));
    }

    [Fact]
    public async Task Trips_and_projects_see_their_recorded_expenses_and_a_trip_budget()
    {
        var projects = new ProjectStore(_factory, _clock);
        var project = await projects.CreateAsync(new ProjectDraft("Home Server", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "mountains-dusk", "", null));
        var travel = new TravelStore(_factory, _clock, null);
        var trip = await travel.CreateTripAsync(new TripDraft("Italy 2027", TripStatus.Planning, new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 10)));
        var a = await Account("Main", 5000m);
        foreach (var (amount, text) in new[] { (120m, "SSD"), (90m, "RAM"), (75m, "Switch") })
            await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, Today, amount, a.Id, 5, text, ProjectId: project.Id));
        await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, Today, 450m, a.Id, 8, "Hotel", TripId: trip.Id));
        await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, Today, 62m, a.Id, 3, "Restaurant", TripId: trip.Id));
        await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, Today, 40m, a.Id, 19, "Train", TripId: trip.Id));
        await _fin.CreateBudgetAsync(new BudgetDraft(null, 500m, "EUR", BudgetPeriod.Custom, new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 10), TripId: trip.Id));

        var p = await _fin.GetProjectExpensesAsync(project.Id, "en");
        Assert.Equal(new MoneyTotal("EUR", 285m), p.Totals.Single());
        Assert.Equal(3, p.Transactions.Count);
        var t = await _fin.GetTripExpensesAsync(trip.Id, "en");
        Assert.Equal(new MoneyTotal("EUR", 552m), t.Totals.Single());
        Assert.Equal(new[] { "Travel", "Restaurants", "Transportation" }, t.ByCategory.Select(c => c.Name));
        var budget = t.Budgets.Single();
        Assert.Equal((552m, -52m, BudgetLevel.Over), (budget.Spent, budget.Remaining, budget.Level));
        // monthly budgets ignore trip budgets
        Assert.DoesNotContain(await _fin.GetBudgetStatusAsync(new DateOnly(2027, 5, 1)), s => s.Budget.TripId != null);
        // deleting the trip keeps the transactions (unlinked) and removes its budget
        await travel.DeleteTripAsync(trip.Id);
        Assert.Equal(6, (await _fin.QueryTransactionsAsync(new TransactionQuery(), "en")).TotalCount);
        Assert.Empty(await _fin.GetBudgetsAsync());
    }

    [Fact]
    public async Task Import_marks_duplicates_for_review_and_can_be_undone()
    {
        var a = await Account("Main", 100m);
        await Expense(a.Id, 45.10m, 2, "REWE Markt", new DateOnly(2026, 9, 2));
        var drafts = new List<TransactionDraft>
        {
            new(TransactionType.Expense, new DateOnly(2026, 9, 2), 45.10m, a.Id, Description: "REWE MARKT"),   // likely
            new(TransactionType.Expense, new DateOnly(2026, 9, 3), 45.10m, a.Id, Description: "rewe markt"),   // possible (next day)
            new(TransactionType.Expense, new DateOnly(2026, 9, 4), 3.20m, a.Id, Description: "Bakery"),
            new(TransactionType.Expense, new DateOnly(2026, 9, 4), 3.20m, a.Id, Description: "Bakery"),        // repeated inside the file
        };
        var dup = await _fin.FindDuplicatesAsync(drafts);
        Assert.Equal(new[] { DuplicateMatch.Likely, DuplicateMatch.Possible, DuplicateMatch.None, DuplicateMatch.Possible }, dup);
        var batch = await _fin.ImportAsync(drafts.Skip(1).Select((d, i) => new ImportTransaction(d, dup[i + 1] != DuplicateMatch.None)).ToList());
        var review = await _fin.QueryTransactionsAsync(new TransactionQuery(NeedsReviewOnly: true), "en");
        Assert.Equal(2, review.TotalCount);
        Assert.Equal(100m - 45.10m * 2 - 6.40m, await Balance(a.Id));
        Assert.Equal(3, await _fin.UndoImportAsync(batch));
        Assert.Equal(100m - 45.10m, await Balance(a.Id));
    }

    [Fact]
    public async Task Search_finds_transactions_by_text_category_names_in_any_language_and_account()
    {
        var a = await Account("Sparkasse Giro");
        var t = await _fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, Today, 129.99m, a.Id, 5, "Headphones", "MediaMarkt", Tags: "tech, gift", Notes: "birthday present"));
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Internet", RecurringKind.Subscription, 39.99m, "EUR", RecurrenceFrequency.Monthly, 1, Today));
        var search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(_factory)]);
        foreach (var q in new[] { "mediamarkt", "Einkäufe", "التسوق", "gift", "birthday", "sparkasse", "headph" })
            Assert.Contains(await search.SearchAsync(new SearchRequest(q)), h => h.Kind == SearchResultKind.Transaction && h.Id == t.Id);
        Assert.Contains(await search.SearchAsync(new SearchRequest("internet")), h => h.Kind == SearchResultKind.Recurring);
        Assert.Contains(await search.SearchAsync(new SearchRequest("sparkasse")), h => h.Kind == SearchResultKind.Account);
        // the register's own text filter matches built-in category names too
        Assert.Equal(1, (await _fin.QueryTransactionsAsync(new TransactionQuery(Text: "Einkäufe"), "en")).TotalCount);
        await _fin.DeleteTransactionAsync(t.Id);
        Assert.DoesNotContain(await search.SearchAsync(new SearchRequest("mediamarkt")), h => h.Kind == SearchResultKind.Transaction);
    }

    [Fact]
    public async Task Bills_appear_on_the_calendar_as_a_switchable_projection()
    {
        var a = await Account("Main");
        var rent = await _fin.CreateRecurringItemAsync(new RecurringDraft("Rent", RecurringKind.Bill, 900m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 10, 1), AccountId: a.Id));
        await _fin.CreateRecurringItemAsync(new RecurringDraft("Hidden", RecurringKind.Bill, 1m, "EUR", RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 10, 1), ShowOnCalendar: false));
        var query = new CalendarQueryService(_factory, _clock);
        var items = await query.GetItemsAsync(new DateTime(2026, 10, 1), new DateTime(2026, 12, 1), CalendarSources.All);
        var bills = items.Where(i => i.Kind == CalendarItemKind.Bill).ToList();
        Assert.Equal(new[] { new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1) }, bills.Select(b => b.StartDate));
        Assert.All(bills, b => Assert.Equal(new EntityRef(EntityKind.Recurring, rent.Id), b.Ref));
        Assert.DoesNotContain(await query.GetItemsAsync(new DateTime(2026, 10, 1), new DateTime(2026, 12, 1), CalendarSources.All & ~CalendarSources.Finance), i => i.Kind == CalendarItemKind.Bill);
        await _fin.MarkPaidAsync(rent.Id, new DateOnly(2026, 10, 1));
        Assert.True((await query.GetItemsAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), CalendarSources.Finance)).Single().IsDone);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.CalendarEvents.CountAsync()); // nothing copied into events
    }

    [Fact]
    public async Task Quick_expenses_save_only_what_is_clear()
    {
        var eur = await Account("Main");
        var service = new QuickExpenseService(_fin, _factory);
        var r = await service.CaptureAsync("€35 groceries", new DateTime(2026, 9, 28, 12, 0, 0), DecimalStyle.Point, null);
        Assert.NotNull(r.Saved);
        Assert.Equal((35m, 2, "groceries", eur.Id), (r.Saved!.Amount, r.Saved.CategoryId!.Value, r.Saved.Description, r.Saved.AccountId));
        var fuel = await service.CaptureAsync("60 Tanken", new DateTime(2026, 9, 28, 12, 0, 0), DecimalStyle.Point, null);
        Assert.Equal(18, fuel.Saved!.CategoryId); // the German name of a subcategory
        // two accounts and no default: the editor decides
        await Account("Second");
        var unclear = await service.CaptureAsync("12 lunch", new DateTime(2026, 9, 28, 12, 0, 0), DecimalStyle.Point, null);
        Assert.Null(unclear.Saved);
        Assert.Equal((12m, "lunch"), (unclear.Draft.Amount, unclear.Draft.Description));
        var withDefault = await service.CaptureAsync("12 lunch", new DateTime(2026, 9, 28, 12, 0, 0), DecimalStyle.Point, eur.Id);
        Assert.NotNull(withDefault.Saved);
        Assert.Null((await service.CaptureAsync("lunch twelve", new DateTime(2026, 9, 28, 12, 0, 0), DecimalStyle.Point, eur.Id)).Saved);
    }

    [Fact]
    public async Task Receipts_reference_library_documents_without_copying()
    {
        var a = await Account("Main");
        var t = await Expense(a.Id, 10m, 2, "Shop");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Documents.Add(new Arcadia.Core.Documents.Document { Id = 7, DisplayName = "receipt.pdf", Path = @"C:\Receipts\receipt.pdf", Extension = ".pdf", AddedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await _fin.AttachDocumentAsync(t.Id, 7, DocumentRole.Receipt);
        await _fin.AttachDocumentAsync(t.Id, 7, DocumentRole.Invoice); // re-attaching changes the role
        var docs = await _fin.GetDocumentsAsync(t.Id);
        Assert.Equal((7, DocumentRole.Invoice, "receipt.pdf"), (docs.Single().DocumentId, docs.Single().Role, docs.Single().Name));
        Assert.Equal(1, (await _fin.QueryTransactionsAsync(new TransactionQuery(), "en")).Rows.Single().DocumentCount);
        await Assert.ThrowsAsync<FinanceException>(() => _fin.AttachDocumentAsync(t.Id, 99, DocumentRole.Receipt));
        await _fin.DetachDocumentAsync(t.Id, 7);
        Assert.Empty(await _fin.GetDocumentsAsync(t.Id));
    }

    [Fact]
    public async Task Four_decimal_amounts_are_stored_exactly()
    {
        var a = await Account("Precise", 0.0001m);
        await Expense(a.Id, 0.0001m);
        await Income(a.Id, 999_999_999_999.9999m);
        Assert.Equal(999_999_999_999.9999m, await Balance(a.Id));
        var row = (await _fin.QueryTransactionsAsync(new TransactionQuery(Sort: TransactionSort.AmountAsc), "en")).Rows[0];
        Assert.Equal(0.0001m, row.Amount);
    }

    [Fact]
    public async Task Json_export_contains_every_finance_record()
    {
        var a = await Account("Main", 10m);
        await Expense(a.Id, 3.5m, 2, "Bread");
        await _fin.CreateGoalAsync(new GoalDraft("Car", 1000m, "EUR"));
        using var ms = new MemoryStream();
        await new FinanceJsonExporter(_factory, _fin, _clock).WriteJsonAsync(ms);
        using var json = System.Text.Json.JsonDocument.Parse(ms.ToArray());
        var root = json.RootElement;
        Assert.Equal("arcadia-finance", root.GetProperty("schema").GetString());
        Assert.Equal(6.5m, root.GetProperty("accounts")[0].GetProperty("balance").GetDecimal());
        Assert.Equal(3.5m, root.GetProperty("transactions")[0].GetProperty("Amount").GetDecimal());
        Assert.Equal(21, root.GetProperty("categories").GetArrayLength());
        Assert.Equal("Car", root.GetProperty("goals")[0].GetProperty("Name").GetString());
    }
}

/// <summary>The spec's end-to-end scenario on a database file: create, restart, verify, export, import elsewhere, verify.</summary>
public sealed class FinanceIntegrationTests
{
    private sealed class FileFactory(string file) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options);
    }

    [Fact]
    public async Task Checking_salary_rent_groceries_transfer_budget_subscription_receipt_project_trip_restart_export_import()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-p7-{Guid.NewGuid():N}")).FullName;
        try
        {
            var file = Path.Combine(dir, "arcadia.db");
            var clock = new FinanceStoreTests.Clock(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc));
            var factory = new FileFactory(file);
            await new DatabaseInitializer(factory, clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(false);
            var fin = new FinanceStore(factory, clock, NullLogger<FinanceStore>.Instance);
            var d = new DateOnly(2026, 9, 28);

            var checking = await fin.CreateAccountAsync(new AccountDraft("Checking", AccountType.Checking, "EUR", 2000m));
            var savings = await fin.CreateAccountAsync(new AccountDraft("Savings", AccountType.Savings, "EUR"));
            await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Income, d.AddDays(-27), 3000m, checking.Id, 16, "Salary", "ACME"));
            await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, d.AddDays(-26), 900m, checking.Id, 1, "Rent"));
            var groceries = await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, d.AddDays(-2), 100m, checking.Id, 2, "Groceries", "REWE", new TimeOnly(18, 5)));
            await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Transfer, d.AddDays(-1), 500m, checking.Id, ToAccountId: savings.Id, Description: "Monthly saving"));
            Assert.Equal(3500m, (await fin.GetAccountBalancesAsync()).Single(b => b.Account.Id == checking.Id).Balance);
            Assert.Equal(500m, (await fin.GetAccountBalancesAsync()).Single(b => b.Account.Id == savings.Id).Balance);

            var budget = await fin.CreateBudgetAsync(new BudgetDraft(2, 400m, "EUR", BudgetPeriod.Monthly, d));
            var internet = await fin.CreateRecurringItemAsync(new RecurringDraft("Internet", RecurringKind.Subscription, 39.99m, "EUR", RecurrenceFrequency.Monthly, 1, d.AddDays(3), AccountId: checking.Id, CategoryId: 12));
            int docId, projectId, tripId;
            await using (var db = factory.CreateDbContext())
            {
                var doc = new Arcadia.Core.Documents.Document { DisplayName = "rewe-receipt.pdf", Path = Path.Combine(dir, "rewe-receipt.pdf"), Extension = ".pdf", AddedAtUtc = DateTime.UtcNow };
                db.Documents.Add(doc);
                await db.SaveChangesAsync();
                docId = doc.Id;
            }
            await fin.AttachDocumentAsync(groceries.Id, docId, DocumentRole.Receipt);
            projectId = (await new ProjectStore(factory, clock).CreateAsync(new ProjectDraft("Home Server", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "mountains-dusk", "", null))).Id;
            tripId = (await new TravelStore(factory, clock, null).CreateTripAsync(new TripDraft("Italy 2027", TripStatus.Planning, new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 10)))).Id;
            var ssd = await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, d, 120m, checking.Id, 5, "SSD", ProjectId: projectId));
            var hotel = await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, d, 450m, checking.Id, 8, "Hotel deposit", TripId: tripId));

            // "close Arcadia, reopen": a brand-new store and factory over the same file
            SqliteConnection.ClearAllPools();
            var reopened = new FinanceStore(new FileFactory(file), clock, NullLogger<FinanceStore>.Instance);
            var balances = await reopened.GetAccountBalancesAsync();
            Assert.Equal((2930m, 500m), (balances.Single(b => b.Account.Name == "Checking").Balance, balances.Single(b => b.Account.Name == "Savings").Balance));
            var status = (await reopened.GetBudgetStatusAsync(d)).Single(s => s.Budget.Id == budget.Id);
            Assert.Equal((100m, 300m), (status.Spent, status.Remaining));
            var sub = (await reopened.GetSubscriptionsAsync(d, "en")).Single();
            Assert.Equal((internet.Id, d.AddDays(3), 479.88m), (sub.Item.Id, sub.NextDue!.Value, sub.AnnualCost));
            Assert.Equal("rewe-receipt.pdf", (await reopened.GetDocumentsAsync(groceries.Id)).Single().Name);
            Assert.Equal(ssd.Id, (await reopened.GetProjectExpensesAsync(projectId, "en")).Transactions.Single().Id);
            Assert.Equal(hotel.Id, (await reopened.GetTripExpensesAsync(tripId, "en")).Transactions.Single().Id);

            // export CSV, import into a separate profile, verify the values
            var all = await reopened.QueryTransactionsAsync(new TransactionQuery(Sort: TransactionSort.DateAsc), "en");
            var csv = FinanceCsvExport.Build(all.Rows, _ => null, _ => null, _ => null);
            var otherFile = Path.Combine(dir, "other.db");
            var other = new FileFactory(otherFile);
            await new DatabaseInitializer(other, clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(false);
            var target = new FinanceStore(other, clock, NullLogger<FinanceStore>.Instance);
            var c2 = await target.CreateAccountAsync(new AccountDraft("Checking", AccountType.Checking, "EUR", 2000m));
            var s2 = await target.CreateAccountAsync(new AccountDraft("Savings", AccountType.Savings, "EUR"));
            var cats = await target.GetCategoriesAsync("en");
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(csv)).ToArray();
            var text = CsvText.Decode(bytes, out _);
            var rows = CsvText.Parse(text, CsvText.DetectDelimiter(text));
            var mapping = new ImportMapping(CsvImport.GuessColumns(rows[0]), CsvImport.DetectDateFormats(rows.Skip(1).Select(r => r[0]))[0], DecimalStyle.Point, SignConvention.NegativeIsExpense, c2.Id, true);
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Checking"] = c2.Id, ["Savings"] = s2.Id };
            var parsed = CsvImport.Parse(rows, mapping, new ImportContext(new Dictionary<int, string> { [c2.Id] = "EUR", [s2.Id] = "EUR" },
                n => names.TryGetValue(n, out var id) ? id : null, n => cats.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))?.Id));
            Assert.All(parsed, p => Assert.True(p.IsValid, p.ErrorKey));
            await target.ImportAsync(parsed.Select(p => new ImportTransaction(p.Draft!, false)).ToList());
            var imported = await target.QueryTransactionsAsync(new TransactionQuery(Sort: TransactionSort.DateAsc), "en");
            Assert.Equal(all.Rows.Select(r => (r.Type, r.Date, r.Time, r.Amount, r.AccountName, r.ToAccountName, r.CategoryName, r.Description, r.Payee)),
                imported.Rows.Select(r => (r.Type, r.Date, r.Time, r.Amount, r.AccountName, r.ToAccountName, r.CategoryName, r.Description, r.Payee)));
            var b2 = await target.GetAccountBalancesAsync();
            Assert.Equal((2930m, 500m), (b2.Single(b => b.Account.Id == c2.Id).Balance, b2.Single(b => b.Account.Id == s2.Id).Balance));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class FinanceMigrationTests
{
    private sealed class FileFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task A_phase_6_library_gains_finance_and_keeps_everything()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-p7m-{Guid.NewGuid():N}")).FullName;
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("Organizer");
                // tables the Finance migration does not touch: written through the model
                await OldSchema.InsertAsync(db, "Games", new() { ["Id"] = 3, ["Title"] = "Elden Ring", ["Platform"] = "PC", ["Tags"] = "rpg", ["PlaytimeHours"] = 100.0, ["AddedAtUtc"] = DateTime.UtcNow });
                db.Tasks.Add(new TaskItem { Id = 8, Title = "Backup NAS", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
                db.CalendarEvents.Add(new CalendarEvent { Id = 5, Title = "Dentist", Start = new DateTime(2026, 10, 1, 9, 0, 0), End = new DateTime(2026, 10, 1, 10, 0, 0), CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
            await using var after = new ArcadiaDbContext(options);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Finance", StringComparison.Ordinal));
            Assert.Equal("Elden Ring", (await after.Games.SingleAsync()).Title);
            Assert.Equal("Backup NAS", (await after.Tasks.SingleAsync()).Title);
            Assert.Equal("Dentist", (await after.CalendarEvents.SingleAsync()).Title);
            Assert.Equal(21, await after.FinanceCategories.CountAsync());
            Assert.Equal(0, await after.FinanceTransactions.CountAsync());
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "Backups"), "arcadia-before-*.db")); // named after the newest migration applied
            // the upgraded library works end to end
            var fin = new FinanceStore(factory, TimeProvider.System, NullLogger<FinanceStore>.Instance);
            var a = await fin.CreateAccountAsync(new AccountDraft("Main", AccountType.Checking, "EUR", 10m));
            await fin.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, new DateOnly(2026, 9, 1), 2.5m, a.Id, 2, "Milk"));
            Assert.Equal(7.5m, (await fin.GetAccountBalancesAsync()).Single().Balance);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>Writes a row into a table as an older schema has it (later phases may have added columns to the model).</summary>
internal static class OldSchema
{
    public static async Task InsertAsync(ArcadiaDbContext db, string table, Dictionary<string, object> values)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        var columns = new List<(string Name, string Type, bool NotNull, bool HasDefault)>();
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var r = await info.ExecuteReaderAsync();
            while (await r.ReadAsync()) columns.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, !r.IsDBNull(4)));
        }
        var row = new Dictionary<string, object>(values);
        foreach (var c in columns.Where(c => c.NotNull && !c.HasDefault && !row.ContainsKey(c.Name)))
            row[c.Name] = c.Type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? 0 : c.Type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ? 0.0 : "";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", row.Keys.Select(k => "\"" + k + "\""))}) VALUES ({string.Join(",", row.Keys.Select((_, i) => "@p" + i))})";
        var n = 0;
        foreach (var v in row.Values)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + n++;
            p.Value = v is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF") : v;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync();
    }
}
