using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CueSplitter.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the audio file referenced by a CUE sheet
    /// is hidden from the library (only the virtual tracks are shown).
    /// </summary>
    public bool HideSourceFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional base URL used by the server to reach its own stream endpoint
    /// (for example "http://127.0.0.1:8096"). Empty means automatic.
    /// </summary>
    public string InternalBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret key embedded in the internal stream URLs.
    /// </summary>
    public string StreamKey { get; set; } = string.Empty;
}
