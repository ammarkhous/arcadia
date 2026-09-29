using System.Collections.ObjectModel;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Projects;
using Arcadia.Core.Documents;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Documents;

public enum LibrarySource { View, Kind, Tag, Project }

/// <summary>An entry in the library rail (All, Favorites, PDFs, a tag, a project…).</summary>
public sealed partial class LibraryItem(LibrarySource source, string key, string icon) : ObservableObject
{
    public LibrarySource Source { get; } = source;
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public int? Id { get; init; }
    public string? Tone { get; init; }
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

public sealed record WatchedFolderRow(int Id, string Path, string Name, bool IsEnabled, string? Error);

/// <summary>A library row, with values formatted once (thousands of rows render fast).</summary>
public sealed class DocumentRowViewModel(DocumentRow row, ILocalizer loc)
{
    public DocumentRow Row { get; } = row;
    public int Id => Row.Id;
    public string Name => Row.DisplayName;
    public string Path => Row.Path;
    public string Extension => Row.Extension;
    public bool IsFolder => Row.IsFolder;
    public bool IsMissing => Row.IsMissing;
    public bool IsFavorite => Row.IsFavorite;
    public string Folder => Row.IsFolder ? Row.Path : System.IO.Path.GetDirectoryName(Row.Path) ?? string.Empty;
    public string TypeName { get; } = ShellFileInfo.TypeName(row.Extension, row.IsFolder);
    public string SizeText { get; } = row.SizeBytes is { } s ? ByteSize.Format(s, loc.Culture) : "—";
    public long SizeSort => Row.SizeBytes ?? -1;
    public string ModifiedText { get; } = row.FileModifiedUtc?.ToLocalTime().ToString("g", loc.Culture) ?? "—";
    public DateTime ModifiedSort => Row.FileModifiedUtc ?? DateTime.MinValue;
    public string AddedText { get; } = row.AddedAtUtc.ToLocalTime().ToString("d", loc.Culture);
    public string Tags => Row.Tags;
    public string Projects => Row.Projects;
}

public sealed record PreviewLine(string Text);

/// <summary>The details panel of the selected document.</summary>
public sealed partial class DocumentDetailsViewModel : ObservableObject
{
    public DocumentDetailsViewModel(Document doc, IReadOnlyList<ProjectRef> projects, ILocalizer loc)
    {
        Document = doc;
        Projects = projects;
        _displayName = doc.DisplayName;
        _description = doc.Description ?? string.Empty;
        TagNames = new ObservableCollection<string>(doc.Tags.Select(t => t.Tag?.Name ?? string.Empty).Where(n => n.Length > 0).Order(StringComparer.CurrentCultureIgnoreCase));
        Kind = FileKinds.Classify(doc.Extension, doc.IsFolder);
        PreviewKind = doc.IsFolder ? PreviewKind.None : FileKinds.Preview(doc.Extension);
        TypeName = ShellFileInfo.TypeName(doc.Extension, doc.IsFolder);
        SizeText = doc.SizeBytes is { } s ? $"{ByteSize.Format(s, loc.Culture)} ({s.ToString("N0", loc.Culture)} B)" : "—";
        CreatedText = doc.FileCreatedUtc?.ToLocalTime().ToString("g", loc.Culture) ?? "—";
        ModifiedText = doc.FileModifiedUtc?.ToLocalTime().ToString("g", loc.Culture) ?? "—";
        AddedText = doc.AddedAtUtc.ToLocalTime().ToString("g", loc.Culture);
        OpenedText = doc.LastOpenedAtUtc?.ToLocalTime().ToString("g", loc.Culture) ?? loc["Doc_NeverOpened"];
        _isFavorite = doc.IsFavorite;
        _isMissing = doc.IsMissing;
    }

