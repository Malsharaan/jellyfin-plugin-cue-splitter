using System;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// PCM format of a decoded source file, and its length in samples.
/// </summary>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Number of channels.</param>
/// <param name="BitsPerSample">Output bit depth (16, 24 or 32).</param>
/// <param name="TotalSamples">Total number of samples (per channel) in the source.</param>
/// <param name="SourceCodec">Codec of the source file (flac, ape, ...).</param>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, long TotalSamples, string SourceCodec)
{
    /// <summary>Gets the size in bytes of one sample frame (all channels).</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    /// <summary>Gets the byte rate of the PCM stream.</summary>
    public long ByteRate => (long)SampleRate * BlockAlign;

    /// <summary>Gets the ffmpeg PCM codec name for this bit depth.</summary>
    public string PcmCodec => BitsPerSample switch
    {
        24 => "pcm_s24le",
        32 => "pcm_s32le",
        _ => "pcm_s16le"
    };

    /// <summary>Gets the ffmpeg raw muxer name for this bit depth.</summary>
    public string RawFormat => BitsPerSample switch
    {
        24 => "s24le",
        32 => "s32le",
        _ => "s16le"
    };

    /// <summary>
    /// Converts a position in CD frames (1/75 s) into a sample index.
    /// </summary>
    /// <param name="frames">Position in frames.</param>
    /// <returns>Sample index.</returns>
    public long FramesToSamples(long frames) => frames * SampleRate / Cue.CueParser.FramesPerSecond;

    /// <summary>
    /// Converts a number of samples into .NET ticks (100 ns).
    /// </summary>
    /// <param name="samples">Number of samples.</param>
    /// <returns>Ticks.</returns>
    public long SamplesToTicks(long samples) => (long)Math.Round(samples * (double)TimeSpan.TicksPerSecond / SampleRate);
}
