using System;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// The format virtual tracks are served in.
/// </summary>
public enum TrackFormat
{
    /// <summary>FLAC, encoded once per track and cached on disk.</summary>
    Flac,

    /// <summary>PCM WAV, produced on the fly for any byte range.</summary>
    Wav
}

/// <summary>
/// Helpers for <see cref="TrackFormat"/>.
/// </summary>
public static class TrackFormats
{
    /// <summary>
    /// Gets the configured track format.
    /// </summary>
    /// <returns>The format (FLAC unless the configuration says "Wav").</returns>
    public static TrackFormat Current()
    {
        var value = Plugin.Instance?.Configuration.StreamFormat;
        return string.Equals(value, "Wav", StringComparison.OrdinalIgnoreCase) ? TrackFormat.Wav : TrackFormat.Flac;
    }

    /// <summary>
    /// Gets the file extension / Jellyfin container of a format.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>"flac" or "wav".</returns>
    public static string Container(this TrackFormat format) => format == TrackFormat.Wav ? "wav" : "flac";

    /// <summary>
    /// Gets the MIME type of a format.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>The MIME type.</returns>
    public static string MimeType(this TrackFormat format) => format == TrackFormat.Wav ? "audio/wav" : "audio/flac";
}