    public Document Document { get; }
    public int Id => Document.Id;
    public string Path => Document.Path;
    public string Extension => Document.Extension;
    public bool IsFolder => Document.IsFolder;
    public FileKind Kind { get; }
    public PreviewKind PreviewKind { get; }
    public string TypeName { get; }
    public string SizeText { get; }
    public string CreatedText { get; }
    public string ModifiedText { get; }
    public string AddedText { get; }
    public string OpenedText { get; }
    public IReadOnlyList<ProjectRef> Projects { get; }
    public ObservableCollection<string> TagNames { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private string _newTag = string.Empty;
    [ObservableProperty] private string? _imagePreviewPath;
    [ObservableProperty] private string? _textPreview;
    [ObservableProperty] private bool _previewTruncated;
    [ObservableProperty] private bool _previewUnavailable;
    public bool HasImagePreview => ImagePreviewPath != null;
    partial void OnImagePreviewPathChanged(string? value) => OnPropertyChanged(nameof(HasImagePreview));
}

/// <summary>
/// The document library: references to files anywhere (local, removable, network), organised by type, tag,
/// project and favourites, searchable, with a safe preview for text, Markdown and images.
/// </summary>
public sealed partial class DocumentsViewModel : ObservableObject, IActivatable, IShortcutTarget, IRecipient<DocumentsChangedMessage>
{
    private const int PageSize = 500;
    private readonly IDocumentStore _store;
    private readonly IProjectStore _projects;
    private readonly DocumentActions _actions;
    private readonly DocumentAvailabilityService _availability;
    private readonly DocumentWatcherService _watcher;
    private readonly ThumbnailService _thumbnails;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILocalizer _loc;
    private readonly ISettingsService _settings;
    private readonly StartupState _startup;
    private readonly TimeProvider _time;
    private readonly ILogger<DocumentsViewModel> _logger;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _detailsCts;
    private bool _stale = true;
    private int _take = PageSize;
    private IReadOnlyList<int> _selection = [];

    public DocumentsViewModel(IDocumentStore store, IProjectStore projects, DocumentActions actions, DocumentAvailabilityService availability, DocumentWatcherService watcher,
        ThumbnailService thumbnails, INavigationService navigation, IDialogService dialogs, INotificationService notifications, ILocalizer loc, ISettingsService settings,
        StartupState startup, TimeProvider time, IMessenger messenger, ILogger<DocumentsViewModel> logger)
    {
        _store = store;
        _projects = projects;
        _actions = actions;
        _availability = availability;
        _watcher = watcher;
        _thumbnails = thumbnails;
        _navigation = navigation;
        _dialogs = dialogs;
        _notifications = notifications;
        _loc = loc;
        _settings = settings;
        _startup = startup;
        _time = time;
        _logger = logger;

        Views =
        [
            new(LibrarySource.View, nameof(DocumentView.All), "Library") { IsSelected = true },
            new(LibrarySource.View, nameof(DocumentView.Favorites), "Star"),
            new(LibrarySource.View, nameof(DocumentView.Recent), "Clock"),
            new(LibrarySource.View, nameof(DocumentView.Folders), "Documents"),
            new(LibrarySource.View, nameof(DocumentView.Missing), "Warning"),
        ];
        Kinds = new[] { FileKind.Pdf, FileKind.Word, FileKind.Spreadsheet, FileKind.Presentation, FileKind.Text, FileKind.Markdown, FileKind.Image, FileKind.Archive, FileKind.Code, FileKind.Other }
            .Select(k => new LibraryItem(LibrarySource.Kind, k.ToString(), KindIcon(k))).ToList();
        Sorts = Enum.GetValues<DocumentSort>().Select(s => new Dialogs.Option<DocumentSort>(s, loc[$"DocSort_{s}"])).ToList();
        _sort = Sorts[0];
        RefreshLabels();
        messenger.RegisterAll(this);
        loc.CultureChanged += (_, _) => { RefreshLabels(); _ = LoadAsync(); };
    }

    public IReadOnlyList<LibraryItem> Views { get; }
    public IReadOnlyList<LibraryItem> Kinds { get; }
    public ObservableCollection<LibraryItem> TagItems { get; } = [];
    public ObservableCollection<LibraryItem> ProjectItems { get; } = [];
    public ObservableCollection<WatchedFolderRow> WatchedFolders { get; } = [];
    public IReadOnlyList<Dialogs.Option<DocumentSort>> Sorts { get; }

    [ObservableProperty] private IReadOnlyList<DocumentRowViewModel> _rows = [];
    [ObservableProperty] private DocumentRowViewModel? _selected;
    [ObservableProperty] private DocumentDetailsViewModel? _details;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private Dialogs.Option<DocumentSort> _sort;
    [ObservableProperty] private bool _descending;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isLibraryEmpty;
    [ObservableProperty] private bool _hasNoMatches;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private bool _watchEnabled;

    private LibraryItem Current => Views.Concat(Kinds).Concat(TagItems).Concat(ProjectItems).FirstOrDefault(i => i.IsSelected) ?? Views[0];

