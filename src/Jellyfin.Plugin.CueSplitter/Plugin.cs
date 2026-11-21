using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.CueSplitter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

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
