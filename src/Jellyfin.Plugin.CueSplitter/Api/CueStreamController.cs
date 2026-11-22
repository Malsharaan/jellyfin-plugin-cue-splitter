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
/// Internal endpoint serving the virtual tracks as seekable FLAC or WAV streams.
/// It is the "path" of every virtual track: Jellyfin's own streaming pipeline (direct stream
/// proxy, transcoding, HLS...) reads from it, so every client gets a regular audio stream.
/// Only the server itself may call it, with the secret stream key.
/// </summary>
[ApiController]
[Route("CueSplitter")]
public class CueStreamController : ControllerBase
{
    private readonly CueTrackServer _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueStreamController"/> class.
    /// </summary>
    /// <param name="server">Track server.</param>
    public CueStreamController(CueTrackServer server)
    {
        _server = server;
    }

    /// <summary>
    /// Streams a virtual track (supports HEAD and byte ranges). The extension selects the format,
    /// so item paths written by an older version (stream.wav) keep working.
    /// </summary>
    /// <param name="itemId">The track item id.</param>
    /// <param name="extension">"flac" or "wav".</param>
    /// <param name="key">The secret stream key.</param>
    /// <returns>The stream.</returns>
    [HttpGet("Tracks/{itemId}/stream.{extension}")]
    [HttpHead("Tracks/{itemId}/stream.{extension}")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> GetTrackStream([FromRoute] Guid itemId, [FromRoute] string extension, [FromQuery] string? key)
    {
        if (!IsLocalRequest(HttpContext) || !IsValidKey(key))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        TrackFormat format;
        if (string.Equals(extension, "flac", StringComparison.OrdinalIgnoreCase))
        {
            format = TrackFormat.Flac;
        }
        else if (string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
        {
            format = TrackFormat.Wav;
        }
        else
        {
            return NotFound();
        }

        return await _server.ServeAsync(HttpContext, itemId, format, download: false).ConfigureAwait(false);
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
