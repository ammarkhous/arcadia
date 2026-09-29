using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Finance;
using Arcadia.Core.Finance;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Dialogs;

public enum ImportStep { File, Map, Review, Done }

/// <summary>Which CSV column feeds one transaction field.</summary>
public sealed partial class FieldMapping(ImportField field, string label, bool required, IReadOnlyList<Option<int>> columns) : ObservableObject
{
    public ImportField Field { get; } = field;
    public string Label { get; } = label;
    public bool Required { get; } = required;
    public IReadOnlyList<Option<int>> Columns { get; } = columns;
    [ObservableProperty] private Option<int> _selected = columns[0];
    public override string ToString() => Label;
}

/// <summary>One parsed line in the review list.</summary>
public sealed record ImportPreviewRow(int Line, string DateText, string Description, string AmountText, string? AmountTone, string CategoryText, string StatusText, string StatusTone,
    bool IsValid, DuplicateMatch Duplicate, ParsedImportRow Parsed)
{
    public override string ToString() => $"{Line} {Description} {StatusText}";
}

/// <summary>
/// CSV import in four steps: choose the file (encoding and separator are detected and shown), map the columns (number
/// style and date format are detected or asked, never guessed), review every row (valid, invalid with the reason,
/// possible and likely duplicates), import. Nothing is written before the last step; an import can be undone.
/// </summary>
public sealed partial class ImportWizardViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly IReadOnlyList<AccountBalance> _accounts;
    private readonly IReadOnlyList<CategoryInfo> _categories;
    private string _text = string.Empty;
    private List<string[]> _rows = [];

    public ImportWizardViewModel(FinanceContext ctx, IReadOnlyList<AccountBalance> accounts, IReadOnlyList<CategoryInfo> categories)
    {
        _ctx = ctx;
        _accounts = accounts;
        _categories = categories;
        Title = ctx.Loc["Imp_Title"];
        AccountOptions = accounts.Where(a => !a.Account.IsArchived).Select(a => new FinanceOption(a.Account.Id, $"{a.Account.Name}  ·  {a.Account.Currency}", a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency)).ToList();
        _account = AccountOptions.FirstOrDefault(o => o.Id == ctx.Prefs.DefaultAccountId) ?? AccountOptions.FirstOrDefault();
        DelimiterOptions = [new(',', ctx.Loc["Imp_Comma"]), new(';', ctx.Loc["Imp_Semicolon"]), new('\t', ctx.Loc["Imp_Tab"]), new('|', "|")];
        _delimiter = DelimiterOptions[0];
        StyleOptions = [new(DecimalStyle.Point, "1,234.56"), new(DecimalStyle.Comma, "1.234,56")];
        _style = StyleOptions[0];
        SignOptions = [new(SignConvention.NegativeIsExpense, ctx.Loc["Imp_NegativeIsExpense"]), new(SignConvention.PositiveIsExpense, ctx.Loc["Imp_PositiveIsExpense"])];
        _sign = SignOptions[0];
    }

    public override string Icon => "Import";
    public override double DialogWidth => 900;
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public IReadOnlyList<Option<char>> DelimiterOptions { get; }
    public IReadOnlyList<Option<DecimalStyle>> StyleOptions { get; }
    public IReadOnlyList<Option<SignConvention>> SignOptions { get; }
    public ObservableCollection<FieldMapping> Mappings { get; } = [];
    public ObservableCollection<string> DateFormats { get; } = [];
    public ObservableCollection<string> PreviewLines { get; } = [];
    public ObservableCollection<ImportPreviewRow> Results { get; } = [];

    [ObservableProperty] private ImportStep _step = ImportStep.File;
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string? _fileInfo;
    [ObservableProperty] private FinanceOption? _account;
    [ObservableProperty] private Option<char> _delimiter;
    [ObservableProperty] private bool _hasHeader = true;
    [ObservableProperty] private Option<DecimalStyle> _style;
    [ObservableProperty] private string? _styleHint;
    [ObservableProperty] private bool _styleNeedsChoice;
    [ObservableProperty] private string? _dateFormat;
    [ObservableProperty] private string? _dateHint;
    [ObservableProperty] private bool _dateNeedsChoice;
    [ObservableProperty] private Option<SignConvention> _sign;
    [ObservableProperty] private bool _skipLikely = true;
    [ObservableProperty] private bool _skipPossible;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private int _validCount;
    [ObservableProperty] private int _invalidCount;
    [ObservableProperty] private int _likelyCount;
    [ObservableProperty] private int _possibleCount;
    [ObservableProperty] private int _importCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _batch;
    [ObservableProperty] private string? _doneText;

    public bool IsFileStep => Step == ImportStep.File;
    public bool IsMapStep => Step == ImportStep.Map;
    public bool IsReviewStep => Step == ImportStep.Review;
    public bool IsDoneStep => Step == ImportStep.Done;
    public bool HasFile => _rows.Count > 0;
    public bool CanGoBack => Step is ImportStep.Map or ImportStep.Review;
    public bool CanGoNext => Step is ImportStep.File or ImportStep.Map;
    public string StepText => _ctx.Loc.Format("Imp_StepFmt", (int)Step + 1, _ctx.Loc["ImpStep_" + Step]);
    public bool UsesSingleAmount => Mappings.FirstOrDefault(m => m.Field == ImportField.Amount)?.Selected.Value >= 0;

    partial void OnStepChanged(ImportStep value)
    {
        foreach (var p in new[] { nameof(IsFileStep), nameof(IsMapStep), nameof(IsReviewStep), nameof(IsDoneStep), nameof(CanGoBack), nameof(CanGoNext), nameof(StepText) }) OnPropertyChanged(p);
    }

    partial void OnDelimiterChanged(Option<char> value) { if (_text.Length > 0) Split(); }
    partial void OnHasHeaderChanged(bool value) { if (_rows.Count > 0) BuildMappings(); }
    partial void OnSkipLikelyChanged(bool value) => UpdateImportCount();
    partial void OnSkipPossibleChanged(bool value) => UpdateImportCount();

    [RelayCommand]
    private void PickFile()
    {
        var path = _ctx.Dialogs.PickFile("CSV (*.csv;*.txt)|*.csv;*.txt|*.*|*.*");
        if (path != null) Load(path);
    }

    /// <summary>Reads a file (also used by tests and the QA deep link).</summary>
    public void Load(string path)
    {
        Error = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > CsvText.MaxBytes) { Error = _ctx.Loc["Imp_ErrTooBig"]; return; }
            var bytes = File.ReadAllBytes(path);
            _text = CsvText.Decode(bytes, out var encoding);
            var delimiter = CsvText.DetectDelimiter(_text);
            FileName = info.Name;
            Delimiter = DelimiterOptions.FirstOrDefault(d => d.Value == delimiter) ?? DelimiterOptions[0];
            Split();
            FileInfo = _ctx.Loc.Format("Imp_FileInfo", _rows.Count, encoding, Delimiter.Label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Logger.LogWarning(ex, "Reading a CSV file failed");
            Error = _ctx.Loc["Imp_ErrRead"];
        }
    }

    private void Split()
    {
        _rows = CsvText.Parse(_text, Delimiter.Value);
        PreviewLines.Clear();
        foreach (var r in _rows.Take(6)) PreviewLines.Add(string.Join("   │   ", r.Select(c => c.Length > 28 ? c[..28] + "…" : c)));
        OnPropertyChanged(nameof(HasFile));
        if (_rows.Count < (HasHeader ? 2 : 1)) Error = _ctx.Loc["Imp_ErrEmpty"];
        BuildMappings();
    }

    private void BuildMappings()
    {
        var width = _rows.Count == 0 ? 0 : _rows.Max(r => r.Length);
        var header = HasHeader && _rows.Count > 0 ? _rows[0] : Enumerable.Range(1, width).Select(i => _ctx.Loc.Format("Imp_ColumnN", i)).ToArray();
        var sample = _rows.Skip(HasHeader ? 1 : 0).FirstOrDefault() ?? [];
        List<Option<int>> columns = [new(-1, _ctx.Loc["Imp_NotUsed"]), .. Enumerable.Range(0, width).Select(i =>
            new Option<int>(i, $"{(i < header.Length ? header[i] : "#" + (i + 1))}   ·   {(i < sample.Length ? Short(sample[i]) : string.Empty)}"))];
        var guess = HasHeader && _rows.Count > 0 ? CsvImport.GuessColumns(_rows[0]) : [];
        (ImportField Field, bool Required)[] fields =
        [
            (ImportField.Date, true), (ImportField.Amount, false), (ImportField.Debit, false), (ImportField.Credit, false), (ImportField.Description, false), (ImportField.Payee, false),
            (ImportField.Category, false), (ImportField.Type, false), (ImportField.Time, false), (ImportField.Notes, false), (ImportField.Tags, false),
            (ImportField.Account, false), (ImportField.ToAccount, false), (ImportField.ToAmount, false), (ImportField.Currency, false),
        ];
        foreach (var m in Mappings) m.PropertyChanged -= OnMappingChanged;
        Mappings.Clear();
        foreach (var (field, required) in fields)
        {
            var m = new FieldMapping(field, _ctx.Loc["ImpField_" + field] + (required ? " *" : string.Empty), required, columns);
            if (guess.TryGetValue(field, out var col)) m.Selected = columns.First(c => c.Value == col);
            m.PropertyChanged += OnMappingChanged;
            Mappings.Add(m);
        }
        Detect();
    }

    private static string Short(string s) => s.Length > 24 ? s[..24] + "…" : s;

    private void OnMappingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FieldMapping.Selected)) { Detect(); OnPropertyChanged(nameof(UsesSingleAmount)); }
    }

    private IEnumerable<string?> Column(ImportField field)
    {
        var index = Mappings.FirstOrDefault(m => m.Field == field)?.Selected.Value ?? -1;
        return index < 0 ? [] : _rows.Skip(HasHeader ? 1 : 0).Select(r => index < r.Length ? CsvText.Unguard(r[index].Trim()) : null);
    }

    /// <summary>Number style and date format from the mapped columns; when they cannot be told apart the user chooses.</summary>
    private void Detect()
    {
        var amounts = Column(ImportField.Amount).Concat(Column(ImportField.Debit)).Concat(Column(ImportField.Credit)).Concat(Column(ImportField.ToAmount)).ToList();
        var detected = AmountParser.Detect(amounts);
        StyleNeedsChoice = detected == null && AmountParser.NeedsDecision(amounts);
        if (detected is { } d) Style = StyleOptions.First(o => o.Value == d);
        else if (!StyleNeedsChoice) Style = StyleOptions.First(o => o.Value == _ctx.Money.DecimalStyle);
        StyleHint = detected != null ? _ctx.Loc.Format("Imp_StyleDetected", Style.Label) : StyleNeedsChoice ? _ctx.Loc["Imp_StyleChoose"] : null;

        var dates = Column(ImportField.Date).ToList();
        var found = CsvImport.DetectDateFormats(dates);
        DateFormats.Clear();
        foreach (var f in found.Concat(CsvImport.DateFormats.Except(found))) DateFormats.Add(f);
        DateNeedsChoice = found.Count > 1 && found.Any(f => f.StartsWith("MM", StringComparison.Ordinal) || f.StartsWith("M/", StringComparison.Ordinal));
        DateFormat = found.FirstOrDefault() ?? DateFormats.FirstOrDefault();
        DateHint = found.Count == 0 ? _ctx.Loc["Imp_DateUnknown"] : DateNeedsChoice ? _ctx.Loc["Imp_DateChoose"] : _ctx.Loc.Format("Imp_DateDetected", found[0]);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Error = null;
        if (Step == ImportStep.File)
        {
            if (!HasFile) { Error = _ctx.Loc["Imp_ErrNoFile"]; return; }
            if (Account == null) { Error = _ctx.Loc["Fin_ErrNoAccount"]; return; }
            Step = ImportStep.Map;
            return;
        }
        if (Step == ImportStep.Map) await ReviewAsync();
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        if (Step == ImportStep.Review) Step = ImportStep.Map;
        else if (Step == ImportStep.Map) Step = ImportStep.File;
    }

    private async Task ReviewAsync()
    {
        var map = Mappings.Where(m => m.Selected.Value >= 0).ToDictionary(m => m.Field, m => m.Selected.Value);
        if (!map.ContainsKey(ImportField.Date)) { Error = _ctx.Loc["Imp_ErrMapDate"]; return; }
        if (!map.ContainsKey(ImportField.Amount) && !map.ContainsKey(ImportField.Debit) && !map.ContainsKey(ImportField.Credit)) { Error = _ctx.Loc["Imp_ErrMapAmount"]; return; }
        if (DateFormat == null) { Error = _ctx.Loc["Imp_DateUnknown"]; return; }
        if (map.ContainsKey(ImportField.Amount)) { map.Remove(ImportField.Debit); map.Remove(ImportField.Credit); }
        IsBusy = true;
        try
        {
            var accountsByName = _accounts.GroupBy(a => a.Account.Name, StringComparer.CurrentCultureIgnoreCase).ToDictionary(g => g.Key, g => g.First().Account.Id, StringComparer.CurrentCultureIgnoreCase);
            var categoryNames = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var c in _categories)
                foreach (var name in new[] { c.Name }.Concat(FinanceDefaults.AliasesOf(c.Key)))
                    categoryNames.TryAdd(name.Trim(), c.Id);
            var context = new ImportContext(_accounts.ToDictionary(a => a.Account.Id, a => a.Account.Currency),
                n => accountsByName.TryGetValue(n.Trim(), out var id) ? id : null, n => categoryNames.TryGetValue(n.Trim(), out var id) ? id : null);
            var mapping = new ImportMapping(map, DateFormat, Style.Value, Sign.Value, Account!.Id!.Value, HasHeader);
            var parsed = CsvImport.Parse(_rows, mapping, context);
            var valid = parsed.Where(p => p.IsValid).ToList();
            var dup = await _ctx.Store.FindDuplicatesAsync(valid.Select(p => p.Draft!).ToList());
            var dupByLine = valid.Select((p, i) => (p.Line, dup[i])).ToDictionary(x => x.Line, x => x.Item2);
            var currencies = _accounts.ToDictionary(a => a.Account.Id, a => a.Account.Currency);
            var names = _categories.ToDictionary(c => c.Id, c => c.Name);
            Results.Clear();
            foreach (var p in parsed)
            {
                if (p.Draft is not { } d)
                {
                    Results.Add(new ImportPreviewRow(p.Line, Cell(p.Cells, map, ImportField.Date), Cell(p.Cells, map, ImportField.Description), Cell(p.Cells, map, ImportField.Amount),
                        null, string.Empty, _ctx.Loc[p.ErrorKey ?? "Imp_ErrAmount"], "Red", false, DuplicateMatch.None, p));
                    continue;
                }
                var cur = currencies.GetValueOrDefault(d.AccountId, "EUR");
                var match = dupByLine.GetValueOrDefault(p.Line);
                Results.Add(new ImportPreviewRow(p.Line, d.Date.ToString("d", _ctx.Loc.Culture), d.Description,
                    d.Type == TransactionType.Expense ? _ctx.Format(-d.Amount, cur) : d.Type == TransactionType.Income ? _ctx.FormatSigned(d.Amount, cur) : _ctx.Format(d.Amount, cur),
                    d.Type == TransactionType.Income ? "Green" : null,
                    d.Type == TransactionType.Transfer ? _ctx.Loc["FinType_Transfer"] : d.CategoryId is { } cid && names.TryGetValue(cid, out var n) ? n : _ctx.Loc["Fin_Uncategorized"],
                    _ctx.Loc["ImpDup_" + match], match switch { DuplicateMatch.Likely => "Amber", DuplicateMatch.Possible => "Gold", _ => "Green" }, true, match, p));
            }
            ValidCount = valid.Count;
            InvalidCount = parsed.Count - valid.Count;
            LikelyCount = dup.Count(m => m == DuplicateMatch.Likely);
            PossibleCount = dup.Count(m => m == DuplicateMatch.Possible);
            Summary = _ctx.Loc.Format("Imp_Summary", parsed.Count, ValidCount, InvalidCount, LikelyCount, PossibleCount);
            UpdateImportCount();
            Step = ImportStep.Review;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Preparing an import failed");
            Error = _ctx.Loc["Imp_ErrPrepare"];
        }
        finally { IsBusy = false; }
    }

    private static string Cell(string[] cells, IReadOnlyDictionary<ImportField, int> map, ImportField field) =>
        map.TryGetValue(field, out var i) && i < cells.Length ? cells[i] : string.Empty;

    private IEnumerable<ImportPreviewRow> ToImport() => Results.Where(r => r.IsValid && !(SkipLikely && r.Duplicate == DuplicateMatch.Likely) && !(SkipPossible && r.Duplicate == DuplicateMatch.Possible));

    private void UpdateImportCount() => ImportCount = ToImport().Count();

    [RelayCommand]
    private async Task ImportAsync()
    {
        var rows = ToImport().Select(r => new ImportTransaction(r.Parsed.Draft!, r.Duplicate != DuplicateMatch.None)).ToList();
        if (rows.Count == 0) { Error = _ctx.Loc["Imp_ErrNothing"]; return; }
        IsBusy = true;
        try
        {
            Batch = await _ctx.Store.ImportAsync(rows);
            DoneText = _ctx.Loc.Format("Imp_Done", rows.Count, rows.Count(r => r.NeedsReview));
            _ctx.Changed();
            Step = ImportStep.Done;
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Importing transactions failed");
            Error = _ctx.Loc["Imp_ErrImport"];
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (Batch == null || !_ctx.Dialogs.Confirm(_ctx.Loc["Imp_ConfirmUndo"], _ctx.Loc["Imp_Undo"])) return;
        if (await _ctx.TryAsync(async () => { var n = await _ctx.Store.UndoImportAsync(Batch); DoneText = _ctx.Loc.Format("Imp_Undone", n); }, "Imp_ErrImport"))
        {
            Batch = null;
            _ctx.Changed();
        }
    }

    [RelayCommand] private void Cancel() => Close(Step == ImportStep.Done);
}

