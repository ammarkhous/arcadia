using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Arcadia.App.Dialogs;
using Arcadia.Core.Documents;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Documents were added, removed or changed (library views and project file lists refresh).</summary>
public sealed record DocumentsChangedMessage(int? ProjectId = null);

/// <summary>A project (or the project list) changed.</summary>
public sealed record ProjectsChangedMessage(int? ProjectId = null);

/// <summary>
/// Everything the UI does with document references: add (files, folders, drag and drop), open, open with,
/// show in Explorer, locate a moved file, associate with projects and remove from Arcadia. Files on disk are
/// never modified, moved or deleted.
/// </summary>
public sealed class DocumentActions(
    IDocumentStore store,
    IFileFactsService files,
    IDialogService dialogs,
    INotificationService notifications,
    IClipboardService clipboard,
    IExplorerLauncher explorer,
    IMessenger messenger,
    ILocalizer loc,
    TimeProvider time,
    ILogger<DocumentActions> logger)
{
    public const int ImportChunk = 400;

    public async Task<AddDocumentsResult?> AddPathsAsync(IReadOnlyList<string> paths, int? projectId)
    {
        if (paths.Count == 0) return null;
        try
        {
            var facts = await Task.WhenAll(paths.Select(p => files.GetFactsAsync(p, readContent: true)));
            var result = await store.AddAsync(facts, projectId, null);
            Report(result);
            messenger.Send(new DocumentsChangedMessage(projectId));
            if (projectId != null) messenger.Send(new ProjectsChangedMessage(projectId));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adding {Count} documents failed", paths.Count);
            notifications.Show(loc["Doc_ErrAdd"], NotificationKind.Warning);
            return null;
        }
    }

    public Task<AddDocumentsResult?> PickFilesAsync(int? projectId)
    {
        var picked = dialogs.PickFiles(loc["Doc_AllFiles"] + " (*.*)|*.*", multiple: true);
        return picked.Count == 0 ? Task.FromResult<AddDocumentsResult?>(null) : AddPathsAsync(picked, projectId);
    }

    /// <summary>Asks how to add a folder: as one reference, by indexing its documents, or by also watching it.</summary>
    public async Task AddFolderAsync(int? projectId, string? folder = null, Func<string, Task>? watchStarted = null)
    {
        folder ??= dialogs.PickFolder();
        if (folder == null) return;
        var choice = new AddFolderViewModel(loc, folder);
        if (!dialogs.Show(choice)) return;
        try
        {
            if (choice.Mode == AddFolderMode.Reference)
            {
                await AddPathsAsync([folder], projectId);
                return;
            }
            int? watchedId = null;
            if (choice.Mode == AddFolderMode.Watch)
            {
                watchedId = (await store.AddWatchedFolderAsync(folder, choice.IncludeSubfolders)).Id;
                if (watchStarted != null) await watchStarted(folder);
            }
            notifications.Show(loc.Format("Doc_Indexing", PathRules.DisplayName(folder)));
            var found = await files.ScanFolderAsync(folder, choice.IncludeSubfolders, p => FileKinds.IndexedByDefault.Contains(Path.GetExtension(p)), null);
            int added = 0, existing = 0;
            foreach (var chunk in found.Chunk(ImportChunk))
            {
                var r = await store.AddAsync(chunk, projectId, watchedId);
                added += r.Added;
                existing += r.AlreadyInLibrary;
            }
            notifications.Show(loc.Format("Doc_FolderIndexed", added, existing, PathRules.DisplayName(folder)), NotificationKind.Success);
            messenger.Send(new DocumentsChangedMessage(projectId));
            if (projectId != null) messenger.Send(new ProjectsChangedMessage(projectId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Indexing {Folder} failed", folder);
            notifications.Show(loc.Format("Doc_ErrFolder", ex.Message), NotificationKind.Warning);
        }
    }

    private void Report(AddDocumentsResult r)
    {
        if (r.Added > 0) notifications.Show(loc.Format("Doc_Added", r.Added), NotificationKind.Success);
        else if (r.AlreadyInLibrary > 0) notifications.Show(loc.Format("Doc_AlreadyThere", r.AlreadyInLibrary));
        if (r.Skipped > 0) notifications.Show(loc.Format("Doc_Skipped", r.Skipped), NotificationKind.Warning);
    }

    /// <summary>Opens with the default program. A missing file is marked and the user can locate it.</summary>
    public async Task OpenAsync(int id, string path)
    {
        var facts = await files.GetFactsAsync(path, readContent: false);
        if (!facts.Exists)
        {
            await store.UpdateAvailabilityAsync([(id, facts)], time.GetUtcNow().UtcDateTime);
            messenger.Send(new DocumentsChangedMessage());
            if (dialogs.Confirm(loc.Format("Doc_MissingLocate", PathRules.DisplayName(path)), loc["Doc_Locate"])) await LocateAsync(id, path);
            return;
        }
        if (!Start(new ProcessStartInfo(path) { UseShellExecute = true }, path))
        {
            notifications.Show(loc["Doc_ErrOpen"], NotificationKind.Warning);
            return;
        }
        await store.MarkOpenedAsync(id, time.GetUtcNow().UtcDateTime);
        messenger.Send(new DocumentsChangedMessage());
    }

    /// <summary>Windows' "Open with" chooser.</summary>
    public void OpenWith(string path)
    {
        if (!File.Exists(path)) { notifications.Show(loc["Doc_ErrMissing"], NotificationKind.Warning); return; }
        if (!Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false }, path))
            notifications.Show(loc["Doc_ErrOpen"], NotificationKind.Warning);
    }

    public void Reveal(string path)
    {
        if (!explorer.Reveal(path)) notifications.Show(loc["Doc_ErrFolderUnavailable"], NotificationKind.Warning);
    }

    public void CopyPath(string path)
    {
        if (clipboard.SetText(path)) notifications.Show(loc["Doc_PathCopied"], NotificationKind.Success);
    }

    /// <summary>"Locate file": reconnects a reference to the file's new location.</summary>
    public async Task<bool> LocateAsync(int id, string oldPath)
    {
        var ext = Path.GetExtension(oldPath);
        var filter = string.IsNullOrEmpty(ext) ? loc["Doc_AllFiles"] + " (*.*)|*.*" : $"{PathRules.DisplayName(oldPath)} (*{ext})|*{ext}|{loc["Doc_AllFiles"]} (*.*)|*.*";
        var picked = dialogs.PickFiles(filter, multiple: false, NearestExistingFolder(oldPath));
        if (picked.Count == 0) return false;
        try
        {
            var facts = await files.GetFactsAsync(picked[0], readContent: true);
            await store.RelocateAsync(id, facts);
            notifications.Show(loc.Format("Doc_Relocated", PathRules.DisplayName(picked[0])), NotificationKind.Success);
            messenger.Send(new DocumentsChangedMessage());
            return true;
        }
        catch (InvalidOperationException)
        {
            notifications.Show(loc["Doc_ErrAlreadyInLibrary"], NotificationKind.Warning);
            return false;
        }
    }

    private static string? NearestExistingFolder(string path)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(dir)) return dir;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { }
        return null;
    }

    public async Task RemoveFromLibraryAsync(IReadOnlyCollection<int> ids, string description)
    {
        if (ids.Count == 0) return;
        if (!dialogs.Confirm(loc.Format("Doc_ConfirmRemove", description), loc["Doc_RemoveFromArcadia"])) return;
        await store.RemoveAsync(ids);
        notifications.Show(loc.Format("Doc_Removed", ids.Count), NotificationKind.Success);
        messenger.Send(new DocumentsChangedMessage());
        messenger.Send(new ProjectsChangedMessage());
    }

    public async Task RemoveFromProjectAsync(int documentId, int projectId, string name)
    {
        if (!dialogs.Confirm(loc.Format("Proj_ConfirmRemoveFile", name), loc["Proj_RemoveFromProject"])) return;
        await store.DissociateAsync(documentId, projectId);
        messenger.Send(new DocumentsChangedMessage(projectId));
        messenger.Send(new ProjectsChangedMessage(projectId));
    }

    public async Task AssociateAsync(IReadOnlyCollection<int> documentIds, IReadOnlyList<Arcadia.Core.Projects.ProjectSummary> projects)
    {
        if (documentIds.Count == 0) return;
        var picker = new ProjectPickerViewModel(loc, projects);
        if (!dialogs.Show(picker) || picker.Selected is not { } project) return;
        foreach (var id in documentIds) await store.AssociateAsync(id, project.Id);
        notifications.Show(loc.Format("Doc_Associated", documentIds.Count, project.Name), NotificationKind.Success);
        messenger.Send(new DocumentsChangedMessage(project.Id));
        messenger.Send(new ProjectsChangedMessage(project.Id));
    }

    private bool Start(ProcessStartInfo info, string path)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
            return false;
        }
    }
}

