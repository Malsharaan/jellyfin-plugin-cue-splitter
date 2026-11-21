using System;
using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CueSplitter.Cue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Hides from the library the audio images that are described by a usable CUE sheet,
/// so that only the virtual tracks appear.
/// </summary>
public class CueSourceIgnoreRule : IResolverIgnoreRule
{
    private readonly CueLibraryService _cueLibrary;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CueSourceIgnoreRule> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueSourceIgnoreRule"/> class.
    /// </summary>
    /// <param name="cueLibrary">CUE library service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public CueSourceIgnoreRule(CueLibraryService cueLibrary, ILibraryManager libraryManager, ILogger<CueSourceIgnoreRule> logger)
    {
        _cueLibrary = cueLibrary;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        if (fileInfo.IsDirectory || !(Plugin.Instance?.Configuration.HideSourceFiles ?? false))
        {
            return false;
        }

        var extension = Path.GetExtension(fileInfo.FullName);
        if (string.Equals(extension, ".cue", StringComparison.OrdinalIgnoreCase)
            || !CueLibraryService.AudioExtensions.Contains(extension))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(fileInfo.FullName);
            if (string.IsNullOrEmpty(directory)
                || !_cueLibrary.GetConsumedSourceFiles(directory).Contains(fileInfo.FullName))
            {
                return false;
            }

            // Only act inside music libraries (or when the library type cannot be determined).
            if (parent is not null)
            {
                var contentType = _libraryManager.GetContentType(parent);
                if (contentType.HasValue && contentType.Value != CollectionType.music)
                {
                    return false;
                }
            }

            _logger.LogDebug("Hiding {Path}: described by a CUE sheet", fileInfo.FullName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating {Path}", fileInfo.FullName);
            return false;
        }
    }
}
