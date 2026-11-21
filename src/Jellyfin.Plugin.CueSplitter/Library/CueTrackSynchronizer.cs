using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CueSplitter.Cue;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Keeps the virtual CUE tracks of the music libraries in sync with the sheets on disk.
/// </summary>
public class CueTrackSynchronizer
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly IServerApplicationHost _appHost;
    private readonly IFileSystem _fileSystem;
    private readonly CueLibraryService _cueLibrary;
    private readonly ILogger<CueTrackSynchronizer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CueTrackSynchronizer"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaStreamRepository">Media stream repository.</param>
    /// <param name="appHost">Server application host.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="logger">Logger.</param>
    public CueTrackSynchronizer(
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreamRepository,
        IServerApplicationHost appHost,
        IFileSystem fileSystem,
        CueLibraryService cueLibrary,
        ILogger<CueTrackSynchronizer> logger)
    {
        _libraryManager = libraryManager;
        _mediaStreamRepository = mediaStreamRepository;
        _appHost = appHost;
        _fileSystem = fileSystem;
        _cueLibrary = cueLibrary;
        _logger = logger;
    }

    /// <summary>
    /// Gets the base URL the server uses to reach its own stream endpoint.
    /// </summary>
    /// <returns>The base URL.</returns>
    public string GetInternalBaseUrl()
    {
        var configured = Plugin.Instance?.Configuration.InternalBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return _appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, false).TrimEnd('/');
    }

    /// <summary>
    /// Synchronizes every music library.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncAllInternalAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SyncAllInternalAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var streamKey = Plugin.Instance?.Configuration.StreamKey ?? string.Empty;
        var baseUrl = GetInternalBaseUrl();

        var roots = _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType == CollectionTypeOptions.music)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cueDirectories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var root in roots)
        {
            foreach (var cue in Directory.EnumerateFiles(root, "*.cue", enumeration))
            {
                cueDirectories.Add(Path.GetDirectoryName(cue)!);
            }
        }

        var existing = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            HasAnyProviderId = new Dictionary<string, string> { [CueTrackItemFactory.MarkerKey] = "1" },
            Recursive = true
        }).OfType<AudioItem>().ToDictionary(i => i.Id);

        _logger.LogInformation("CUE sync: {Dirs} folder(s) with CUE sheets, {Existing} existing virtual track(s)", cueDirectories.Count, existing.Count);

        var desired = new HashSet<Guid>();
        var changedParents = new Dictionary<Guid, Folder>();
        var artistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, removed = 0;
        var done = 0;

        foreach (var directory in cueDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(90.0 * done++ / Math.Max(1, cueDirectories.Count));

            var albums = _cueLibrary.GetAlbumsInDirectory(directory);
            if (albums.Count == 0)
            {
                continue;
            }

            if (_libraryManager.FindByPath(directory, true) is not Folder parent)
            {
                _logger.LogInformation("CUE sync: folder {Dir} is not (yet) in the library, skipping", directory);
                continue;
            }

            var toCreate = new List<(AudioItem Item, CueTrackPlan Track)>();
            foreach (var album in albums)
            {
                var disc = CueTrackItemFactory.GetDiscNumber(album, albums);
                foreach (var track in album.Tracks)
                {
                    var id = CueTrackItemFactory.GetItemId(_libraryManager, album.CuePath, track.Key);
                    desired.Add(id);
                    var url = CueTrackItemFactory.BuildStreamUrl(baseUrl, id, streamKey);

                    if (existing.TryGetValue(id, out var item))
                    {
                        var moved = !item.ParentId.Equals(parent.Id);
                        if (moved)
                        {
                            item.SetParent(parent);
                        }

                        if (CueTrackItemFactory.Apply(item, album, track, disc, url) || moved)
                        {
                            await _libraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            _mediaStreamRepository.SaveMediaStreams(item.Id, CueTrackItemFactory.BuildMediaStreams(track.Format), cancellationToken);
                            changedParents[parent.Id] = parent;
                            updated++;
                        }
                    }
                    else
                    {
                        item = new AudioItem { Id = id };
                        CueTrackItemFactory.Apply(item, album, track, disc, url);
                        item.SetParent(parent);
                        toCreate.Add((item, track));
                    }

                    foreach (var name in item.Artists.Concat(item.AlbumArtists))
                    {
                        artistNames.Add(name);
                    }
                }
            }

            if (toCreate.Count > 0)
            {
                _libraryManager.CreateItems(toCreate.Select(t => (BaseItem)t.Item).ToList(), parent, cancellationToken);
                foreach (var (item, track) in toCreate)
                {
                    _mediaStreamRepository.SaveMediaStreams(item.Id, CueTrackItemFactory.BuildMediaStreams(track.Format), cancellationToken);
                }

                created += toCreate.Count;
                changedParents[parent.Id] = parent;
            }
        }

        // Remove the tracks whose sheet disappeared or changed.
        foreach (var stale in existing.Values.Where(i => !desired.Contains(i.Id)))
        {
            var parent = stale.GetParent() as Folder;
            _logger.LogInformation("CUE sync: removing stale track {Name} ({Id})", stale.Name, stale.Id);
            _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false }, true);
            if (parent is not null)
            {
                changedParents[parent.Id] = parent;
            }

            removed++;
        }

        progress.Report(92);

        // Let the albums pick up names / artists from their new tracks.
        foreach (var folder in changedParents.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await folder.RefreshMetadata(
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CUE sync: error refreshing {Folder}", folder.Path);
            }
        }

        progress.Report(96);

        // Post-scan tasks of plugins run after the built-in artist validator: create missing artists ourselves.
        if (created > 0 || updated > 0)
        {
            foreach (var name in artistNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var artist = _libraryManager.GetArtist(name);
                    if (artist is not null && artist.DateLastRefreshed == default)
                    {
                        await artist.RefreshMetadata(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "CUE sync: error creating artist {Artist}", name);
                }
            }
        }

        progress.Report(100);
        _logger.LogInformation("CUE sync done: {Created} created, {Updated} updated, {Removed} removed", created, updated, removed);
    }
}
