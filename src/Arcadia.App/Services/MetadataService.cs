using Arcadia.Core.Localization;
using Arcadia.Core.Media;
using Arcadia.Core.Metadata;
using Arcadia.Core.Settings;
using Arcadia.UI.Artwork;

namespace Arcadia.App.Services;

/// <summary>Front door to IGDB + SteamGridDB for the UI: availability, friendly errors and art downloads.</summary>
public sealed class MetadataService(IGameInfoProvider info, IGameArtProvider art, ISettingsService settings, IMediaStore media, ILocalizer loc)
{
    public IGameInfoProvider Info => info;
    public IGameArtProvider Art => art;

    public bool OnlineAllowed => settings.Current.Privacy.AllowOnlineMetadata;
    public bool IsAvailable => OnlineAllowed && info.IsConfigured;
    public bool ArtAvailable => OnlineAllowed && art.IsConfigured;

    /// <summary>Downloads a remote image (disk-cached) and imports it into Arcadia's media folder.</summary>
    public async Task<ImportedMedia> DownloadAsync(string url, MediaKind kind, CancellationToken ct = default)
    {
        var file = await ImageCache.DownloadToCacheAsync(url).WaitAsync(ct);
        return await media.ImportAsync(file, kind, ct);
    }

    public string Describe(Exception ex) => ex switch
    {
        MetadataException { Failure: MetadataFailure.NotConfigured } => loc["Meta_ErrNotConfigured"],
        MetadataException { Failure: MetadataFailure.Unauthorized } m => loc.Format("Meta_ErrUnauthorized", m.Message),
        MetadataException { Failure: MetadataFailure.RateLimited } => loc["Meta_ErrRateLimited"],
        MetadataException { Failure: MetadataFailure.Unavailable } => loc["Meta_ErrOffline"],
        _ => loc["Meta_ErrGeneric"],
    };
}
