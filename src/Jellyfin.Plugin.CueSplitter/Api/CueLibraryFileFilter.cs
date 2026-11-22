using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Streaming;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

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
    private readonly CueTrackServer _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueLibraryFileFilter"/> class.
    /// </summary>
    /// <param name="server">Track server.</param>
    public CueLibraryFileFilter(CueTrackServer server)
    {
        _server = server;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!IsOriginalFileAction(context, out var isDownload)
            || !context.ActionArguments.TryGetValue("itemId", out var idArgument)
            || idArgument is not Guid itemId
            || !_server.IsCueTrack(itemId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Result = await _server.ServeAsync(context.HttpContext, itemId, TrackFormats.Current(), isDownload).ConfigureAwait(false);
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