    partial void OnSearchChanged(string value) => _ = LoadRowsDebouncedAsync();
    partial void OnSortChanged(Dialogs.Option<DocumentSort> value) => _ = LoadRowsAsync();
    partial void OnDescendingChanged(bool value) => _ = LoadRowsAsync();
    partial void OnSelectedChanged(DocumentRowViewModel? value) => _ = LoadDetailsAsync(value?.Id);

    partial void OnWatchEnabledChanged(bool value)
    {
        if (_settings.Current.Documents.WatchEnabled == value) return;
        _settings.Current.Documents.WatchEnabled = value;
        _ = _settings.SaveAsync();
        _ = _watcher.RefreshAsync();
    }

    public async Task ActivateAsync()
    {
        await _startup.DatabaseReady;
        WatchEnabled = _settings.Current.Documents.WatchEnabled;
        if (_stale) await LoadAsync();
        _ = _availability.CheckAllAsync();
    }

    public void Receive(DocumentsChangedMessage message)
    {
        _stale = true;
        _ = LoadAsync(keepSelection: true);
    }

    // ── loading ──────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync(bool keepSelection = false)
    {
        try
        {
            var counts = await _store.GetCountsAsync(_time.GetUtcNow().UtcDateTime);
            Views[0].Count = counts.All;
            Views[1].Count = counts.Favorites;
            Views[2].Count = counts.Recent;
            Views[3].Count = counts.Folders;
            Views[4].Count = counts.Missing;
            foreach (var k in Kinds) k.Count = counts.ByKind.GetValueOrDefault(Enum.Parse<FileKind>(k.Key));
            IsLibraryEmpty = counts.All == 0;

            var selectedKey = Current is { Source: LibrarySource.Tag or LibrarySource.Project } c ? (c.Source, c.Id) : default;
            TagItems.Clear();
            foreach (var t in await _store.GetTagsAsync())
                TagItems.Add(new LibraryItem(LibrarySource.Tag, t.Name, "Flag") { Id = t.Id, Label = t.Name, Count = t.Count, IsSelected = selectedKey == (LibrarySource.Tag, t.Id) });
            ProjectItems.Clear();
            // only projects that hold documents (hundreds of projects would otherwise fill the rail)
            foreach (var p in (await _projects.ListAsync()).Where(p => p.Status != ProjectStatus.Archived
                && (counts.ByProject.GetValueOrDefault(p.Id) > 0 || selectedKey == (LibrarySource.Project, p.Id))))
                ProjectItems.Add(new LibraryItem(LibrarySource.Project, p.Name, p.Icon) { Id = p.Id, Label = p.Name, Tone = p.Accent, Count = counts.ByProject.GetValueOrDefault(p.Id), IsSelected = selectedKey == (LibrarySource.Project, p.Id) });
            if (!Views.Concat(Kinds).Concat(TagItems).Concat(ProjectItems).Any(i => i.IsSelected)) Views[0].IsSelected = true;

            WatchedFolders.Clear();
            foreach (var w in await _store.GetWatchedFoldersAsync())
                WatchedFolders.Add(new WatchedFolderRow(w.Id, w.Path, PathRules.DisplayName(w.Path), w.IsEnabled, w.LastError));

            await LoadRowsAsync(keepSelection);
            _stale = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the document library failed");
            _notifications.Show(_loc["Doc_ErrLoad"], NotificationKind.Warning);
        }
    }

