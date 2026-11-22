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

    /// <summary>
    /// Gets or sets the format the virtual tracks are served in: "Flac" (default) or "Wav".
    /// FLAC is played directly by virtually every client and uses about half the bandwidth;
    /// each track is encoded once on first play and kept in a disk cache.
    /// WAV needs no cache but some clients (e.g. Wholphin) only play it through transcoding.
    /// </summary>
    public string StreamFormat { get; set; } = "Flac";

    /// <summary>
    /// Gets or sets the maximum size of the FLAC track cache, in megabytes.
    /// </summary>
    public int CacheSizeMb { get; set; } = 2048;
}
