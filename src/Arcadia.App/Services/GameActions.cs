using Arcadia.App.Dialogs;
using Arcadia.App.Messages;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Media;
using Arcadia.Core.Metadata;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// The user-facing game workflows (add/edit, log session, launch, import, delete), shared by Home and the Games module.
/// Every successful change broadcasts <see cref="LibraryChangedMessage"/>.
/// </summary>
public sealed class GameActions(
    IGameCatalog catalog,
    IMediaStore media,
    IDialogService dialogs,
    IShellLauncher launcher,
    INotificationService notifications,
    ILocalizer loc,
    TimeProvider time,
    IMessenger messenger,
    SessionTrackingService tracking,
    MetadataService metadata,
    ILogger<GameActions> logger)
{
    public string? ResolveMedia(string? stored) => media.Resolve(stored);

    /// <summary>Shows the editor for a new (null) or existing game. Returns the saved game's id, or null.</summary>
    public async Task<int?> EditAsync(int? gameId)
    {
        Game? existing = null;
        if (gameId is { } id)
        {
            existing = await catalog.GetGameDetailAsync(id);
            if (existing == null) return null;
        }

        var editor = new GameEditorViewModel(existing, loc, dialogs, media.Resolve);
        if (!dialogs.Show(editor) || editor.Result is not { } game) return null;

        var imported = new List<string?>();
        try
        {
            if (editor.NewCoverSource != null)
            {
                var cover = await media.ImportAsync(editor.NewCoverSource, MediaKind.Cover);
                game.CoverImagePath = cover.Path;
                game.CoverThumbnailPath = cover.ThumbnailPath;
                imported.AddRange([cover.Path, cover.ThumbnailPath]);
            }
            if (editor.NewHeroSource != null)
            {
                var hero = await media.ImportAsync(editor.NewHeroSource, MediaKind.Hero);
                game.HeroImagePath = hero.Path;
                imported.AddRange([hero.Path, hero.ThumbnailPath]);
                media.Delete([hero.ThumbnailPath]); // banners are always shown large
            }

            var savedId = await catalog.SaveGameAsync(game);
            media.Delete(editor.ObsoleteMedia());
            messenger.Send(new LibraryChangedMessage(savedId));
            notifications.Show(loc.Format("Toast_GameSaved", game.Title), NotificationKind.Success);
            return savedId;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            logger.LogError(ex, "Saving game {Title} failed", game.Title);
            media.Delete(imported);
            notifications.Show(loc["Toast_SaveFailed"], NotificationKind.Warning);
            return null;
        }
    }

    public bool OnlineMetadataAvailable => metadata.IsAvailable;

    /// <summary>"Find details &amp; art": look the game up on IGDB/SteamGridDB and apply what the user picks.</summary>
    public async Task<bool> FindOnlineAsync(int gameId)
    {
        if (!metadata.IsAvailable)
        {
            notifications.Show(loc["Meta_ErrNotConfigured"], NotificationKind.Warning);
            return false;
        }
        var game = await catalog.GetGameDetailAsync(gameId);
        if (game == null) return false;

        var dialog = new FindOnlineViewModel(game, metadata, loc);
        if (!dialogs.Show(dialog) || dialog.Result is not { } selection) return false;

        notifications.Show(loc.Format("Meta_Applying", game.Title));
        var imported = new List<string?>();
        var obsolete = new List<string?>();
        try
        {
            if (selection.ApplyDetails && selection.Details is { } d)
            {
                if (!string.IsNullOrWhiteSpace(d.Summary)) game.Description = d.Summary;
                if (d.Developers.Count > 0) game.Developer = string.Join(", ", d.Developers.Take(2));
                if (d.Publishers.Count > 0) game.Publisher = string.Join(", ", d.Publishers.Take(2));
                game.ReleaseDate = d.ReleaseDate ?? game.ReleaseDate;
                game.Tags = string.Join(",", game.TagList.Concat(d.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Take(6));
                if (string.IsNullOrWhiteSpace(game.LaunchTarget) && d.SteamAppId is { } appId) game.LaunchTarget = $"steam://rungameid/{appId}";
            }
            if (selection.Details is { } ids) game.IgdbId = ids.Id;
            game.SteamGridDbId = selection.SteamGridDbId ?? game.SteamGridDbId;

            // Download all chosen images in parallel, then assign them.
            var downloads = await Task.WhenAll(selection.Art.Select(async a =>
            {
                var mediaKind = a.Key switch { ArtKind.BoxArt => MediaKind.BoxArt, ArtKind.Capsule => MediaKind.Cover, ArtKind.Hero => MediaKind.Hero, _ => MediaKind.Logo };
                return (kind: a.Key, file: await metadata.DownloadAsync(a.Value.Url, mediaKind));
            }));
            foreach (var (kind, file) in downloads)
            {                imported.AddRange([file.Path, file.ThumbnailPath]);
                switch (kind)
                {
                    case ArtKind.BoxArt: obsolete.Add(game.BoxArtPath); game.BoxArtPath = file.Path; media.Delete([file.ThumbnailPath]); break;
                    case ArtKind.Capsule: obsolete.AddRange([game.CoverImagePath, game.CoverThumbnailPath]); game.CoverImagePath = file.Path; game.CoverThumbnailPath = file.ThumbnailPath; break;
                    case ArtKind.Hero: obsolete.Add(game.HeroImagePath); game.HeroImagePath = file.Path; media.Delete([file.ThumbnailPath]); break;
                    default: obsolete.Add(game.LogoImagePath); game.LogoImagePath = file.Path; break;
                }
            }

            await catalog.SaveGameAsync(game);
            media.Delete(obsolete);
            messenger.Send(new LibraryChangedMessage(game.Id));
            notifications.Show(loc.Format("Meta_Applied", game.Title), NotificationKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or NotSupportedException
                                       or InvalidOperationException or ArgumentException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            logger.LogError(ex, "Applying online metadata to {Title} failed", game.Title);
            media.Delete(imported);
            notifications.Show(loc["Toast_SaveFailed"], NotificationKind.Warning);
            return false;
        }
    }

    public async Task<bool> LogSessionAsync(int gameId)
    {
        var game = await catalog.GetGameDetailAsync(gameId);
        if (game == null) return false;

        var dialog = new LogSessionViewModel(game, loc, time);
        if (!dialogs.Show(dialog)) return false;

        try
        {
            await catalog.LogSessionAsync(new SessionLog(
                game.Id,
                dialog.StartedAtUtc,
                dialog.DurationMinutes,
                dialog.Note,
                dialog.UpdateProgress ? (int)Math.Round(dialog.Completion) : null,
                dialog.Objective));
            messenger.Send(new LibraryChangedMessage(game.Id));
            notifications.Show(loc.Format("Session_Logged", RelativeTime.Hours(dialog.DurationMinutes / 60.0, loc), game.Title), NotificationKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            logger.LogError(ex, "Logging a session for {GameId} failed", game.Id);
            notifications.Show(loc["Toast_SaveFailed"], NotificationKind.Warning);
            return false;
        }
    }

    /// <summary>"Continue Playing": opens the game's launch target through the shell.</summary>
    public void Launch(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !LaunchTargets.IsAllowed(game.LaunchTarget))
        {
            notifications.Show(loc.Format("Toast_NoLaunchTarget", game.Title));
            return;
        }

        if (launcher.Launch(game.LaunchTarget))
        {
            notifications.Show(loc.Format("Toast_Launching", game.Title));
            _ = tracking.NotifyLaunchedAsync(game);
        }
        else
            notifications.Show(loc.Format("Toast_LaunchFailed", game.Title), NotificationKind.Warning);
    }

    public async Task LaunchAsync(int gameId)
    {
        var game = await catalog.GetGameDetailAsync(gameId);
        if (game != null) Launch(game);
    }

    /// <summary>Picks image files and imports them as screenshots of the game. Returns the number imported.</summary>
    public Task<int> ImportScreenshotsAsync(int gameId)
    {
        var files = dialogs.PickImages(multiple: true);
        return files.Count == 0 ? Task.FromResult(0) : ImportScreenshotFilesAsync(gameId, files);
    }

    /// <summary>True for files that can be imported as screenshots (used to accept drag-and-drop).</summary>
    public static bool IsImportableImage(string path) =>
        IMediaStore.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path);

    /// <summary>Imports the given image files (e.g. dropped onto a page). Files already imported are skipped.</summary>
    public async Task<int> ImportScreenshotFilesAsync(int gameId, IEnumerable<string> paths)
    {
        var files = paths.Where(IsImportableImage).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return 0;

        int imported = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                if (await catalog.HasScreenshotFromSourceAsync(file)) continue;
                var result = await media.ImportAsync(file, MediaKind.Screenshot);
                var captured = File.GetLastWriteTimeUtc(file);
                await catalog.AddScreenshotAsync(new Screenshot
                {
                    GameId = gameId,
                    Title = Path.GetFileNameWithoutExtension(file),
                    ImagePath = result.Path,
                    ThumbnailPath = result.ThumbnailPath,
                    SourcePath = file,
                    CapturedAtUtc = captured.Year > 2000 ? captured : time.GetUtcNow().UtcDateTime,
                    ArtworkSeed = Random.Shared.Next(1, 10_000),
                });
                imported++;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                logger.LogWarning(ex, "Importing screenshot {File} failed", file);
                failed++;
            }
        }

        if (imported > 0)
        {
            messenger.Send(new LibraryChangedMessage(gameId));
            notifications.Show(loc.Format("Shot_Imported", imported), NotificationKind.Success);
        }
        if (failed > 0) notifications.Show(loc["Shot_ImportFailed"], NotificationKind.Warning);
        return imported;
    }

    public async Task<bool> EditScreenshotAsync(Screenshot shot)
    {
        var editor = new ScreenshotEditorViewModel(shot, media.Resolve(shot.ImagePath), loc);
        if (!dialogs.Show(editor)) return false;
        shot.Title = editor.ShotTitle;
        shot.Location = editor.Location;
        shot.Note = editor.Note;
        shot.Tags = editor.Tags;
        await catalog.UpdateScreenshotAsync(shot);
        messenger.Send(new LibraryChangedMessage(shot.GameId));
        return true;
    }

    public async Task<bool> DeleteScreenshotAsync(Screenshot shot)
    {
        if (!dialogs.Confirm(loc["Confirm_DeleteScreenshot"], loc["Confirm_Delete"])) return false;
        media.Delete(await catalog.DeleteScreenshotAsync(shot.Id));
        messenger.Send(new LibraryChangedMessage(shot.GameId));
        return true;
    }

    public async Task<bool> DeleteGameAsync(int gameId, string title)
    {
        if (!dialogs.Confirm(loc.Format("Confirm_DeleteGame", title), loc["Confirm_Delete"])) return false;
        media.Delete(await catalog.DeleteGameAsync(gameId));
        messenger.Send(new LibraryChangedMessage(gameId, Deleted: true));
        notifications.Show(loc.Format("Toast_GameDeleted", title));
        return true;
    }

    public async Task SetFeaturedAsync(int gameId, string title)
    {
        await catalog.SetFeaturedAsync(gameId);
        messenger.Send(new LibraryChangedMessage(gameId));
        notifications.Show(loc.Format("Toast_Featured", title), NotificationKind.Success);
    }

    public async Task DeleteSessionAsync(GameSession session)
    {
        await catalog.DeleteSessionAsync(session.Id);
        messenger.Send(new LibraryChangedMessage(session.GameId));
    }

    public async Task AddJournalEntryAsync(int gameId, string text)
    {
        await catalog.AddJournalEntryAsync(gameId, text);
        messenger.Send(new LibraryChangedMessage(gameId));
    }

    public async Task SetJournalPinnedAsync(JournalEntry entry, bool pinned)
    {
        await catalog.SetJournalPinnedAsync(entry.Id, pinned);
        messenger.Send(new LibraryChangedMessage(entry.GameId));
    }

    public async Task DeleteJournalEntryAsync(JournalEntry entry)
    {
        await catalog.DeleteJournalEntryAsync(entry.Id);
        messenger.Send(new LibraryChangedMessage(entry.GameId));
    }
}
