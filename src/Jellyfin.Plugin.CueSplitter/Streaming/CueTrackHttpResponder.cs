using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Writes a virtual track to an HTTP response as a seekable WAV file (HEAD and Range support).
/// </summary>
public static class CueTrackHttpResponder
{
    /// <summary>
    /// Serves a track.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="streamer">The track streamer.</param>
    /// <param name="track">The track.</param>
    /// <param name="downloadFileName">When set, the response is sent as an attachment with this file name.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(HttpContext context, CueTrackStreamer streamer, CueTrackPlan track, string? downloadFileName, ILogger logger)
    {
        var request = context.Request;
        var response = context.Response;
        var total = CueTrackStreamer.GetTotalLength(track);

        response.Headers.AcceptRanges = "bytes";
        response.ContentType = "audio/wav";
        if (!string.IsNullOrEmpty(downloadFileName))
        {
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(downloadFileName);
            response.Headers.ContentDisposition = disposition.ToString();
        }

        var result = ByteRange.TryParse(request.Headers.Range.ToString(), total, out var range);
        if (result == RangeParseResult.Unsatisfiable)
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");
            return;
        }

        if (result == RangeParseResult.Partial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{total}");
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        response.ContentLength = range.Length;
        if (HttpMethods.IsHead(request.Method))
        {
            return;
        }

        try
        {
            await streamer.WriteRangeAsync(track, range, response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client went away (seek, stop, next track).
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Stream of track {Key} of {Cue} interrupted", track.Key, track.SourcePath);
        }
    }
}