/// <summary>Re-checks whether referenced files still exist (in the background, at most every few minutes).</summary>
public sealed class DocumentAvailabilityService(IDocumentStore store, IFileFactsService files, IMessenger messenger, TimeProvider time, ILogger<DocumentAvailabilityService> logger)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);
    private DateTime _lastFull = DateTime.MinValue;
    private int _running;

    /// <summary>Checks everything (throttled) — called when the Documents or Projects pages open.</summary>
    public Task CheckAllAsync(bool force = false)
    {
        if (!force && time.GetUtcNow().UtcDateTime - _lastFull < MinInterval) return Task.CompletedTask;
        return CheckAsync(null);
    }

    public async Task CheckAsync(IReadOnlyCollection<int>? ids)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var sw = Stopwatch.StartNew();
            var paths = await store.GetPathsAsync(ids);
            int missing = 0;
            foreach (var batch in paths.Chunk(200))
            {
                var facts = await files.CheckAsync(batch.Select(p => p.Path).ToList());
                missing += facts.Count(f => !f.Exists);
                await store.UpdateAvailabilityAsync(batch.Zip(facts, (p, f) => (p.Id, f)).ToList(), time.GetUtcNow().UtcDateTime);
            }
            if (ids == null) _lastFull = time.GetUtcNow().UtcDateTime;
            logger.LogInformation("Checked {Count} document references in {Ms:0} ms ({Missing} unavailable)", paths.Count, sw.Elapsed.TotalMilliseconds, missing);
            messenger.Send(new DocumentsChangedMessage());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Document availability check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}