    private async Task LoadRowsDebouncedAsync()
    {
        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            _take = PageSize;
            await LoadRowsAsync(ct: cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadRowsAsync(bool keepSelection = false, CancellationToken ct = default)
    {
        var item = Current;
        var query = new DocumentQuery(
            View: item.Source == LibrarySource.View ? Enum.Parse<DocumentView>(item.Key) : DocumentView.All,
            Kind: item.Source == LibrarySource.Kind ? Enum.Parse<FileKind>(item.Key) : null,
            TagId: item.Source == LibrarySource.Tag ? item.Id : null,
            ProjectId: item.Source == LibrarySource.Project ? item.Id : null,
            Text: string.IsNullOrWhiteSpace(Search) ? null : Search,
            Sort: Sort.Value, Descending: Descending, Take: _take);
        IsLoading = true;
        try
        {
            var page = await _store.QueryAsync(query, _time.GetUtcNow().UtcDateTime, ct);
            if (ct.IsCancellationRequested) return;
            var keep = keepSelection ? Selected?.Id : null;
            Rows = page.Rows.Select(r => new DocumentRowViewModel(r, _loc)).ToList();
            Total = page.Total;
            HasMore = page.Total > Rows.Count;
            HasNoMatches = !IsLibraryEmpty && Rows.Count == 0;
            Title = item.Label;
            CountText = HasMore ? _loc.Format("Doc_CountPartial", Rows.Count, page.Total) : _loc.Format("Doc_Count", page.Total);
            Subtitle = _loc.Format("Doc_Subtitle", Views[0].Count);
            if (keep != null) Selected = Rows.FirstOrDefault(r => r.Id == keep) ?? Selected;
            if (keep != null && Selected?.Id == keep) await LoadDetailsAsync(keep);
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        _take += PageSize;
        await LoadRowsAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task SelectSourceAsync(LibraryItem? item)
    {
        if (item == null) return;
        foreach (var i in Views.Concat(Kinds).Concat(TagItems).Concat(ProjectItems)) i.IsSelected = ReferenceEquals(i, item);
        _take = PageSize;
        await LoadRowsAsync();
    }

    public void SetSelection(IReadOnlyList<int> ids) => _selection = ids;

    private IReadOnlyList<int> SelectionOrCurrent(DocumentRowViewModel? row) =>
        row != null && !_selection.Contains(row.Id) ? [row.Id] : _selection.Count > 0 ? _selection : Selected != null ? [Selected.Id] : [];

    /// <summary>Opens the library on one document (command palette, project files).</summary>
    public async Task ShowDocumentAsync(int id)
    {
        Search = string.Empty;
        await SelectSourceAsync(Views[0]);
        Selected = Rows.FirstOrDefault(r => r.Id == id);
        if (Selected == null) await LoadDetailsAsync(id);
    }

    // ── details & preview ────────────────────────────────────

    private async Task LoadDetailsAsync(int? id)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        if (id is not { } docId) { Details = null; return; }
        try
        {
            var doc = await _store.GetAsync(docId, cts.Token);
            if (doc == null || cts.IsCancellationRequested) { Details = null; return; }
            var projects = await _store.GetProjectsAsync(docId, cts.Token);
            var details = new DocumentDetailsViewModel(doc, projects, _loc);
            Details = details;
            await LoadPreviewAsync(details, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading document details failed");
        }
    }

    /// <summary>Safe previews only: text/Markdown (first 64 KB), images (cached thumbnail). Everything else: metadata + Open.</summary>
    private async Task LoadPreviewAsync(DocumentDetailsViewModel d, CancellationToken ct)
    {
        if (d.IsMissing || d.PreviewKind == PreviewKind.None) return;
        try
        {
            switch (d.PreviewKind)
            {
                case PreviewKind.Image:
                    d.ImagePreviewPath = await _thumbnails.GetAsync(d.Path, 640, ct);
                    d.PreviewUnavailable = d.ImagePreviewPath == null;
                    break;
                case PreviewKind.Text or PreviewKind.Markdown:
                    var text = await Task.Run(() => Infrastructure.Files.FileFactsService.ReadText(d.Path), ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
                    d.TextPreview = text;
                    d.PreviewTruncated = text?.Length >= Infrastructure.Files.FileFactsService.MaxContentChars;
                    d.PreviewUnavailable = text == null;
                    break;
            }
        }
        catch (TimeoutException)
        {
            d.PreviewUnavailable = true;
        }
    }

    [RelayCommand]
    private async Task SaveDetailsAsync()
    {
        if (Details is not { } d) return;
        if (d.DisplayName == d.Document.DisplayName && (d.Description ?? string.Empty) == (d.Document.Description ?? string.Empty)) return;
        await _store.UpdateDetailsAsync(d.Id, d.DisplayName, d.Description);
        d.Document.DisplayName = d.DisplayName;
        d.Document.Description = d.Description;
        await LoadRowsAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (Details is not { } d || string.IsNullOrWhiteSpace(d.NewTag)) return;
        foreach (var tag in TagText.Split(d.NewTag))
            if (!d.TagNames.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) d.TagNames.Add(tag);
        d.NewTag = string.Empty;
        await _store.SetTagsAsync(d.Id, d.TagNames.ToList());
        await LoadAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task RemoveTagAsync(string? tag)
    {
        if (Details is not { } d || tag == null) return;
        d.TagNames.Remove(tag);
        await _store.SetTagsAsync(d.Id, d.TagNames.ToList());
        await LoadAsync(keepSelection: true);
    }

    // ── actions ──────────────────────────────────────────────

    [RelayCommand] private Task AddDocumentsAsync() => _actions.PickFilesAsync(ProjectContext());
    [RelayCommand] private Task AddFolderAsync() => _actions.AddFolderAsync(ProjectContext(), watchStarted: async _ => await _watcher.RefreshAsync());

    private int? ProjectContext() => Current.Source == LibrarySource.Project ? Current.Id : null;

    public Task AddPathsAsync(IReadOnlyList<string> paths) => _actions.AddPathsAsync(paths, ProjectContext());

    [RelayCommand]
    private Task OpenAsync(DocumentRowViewModel? row)
    {
        row ??= Selected;
        return row == null ? Task.CompletedTask : _actions.OpenAsync(row.Id, row.Path);
    }

    [RelayCommand] private void OpenWith(DocumentRowViewModel? row) { if ((row ?? Selected) is { } target) _actions.OpenWith(target.Path); }
    [RelayCommand] private void Reveal(DocumentRowViewModel? row) { if ((row ?? Selected) is { } target) _actions.Reveal(target.Path); }
    [RelayCommand] private void CopyPath(DocumentRowViewModel? row) { if ((row ?? Selected) is { } target) _actions.CopyPath(target.Path); }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(DocumentRowViewModel? row)
    {
        var id = row?.Id ?? Details?.Id;
        if (id is not { } docId) return;
        var current = row?.IsFavorite ?? Details?.IsFavorite ?? false;
        await _store.SetFavoriteAsync(docId, !current);
        await LoadAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task LocateAsync(DocumentRowViewModel? row)
    {
        row ??= Selected;
        if (row == null) return;
        await _actions.LocateAsync(row.Id, row.Path);
    }

    [RelayCommand]
    private async Task AssociateAsync(DocumentRowViewModel? row)
    {
        var ids = SelectionOrCurrent(row);
        await _actions.AssociateAsync(ids, await _projects.ListAsync());
    }

    [RelayCommand]
    private async Task DissociateAsync(ProjectRef? project)
    {
        if (Details is not { } d || project == null) return;
        await _actions.RemoveFromProjectAsync(d.Id, project.Id, d.DisplayName);
    }

    [RelayCommand]
    private void OpenProject(ProjectRef? project)
    {
        if (project != null) _navigation.ShowProject(project.Id);
    }

    [RelayCommand]
    private async Task RemoveAsync(DocumentRowViewModel? row)
    {
        var ids = SelectionOrCurrent(row);
        if (ids.Count == 0) return;
        var name = ids.Count == 1 ? (Rows.FirstOrDefault(r => r.Id == ids.First())?.Name ?? Details?.DisplayName ?? string.Empty) : _loc.Format("Doc_NDocuments", ids.Count);
        await _actions.RemoveFromLibraryAsync(ids, name);
    }

    [RelayCommand]
    private async Task CheckAvailabilityAsync()
    {
        await _availability.CheckAllAsync(force: true);
        _notifications.Show(_loc.Format("Doc_Checked", Views[4].Count), NotificationKind.Success);
    }

    [RelayCommand]
    private async Task RemoveWatchedAsync(WatchedFolderRow? folder)
    {
        if (folder == null) return;
        var removeDocs = _dialogs.Confirm(_loc.Format("Doc_ConfirmUnwatch", folder.Name), _loc["Doc_UnwatchAndRemove"]);
        await _store.RemoveWatchedFolderAsync(folder.Id, removeDocs);
        await _watcher.RefreshAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleWatchedAsync(WatchedFolderRow? folder)
    {
        if (folder == null) return;
        await _store.SetWatchedFolderStateAsync(folder.Id, !folder.IsEnabled, null, null);
        await _watcher.RefreshAsync();
        await LoadAsync();
    }

    public bool OnNew()
    {
        _ = AddDocumentsAsync();
        return true;
    }

    public bool OnSave()
    {
        _ = SaveDetailsAsync();
        return true;
    }

    public static string KindIcon(FileKind k) => k switch
    {
        FileKind.Pdf => "Documents",
        FileKind.Word => "Notes",
        FileKind.Spreadsheet => "ViewGrid",
        FileKind.Presentation => "Monitor",
        FileKind.Text => "Journal",
        FileKind.Markdown => "Hash",
        FileKind.Image => "Image",
        FileKind.Archive => "Library",
        FileKind.Code => "Terminal",
        FileKind.Folder => "Documents",
        _ => "Sparkle",
    };

    private void RefreshLabels()
    {
        foreach (var v in Views) v.Label = _loc[$"DocView_{v.Key}"];
        foreach (var k in Kinds) k.Label = _loc[$"FileKind_{k.Key}"];
    }
}
