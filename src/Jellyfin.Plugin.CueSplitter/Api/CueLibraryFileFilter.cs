using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Library;
using Jellyfin.Plugin.CueSplitter.Streaming;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Api;

/// <summary>
/// Serves virtual CUE tracks on Jellyfin's "original file" routes, <c>GET /Items/{id}/File</c> and
/// <c>GET /Items/{id}/Download</c>. Jellyfin answers them with the file found at the item path,
/// which for a virtual track is the internal stream URL, not a file. Apps that play through these
/// routes (e.g. Finamp) or download tracks would fail with a "source error".
/// The filter runs after Jellyfin's authorization (authentication, download permission) and only
/// replaces the response for virtual tracks; every other item goes through Jellyfin unchanged.
/// </summary>
public class CueLibraryFileFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly CueLibraryService _cueLibrary;
    private readonly CueTrackStreamer _streamer;
    private readonly ILogger<CueLibraryFileFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueLibraryFileFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="streamer">Track streamer.</param>
    /// <param name="logger">Logger.</param>
    public CueLibraryFileFilter(ILibraryManager libraryManager, CueLibraryService cueLibrary, CueTrackStreamer streamer, ILogger<CueLibraryFileFilter> logger)
    {
        _libraryManager = libraryManager;
        _cueLibrary = cueLibrary;
        _streamer = streamer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!IsOriginalFileAction(context, out var isDownload)
            || !context.ActionArguments.TryGetValue("itemId", out var idArgument)
            || idArgument is not Guid itemId)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null
            || !CueTrackItemFactory.IsCueTrack(item)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.CueKey, out var cuePath)
            || !item.ProviderIds.TryGetValue(CueTrackItemFactory.TrackKey, out var trackKey))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var track = _cueLibrary.GetTrack(cuePath, trackKey);
        if (track is null)
        {
            _logger.LogWarning("Track {Key} of {Cue} is no longer available", trackKey, cuePath);
            context.Result = new NotFoundResult();
            return;
        }

        var fileName = isDownload ? CueTrackHttpResponder.GetDownloadFileName(track) : null;
        await CueTrackHttpResponder.WriteAsync(context.HttpContext, _streamer, track, fileName, _logger).ConfigureAwait(false);
        context.Result = new EmptyResult();
    }

    /// <summary>
    /// Gets a value indicating whether the action is Jellyfin's GetFile / GetDownload.
    /// </summary>
    private static bool IsOriginalFileAction(ActionExecutingContext context, out bool isDownload)
    {
        isDownload = false;
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || !string.Equals(action.ControllerName, "Library", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(action.ActionName, "GetFile", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(action.ActionName, "GetDownload", StringComparison.Ordinal))
        {
            isDownload = true;
            return true;
        }

        return false;
    }
}