/// <summary>
/// Watches the folders the user chose (optional). Events are coalesced (editors save files several times),
/// temporary files are ignored, renames follow the file, deletions mark references as missing. A watcher whose
/// drive or share disappears is retried every minute. Only document types are picked up.
/// </summary>
public sealed class DocumentWatcherService(IDocumentStore store, IFileFactsService files, ISettingsService settings, IMessenger messenger, ILogger<DocumentWatcherService> logger) : IDisposable
{
    /// <summary>Gaming Mode / Power Saver / memory pressure: the periodic folder re-scan waits (live watcher events still apply).</summary>
    public static bool PauseScans { get; set; }

    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);
    private readonly Dictionary<int, FileSystemWatcher> _watchers = [];
    private readonly Channel<(WatcherChangeTypes Kind, string Path, string? OldPath, int FolderId)> _events = Channel.CreateUnbounded<(WatcherChangeTypes, string, string?, int)>();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Timer? _retry;

    public bool IsRunning => _cts != null;

    public async Task StartAsync()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));
        _retry = new Timer(_ => { if (!PauseScans) _ = RefreshAsync(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        await RefreshAsync(initialScan: true);
    }

    /// <summary>Starts watchers for new folders, drops removed ones, retries unavailable ones.</summary>
    public async Task RefreshAsync(bool initialScan = false)
    {
        if (_cts == null) return;
        try
        {
            var folders = await store.GetWatchedFoldersAsync();
            var enabled = settings.Current.Documents.WatchEnabled;
            lock (_gate)
            {
                foreach (var id in _watchers.Keys.Where(id => !enabled || folders.All(f => f.Id != id || !f.IsEnabled)).ToList())
                {
                    _watchers[id].Dispose();
                    _watchers.Remove(id);
                }
            }
            if (!enabled) return;
            foreach (var folder in folders.Where(f => f.IsEnabled))
            {
                bool active;
                lock (_gate) active = _watchers.ContainsKey(folder.Id);
                if (active) continue;
                if (!await Task.Run(() => Directory.Exists(folder.Path)).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result))
                {
                    if (folder.LastError == null) await store.SetWatchedFolderStateAsync(folder.Id, true, "unavailable", null);
                    continue; // retried by the timer
                }
                Watch(folder);
                if (initialScan || folder.LastError != null) _ = ReconcileAsync(folder);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refreshing watched folders failed");
        }
    }

    private void Watch(WatchedFolder folder)
    {
        try
        {
            var w = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = folder.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => _events.Writer.TryWrite((WatcherChangeTypes.Created, e.FullPath, null, folder.Id));
            w.Changed += (_, e) => _events.Writer.TryWrite((WatcherChangeTypes.Changed, e.FullPath, null, folder.Id));
            w.Deleted += (_, e) => _events.Writer.TryWrite((WatcherChangeTypes.Deleted, e.FullPath, null, folder.Id));
            w.Renamed += (_, e) => _events.Writer.TryWrite((WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath, folder.Id));
            w.Error += (_, e) =>
            {
                logger.LogWarning(e.GetException(), "Watcher for {Folder} failed; it will be retried", folder.Path);
                lock (_gate)
                {
                    if (_watchers.Remove(folder.Id, out var dead)) dead.Dispose();
                }
                _ = store.SetWatchedFolderStateAsync(folder.Id, true, e.GetException().Message, null);
            };
            w.EnableRaisingEvents = true;
            lock (_gate) _watchers[folder.Id] = w;
            if (folder.LastError != null) _ = store.SetWatchedFolderStateAsync(folder.Id, true, null, null);
            logger.LogInformation("Watching {Folder}", folder.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "Cannot watch {Folder}", folder.Path);
            _ = store.SetWatchedFolderStateAsync(folder.Id, true, ex.Message, null);
        }
    }

    /// <summary>Catches up with changes made while Arcadia was closed.</summary>
    private async Task ReconcileAsync(WatchedFolder folder)
    {
        try
        {
            var found = await files.ScanFolderAsync(folder.Path, folder.IncludeSubfolders, Wanted, null);
            foreach (var chunk in found.Chunk(DocumentActions.ImportChunk)) await store.AddAsync(chunk, null, folder.Id);
            var known = await store.GetPathsAsync();
            var present = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, path) in known.Where(k => PathRules.IsUnder(k.Path, folder.Path) && !present.Contains(k.Path) && Wanted(k.Path)))
                if (!File.Exists(path) && !Directory.Exists(path)) await store.MarkMissingByPathAsync(path);
            await store.SetWatchedFolderStateAsync(folder.Id, true, null, DateTime.UtcNow);
            messenger.Send(new DocumentsChangedMessage());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Scanning watched folder {Folder} failed", folder.Path);
        }
    }

    private static bool Wanted(string path) =>
        FileKinds.IndexedByDefault.Contains(Path.GetExtension(path)) && !FileKinds.IsTemporary(Path.GetFileName(path));

    private async Task PumpAsync(CancellationToken ct)
    {
        // Coalesce: keep the latest event per path until the folder has been quiet for a moment.
        var pending = new Dictionary<string, (WatcherChangeTypes Kind, string? OldPath, int FolderId)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (pending.Count == 0) await _events.Reader.WaitToReadAsync(ct);
                while (_events.Reader.TryRead(out var e))
                {
                    if (e.Kind == WatcherChangeTypes.Renamed && e.OldPath != null) pending.Remove(e.OldPath);
                    pending[e.Path] = pending.TryGetValue(e.Path, out var prev) && prev.Kind == WatcherChangeTypes.Renamed && e.Kind == WatcherChangeTypes.Changed
                        ? prev
                        : (e.Kind, e.OldPath, e.FolderId);
                }
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quiet.CancelAfter(Quiet);
                try
                {
                    if (await _events.Reader.WaitToReadAsync(quiet.Token)) continue; // more events: keep collecting
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                var batch = pending.ToList();
                pending.Clear();
                bool changed = false;
                foreach (var (path, (kind, oldPath, folderId)) in batch)
                    changed |= await ApplyAsync(kind, path, oldPath, folderId);
                if (changed) messenger.Send(new DocumentsChangedMessage());
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ApplyAsync(WatcherChangeTypes kind, string path, string? oldPath, int folderId)
    {
        try
        {
            switch (kind)
            {
                case WatcherChangeTypes.Deleted:
                    if (!Wanted(path)) return false;
                    await store.MarkMissingByPathAsync(path);
                    return true;
                case WatcherChangeTypes.Renamed when oldPath != null:
                    var renamed = await files.GetFactsAsync(path, readContent: true);
                    if (await store.FindByPathAsync(oldPath) != null) { await store.RenamePathAsync(oldPath, renamed); return true; }
                    if (!Wanted(path) || !renamed.Exists || renamed.IsFolder) return false;
                    await store.AddAsync([renamed], null, folderId);
                    return true;
                default:
                    if (!Wanted(path)) return false;
                    var facts = await files.GetFactsAsync(path, readContent: true);
                    if (!facts.Exists || facts.IsFolder) return false;
                    var existing = await store.FindByPathAsync(path);
                    if (existing == null) await store.AddAsync([facts], null, folderId);
                    else await store.UpdateAvailabilityAsync([(existing.Id, facts)], DateTime.UtcNow);
                    return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Applying a watcher change for {Path} failed", path);
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _retry?.Dispose();
        lock (_gate)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }
}
