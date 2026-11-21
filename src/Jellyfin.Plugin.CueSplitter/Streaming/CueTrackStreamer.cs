using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Produces the bytes of a virtual track as a WAV file: a computed header followed by the PCM
/// samples of the track, decoded on the fly from the original image with ffmpeg.
/// Any byte range can be produced, which gives exact seeking.
/// </summary>
public class CueTrackStreamer
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<CueTrackStreamer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueTrackStreamer"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder (gives the ffmpeg path).</param>
    /// <param name="logger">Logger.</param>
    public CueTrackStreamer(IMediaEncoder mediaEncoder, ILogger<CueTrackStreamer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the total size of the WAV representation of a track.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>Size in bytes.</returns>
    public static long GetTotalLength(CueTrackPlan track)
        => WavHeader.GetHeaderLength(track.Format) + (track.SampleCount * track.Format.BlockAlign);

    /// <summary>
    /// Writes a byte range of the WAV representation of a track.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="range">The byte range.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task WriteRangeAsync(CueTrackPlan track, ByteRange range, Stream output, CancellationToken cancellationToken)
    {
        var format = track.Format;
        var header = WavHeader.Build(format, track.SampleCount);
        var position = range.Start;
        var remaining = range.Length;

        // 1. Header part.
        if (position < header.Length)
        {
            var count = (int)Math.Min(header.Length - position, remaining);
            await output.WriteAsync(header.AsMemory((int)position, count), cancellationToken).ConfigureAwait(false);
            position += count;
            remaining -= count;
        }

        if (remaining <= 0)
        {
            return;
        }

        // 2. PCM part: decode from the first sample that touches the range.
        var dataOffset = position - header.Length;
        var firstSample = dataOffset / format.BlockAlign;
        var skipBytes = (int)(dataOffset % format.BlockAlign);
        var samplesNeeded = ((skipBytes + remaining) + format.BlockAlign - 1) / format.BlockAlign;
        var absoluteStart = track.StartSample + firstSample;

        await DecodeAsync(track.SourcePath, format, absoluteStart, samplesNeeded, skipBytes, remaining, output, cancellationToken).ConfigureAwait(false);
    }

    private async Task DecodeAsync(
        string sourcePath,
        AudioFormat format,
        long startSample,
        long sampleCount,
        int skipBytes,
        long bytesToWrite,
        Stream output,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in BuildArguments(sourcePath, format, startSample, sampleCount))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = process.StandardOutput.BaseStream;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long written = 0;
        try
        {
            // Skip the bytes of the first sample that precede the requested range.
            var toSkip = skipBytes;
            while (toSkip > 0)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(0, Math.Min(toSkip, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                toSkip -= read;
            }

            while (written < bytesToWrite)
            {
                var want = (int)Math.Min(buffer.Length, bytesToWrite - written);
                var read = await stdout.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }

            // The advertised length must be honoured: pad with silence if the decoder ended early.
            if (written < bytesToWrite)
            {
                _logger.LogDebug("Decoder for {Source} ended early, padding {Bytes} byte(s) of silence", sourcePath, bytesToWrite - written);
                Array.Clear(buffer);
                while (written < bytesToWrite)
                {
                    var count = (int)Math.Min(buffer.Length, bytesToWrite - written);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    written += count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (process.WaitForExit(5000) && process.ExitCode != 0 && written < bytesToWrite)
            {
                _logger.LogWarning("ffmpeg failed on {Source}: {Error}", sourcePath, await stderrTask.ConfigureAwait(false));
            }
        }
    }

    /// <summary>
    /// Builds the ffmpeg arguments that decode <paramref name="sampleCount"/> samples starting at
    /// <paramref name="startSample"/> to raw PCM on stdout.
    /// </summary>
    /// <param name="sourcePath">Source audio file.</param>
    /// <param name="format">Source format.</param>
    /// <param name="startSample">First sample.</param>
    /// <param name="sampleCount">Number of samples.</param>
    /// <returns>Arguments.</returns>
    internal static string[] BuildArguments(string sourcePath, AudioFormat format, long startSample, long sampleCount)
    {
        var args = new System.Collections.Generic.List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        if (startSample > 0)
        {
            // Input seeking with accurate_seek (default) is sample-exact for lossless sources.
            args.Add("-ss");
            args.Add(FormatSeconds((decimal)startSample / format.SampleRate));
        }

        args.AddRange(new[]
        {
            "-i", sourcePath,
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", string.Create(CultureInfo.InvariantCulture, $"atrim=end_sample={sampleCount}"),
            "-ac", format.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", format.PcmCodec,
            "-f", format.RawFormat,
            "pipe:1"
        });
        return args.ToArray();
    }

    private static string FormatSeconds(decimal seconds) => Math.Round(seconds, 6, MidpointRounding.AwayFromZero).ToString("0.000000", CultureInfo.InvariantCulture);
}
