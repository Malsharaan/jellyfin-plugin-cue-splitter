using System.Collections.Generic;
using Jellyfin.Plugin.CueSplitter.Streaming;

namespace Jellyfin.Plugin.CueSplitter.Cue;

/// <summary>
/// The playable tracks described by one CUE sheet, resolved against the files on disk.
/// </summary>
/// <param name="CuePath">Full path of the CUE sheet.</param>
/// <param name="Sheet">The parsed sheet.</param>
/// <param name="Tracks">The resolved tracks, in order.</param>
public sealed record CueAlbum(string CuePath, CueSheet Sheet, IReadOnlyList<CueTrackPlan> Tracks);

/// <summary>
/// A single virtual track: a sample range of a source audio file.
/// </summary>
/// <param name="TrackNumber">Track number from the sheet.</param>
/// <param name="Key">Stable key of the track inside its sheet (track number, de-duplicated).</param>
/// <param name="Title">Track title.</param>
/// <param name="Performer">Track performer (falls back to the album performer).</param>
/// <param name="Songwriter">Track songwriter / composer.</param>
/// <param name="SourcePath">Full path of the audio file that contains the track.</param>
/// <param name="StartSample">First sample (inclusive).</param>
/// <param name="EndSample">Last sample (exclusive).</param>
/// <param name="Format">PCM format of the source.</param>
public sealed record CueTrackPlan(
    int TrackNumber,
    string Key,
    string Title,
    string? Performer,
    string? Songwriter,
    string SourcePath,
    long StartSample,
    long EndSample,
    AudioFormat Format)
{
    /// <summary>Gets the number of samples of the track.</summary>
    public long SampleCount => EndSample - StartSample;
}