/// <summary>Export transactions as CSV (filtered) or all finance data as JSON. Files go only where the user saves them.</summary>
public sealed partial class FinanceExportViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly IFinanceExporter _exporter;

    public FinanceExportViewModel(FinanceContext ctx, IFinanceExporter exporter, IReadOnlyList<AccountBalance> accounts, IReadOnlyList<CategoryInfo> categories, TransactionQuery? preset = null)
    {
        _ctx = ctx;
        _exporter = exporter;
        Title = ctx.Loc["Exp_Title"];
        AccountOptions = [new FinanceOption(null, ctx.Loc["Exp_AllAccounts"], "Wallet"), .. accounts.Select(a => new FinanceOption(a.Account.Id, a.Account.Name, a.Account.Icon, a.Account.Accent))];
        CategoryOptions = [new FinanceOption(null, ctx.Loc["Exp_AllCategories"]), .. categories.Select(c => new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null))];
        PeriodOptions = [new(null, ctx.Loc["Exp_AllTime"]), .. Enum.GetValues<FinancePeriod>().Select(p => new Option<FinancePeriod?>(p, ctx.Loc["FinPeriod_" + p]))];
        _account = AccountOptions.FirstOrDefault(o => o.Id == preset?.AccountId) ?? AccountOptions[0];
        _category = CategoryOptions.FirstOrDefault(o => o.Id == preset?.CategoryId) ?? CategoryOptions[0];
        _period = PeriodOptions[0];
        _fromText = DateText.Format(FinanceMath.MonthStart(ctx.Today), ctx.Loc.Culture);
        _toText = DateText.Format(ctx.Today, ctx.Loc.Culture);
    }

    public override string Icon => "Export";
    public override double DialogWidth => 520;
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public IReadOnlyList<FinanceOption> CategoryOptions { get; }
    public IReadOnlyList<Option<FinancePeriod?>> PeriodOptions { get; }
    [ObservableProperty] private FinanceOption _account;
    [ObservableProperty] private FinanceOption _category;
    [ObservableProperty] private Option<FinancePeriod?> _period;
    [ObservableProperty] private string _fromText;
    [ObservableProperty] private string _toText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _result;

    public bool IsCustom => Period.Value == FinancePeriod.Custom;
    partial void OnPeriodChanged(Option<FinancePeriod?> value) => OnPropertyChanged(nameof(IsCustom));

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        Error = null;
        DateOnly? from = null, to = null;
        if (Period.Value is { } p)
        {
            DateOnly? cf = null, ct = null;
            if (p == FinancePeriod.Custom && (!DateText.TryParse(FromText, _ctx.Loc.Culture, out cf) || !DateText.TryParse(ToText, _ctx.Loc.Culture, out ct))) { Error = _ctx.Loc["Proj_ErrDate"]; return; }
            (from, to) = FinancePeriods.Range(p, _ctx.Today, cf, ct);
        }
        var target = _ctx.Dialogs.PickSaveFile($"arcadia-transactions-{_ctx.Today:yyyyMMdd}.csv", "CSV (UTF-8)|*.csv");
        if (target is not { } t) return;
        try
        {
            var page = await _ctx.Store.QueryTransactionsAsync(new TransactionQuery(From: from, To: to, AccountId: Account.Id, CategoryId: Category.Id, Sort: TransactionSort.DateAsc, Take: 1_000_000), _ctx.Language);
            var notes = await _ctx.Store.GetNotesAsync(page.Rows.Where(r => r.HasNotes).Select(r => r.Id).ToList());
            var refs = page.Rows.SelectMany(r => new[]
            {
                r.ProjectId is { } pid ? new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Project, pid) : (Arcadia.Core.Organizer.EntityRef?)null,
                r.TripId is { } tid ? new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Trip, tid) : null,
            }).OfType<Arcadia.Core.Organizer.EntityRef>().Distinct().ToList();
            var names = await _ctx.Directory.DescribeAsync(refs);
            string? Name(Arcadia.Core.Organizer.EntityKind k, int? id) => id is { } i && names.TryGetValue(new(k, i), out var info) ? info.Name : null;
            var csv = FinanceCsvExport.Build(page.Rows, id => notes.GetValueOrDefault(id), id => Name(Arcadia.Core.Organizer.EntityKind.Project, id), id => Name(Arcadia.Core.Organizer.EntityKind.Trip, id));
            // UTF-8 with a byte-order mark so spreadsheet programs read Arabic and German text correctly
            await File.WriteAllTextAsync(t.Path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Result = _ctx.Loc.Format("Exp_CsvDone", page.Rows.Count, Path.GetFileName(t.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Logger.LogWarning(ex, "Writing the CSV export failed");
            Error = _ctx.Loc["Exp_ErrWrite"];
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        Error = null;
        var target = _ctx.Dialogs.PickSaveFile($"arcadia-finance-{_ctx.Today:yyyyMMdd}.json", "JSON|*.json");
        if (target is not { } t) return;
        try
        {
            await using (var stream = File.Create(t.Path)) await _exporter.WriteJsonAsync(stream);
            Result = _ctx.Loc.Format("Exp_JsonDone", Path.GetFileName(t.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Logger.LogWarning(ex, "Writing the JSON export failed");
            Error = _ctx.Loc["Exp_ErrWrite"];
        }
    }

    [RelayCommand] private void Done() => Close(false);
}
