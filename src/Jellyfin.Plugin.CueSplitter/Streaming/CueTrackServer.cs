using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Library;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Serves a virtual track over HTTP, in FLAC (cached file) or WAV (on-the-fly PCM).
/// Shared by the internal stream endpoint and the /Items/{id}/File and /Download routes.
/// </summary>
public class CueTrackServer
{
    private readonly ILibraryManager _libraryManager;
    private readonly CueLibraryService _cueLibrary;
    private readonly CueTrackStreamer _streamer;
    private readonly FlacTrackCache _cache;
    private readonly ILogger<CueTrackServer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueTrackServer"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="streamer">WAV streamer.</param>
    /// <param name="cache">FLAC cache.</param>
    /// <param name="logger">Logger.</param>
    public CueTrackServer(ILibraryManager libraryManager, CueLibraryService cueLibrary, CueTrackStreamer streamer, FlacTrackCache cache, ILogger<CueTrackServer> logger)
    {
        _libraryManager = libraryManager;
        _cueLibrary = cueLibrary;
        _streamer = streamer;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether an item is a virtual CUE track.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>True for a virtual track.</returns>
    public bool IsCueTrack(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item is not null && CueTrackItemFactory.IsCueTrack(item);
    }

    /// <summary>
    /// Serves a track.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="itemId">The virtual track item id.</param>
    /// <param name="format">The format to serve.</param>
    /// <param name="download">True to send it as an attachment.</param>
    /// <returns>The result to execute (the WAV path writes the response itself and returns an empty result).</returns>
    public async Task<IActionResult> ServeAsync(HttpContext context, Guid itemId, TrackFormat format, bool download)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null
            || !CueTrackItemFactory.IsCueTrack(item)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.CueKey, out var cuePath)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.TrackKey, out var trackKey))
        {
            return new NotFoundResult();
        }

        var album = _cueLibrary.GetAlbum(cuePath);
        var track = album?.Tracks.FirstOrDefault(t => string.Equals(t.Key, trackKey, StringComparison.Ordinal));
        if (album is null || track is null)
        {
            _logger.LogWarning("Track {Key} of {Cue} is no longer available", trackKey, cuePath);
            return new NotFoundResult();
        }

        var fileName = download ? GetDownloadFileName(track, format) : null;
        if (format == TrackFormat.Wav)
        {
            await CueTrackHttpResponder.WriteAsync(context, _streamer, track, fileName, _logger).ConfigureAwait(false);
            return new EmptyResult();
        }

        var path = await _cache.GetOrCreateAsync(track, BuildTags(album, track), context.RequestAborted).ConfigureAwait(false);
        if (path is null)
        {
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        // Gapless album playback: have the next track ready before the player asks for it.
        var index = IndexOf(album.Tracks, track);
        if (index >= 0 && index + 1 < album.Tracks.Count)
        {
            _cache.Prefetch(album.Tracks[index + 1], BuildTags(album, album.Tracks[index + 1]));
        }

        return new PhysicalFileResult(path, format.MimeType())
        {
            EnableRangeProcessing = true,
            FileDownloadName = fileName
        };
    }

    /// <summary>
    /// Builds a download file name for a track, e.g. "03 - Running The City.flac".
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="format">The format.</param>
    /// <returns>A file name safe on common file systems.</returns>
    public static string GetDownloadFileName(CueTrackPlan track, TrackFormat format)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{track.TrackNumber:D2} - {track.Title}.{format.Container()}");
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    /// <summary>
    /// Builds the tags embedded in a FLAC track.
    /// </summary>
    /// <param name="album">The album.</param>
    /// <param name="track">The track.</param>
    /// <returns>Vorbis comment tags.</returns>
    internal static IReadOnlyDictionary<string, string> BuildTags(CueAlbum album, CueTrackPlan track)
    {
        var sheet = album.Sheet;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = track.Title,
            ["TRACKNUMBER"] = track.TrackNumber.ToString(CultureInfo.InvariantCulture)
        };
        Add(tags, "ARTIST", track.Performer);
        Add(tags, "ALBUM", sheet.Title);
        Add(tags, "ALBUMARTIST", sheet.Performer);
        Add(tags, "GENRE", sheet.Genre);
        Add(tags, "DATE", sheet.Date);
        Add(tags, "COMPOSER", track.Songwriter);
        if (sheet.DiscNumber.HasValue)
        {
            tags["DISCNUMBER"] = sheet.DiscNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        return tags;
    }

    private static void Add(Dictionary<string, string> tags, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags[key] = value;
        }
    }

    private static int IndexOf(IReadOnlyList<CueTrackPlan> tracks, CueTrackPlan track)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (string.Equals(tracks[i].Key, track.Key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
