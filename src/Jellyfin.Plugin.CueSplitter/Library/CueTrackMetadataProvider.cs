using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Re-applies the CUE data when a virtual track is refreshed (e.g. "Refresh metadata" in the UI),
/// so that the track keeps its title, artists and media information.
/// </summary>
public class CueTrackMetadataProvider : ICustomMetadataProvider<AudioItem>
{
    private readonly CueLibraryService _cueLibrary;
    private readonly CueTrackSynchronizer _synchronizer;
    private readonly IMediaStreamRepository _mediaStreamRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueTrackMetadataProvider"/> class.
    /// </summary>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="synchronizer">Synchronizer (gives the internal base URL).</param>
    /// <param name="mediaStreamRepository">Media stream repository.</param>
    public CueTrackMetadataProvider(CueLibraryService cueLibrary, CueTrackSynchronizer synchronizer, IMediaStreamRepository mediaStreamRepository)
    {
        _cueLibrary = cueLibrary;
        _synchronizer = synchronizer;
        _mediaStreamRepository = mediaStreamRepository;
    }

    /// <inheritdoc />
    public string Name => "CUE Splitter";

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(AudioItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!CueTrackItemFactory.IsCueTrack(item)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.CueKey, out var cuePath)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.TrackKey, out var trackKey))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var album = _cueLibrary.GetAlbum(cuePath);
        var track = _cueLibrary.GetTrack(cuePath, trackKey);
        if (album is null || track is null)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var folderAlbums = _cueLibrary.GetAlbumsInDirectory(System.IO.Path.GetDirectoryName(cuePath)!);
        var disc = CueTrackItemFactory.GetDiscNumber(album, folderAlbums);
        var trackFormat = TrackFormats.Current();
        var url = CueTrackItemFactory.BuildStreamUrl(_synchronizer.GetInternalBaseUrl(), item.Id, Plugin.Instance?.Configuration.StreamKey ?? string.Empty, trackFormat);

        // Force re-application on an explicit refresh: the provider may have been preceded by a metadata reset.
        if (options.ReplaceAllMetadata)
        {
            item.ProviderIds.Remove(CueTrackItemFactory.SignatureKey);
        }

        var changed = CueTrackItemFactory.Apply(item, album, track, disc, url, trackFormat);
        _mediaStreamRepository.SaveMediaStreams(item.Id, CueTrackItemFactory.BuildMediaStreams(track.Format, trackFormat), cancellationToken);
        return Task.FromResult(changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None);
    }
}
