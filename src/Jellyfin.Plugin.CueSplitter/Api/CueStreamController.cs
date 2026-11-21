using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Library;
using Jellyfin.Plugin.CueSplitter.Streaming;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Api;

/// <summary>
/// Internal endpoint serving the virtual tracks as seekable WAV streams.
/// It is the "path" of every virtual track: Jellyfin's own streaming pipeline (direct stream
/// proxy, transcoding, HLS...) reads from it, so every client gets a regular audio stream.
/// Only the server itself may call it, with the secret stream key.
/// </summary>
[ApiController]
[Route("CueSplitter")]
public class CueStreamController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly CueLibraryService _cueLibrary;
    private readonly CueTrackStreamer _streamer;
    private readonly ILogger<CueStreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueStreamController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="streamer">Track streamer.</param>
    /// <param name="logger">Logger.</param>
    public CueStreamController(ILibraryManager libraryManager, CueLibraryService cueLibrary, CueTrackStreamer streamer, ILogger<CueStreamController> logger)
    {
        _libraryManager = libraryManager;
        _cueLibrary = cueLibrary;
        _streamer = streamer;
        _logger = logger;
    }

    /// <summary>
    /// Streams a virtual track as WAV (supports HEAD and byte ranges).
    /// </summary>
    /// <param name="itemId">The track item id.</param>
    /// <param name="key">The secret stream key.</param>
    /// <returns>A task.</returns>
    [HttpGet("Tracks/{itemId}/stream.wav")]
    [HttpHead("Tracks/{itemId}/stream.wav")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task GetTrackStream([FromRoute] Guid itemId, [FromQuery] string? key)
    {
        if (!IsLocalRequest(HttpContext) || !IsValidKey(key))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null
            || !CueTrackItemFactory.IsCueTrack(item)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.CueKey, out var cuePath)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.TrackKey, out var trackKey))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var track = _cueLibrary.GetTrack(cuePath, trackKey);
        if (track is null)
        {
            _logger.LogWarning("Track {Key} of {Cue} is no longer available", trackKey, cuePath);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var total = CueTrackStreamer.GetTotalLength(track);
        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = "audio/wav";

        var result = ByteRange.TryParse(Request.Headers.Range.ToString(), total, out var range);
        if (result == RangeParseResult.Unsatisfiable)
        {
            Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            Response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");
            return;
        }

        if (result == RangeParseResult.Partial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{total}");
        }
        else
        {
            Response.StatusCode = StatusCodes.Status200OK;
        }

        Response.ContentLength = range.Length;
        if (HttpMethods.IsHead(Request.Method))
        {
            return;
        }

        try
        {
            await _streamer.WriteRangeAsync(track, range, Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client went away (seek, stop, next track).
        }
        catch (System.IO.IOException ex)
        {
            _logger.LogDebug(ex, "Stream of {Item} interrupted", itemId);
        }
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remote))
        {
            return true;
        }

        var local = context.Connection.LocalIpAddress;
        return local is not null && (remote.Equals(local) || (remote.IsIPv4MappedToIPv6 && remote.MapToIPv4().Equals(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local)));
    }

    private static bool IsValidKey(string? key)
    {
        var expected = Plugin.Instance?.Configuration.StreamKey;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected));
    }
}
