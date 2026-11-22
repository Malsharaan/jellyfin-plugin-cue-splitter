using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CueSplitter.Configuration;
using Jellyfin.Plugin.CueSplitter.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CueSplitter;

/// <summary>
/// CUE Splitter plugin: exposes the tracks described by a CUE sheet as individual
/// library items, without ever modifying or splitting the original audio image.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("ad5ffba7-aeea-42e5-a9fe-e3f8e8daaf4c");

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="libraryManager">Library manager.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _libraryManager = libraryManager;

        // The stream key protects the internal loopback endpoint; generate it once.
        if (string.IsNullOrWhiteSpace(Configuration.StreamKey))
        {
            Configuration.StreamKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "CUE Splitter";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Shows the tracks of single-file album images (FLAC, APE, WavPack, WAV...) described by a .cue sheet as individual songs, without splitting the original file.";

    /// <summary>
    /// Removes the virtual tracks: Jellyfin only cleans up file-backed items by itself, so without
    /// this they would stay in the library, unplayable, after the plugin is gone.
    /// </summary>
    public override void OnUninstalling()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            HasAnyProviderId = new Dictionary<string, string> { [CueTrackItemFactory.MarkerKey] = "1" },
            Recursive = true
        });
        foreach (var item in items)
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        }

        // FLAC track cache (only files this plugin created, under Jellyfin's cache directory).
        try
        {
            var cacheDir = System.IO.Path.Combine(ApplicationPaths.CachePath, "cuesplitter");
            if (System.IO.Directory.Exists(cacheDir))
            {
                System.IO.Directory.Delete(cacheDir, true);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Best effort: Jellyfin's cache cleanup task removes leftovers eventually.
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }
}
