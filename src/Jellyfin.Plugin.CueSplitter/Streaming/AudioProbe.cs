using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Reads the PCM format and exact sample count of an audio file with ffprobe (cached).
/// </summary>
public class AudioProbe
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<AudioProbe> _logger;
    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, AudioFormat Format)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioProbe"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder (gives the ffprobe path).</param>
    /// <param name="logger">Logger.</param>
    public AudioProbe(IMediaEncoder mediaEncoder, ILogger<AudioProbe> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the format of an audio file.
    /// </summary>
    /// <param name="path">Audio file path.</param>
    /// <returns>The format, or null if the file could not be probed.</returns>
    public virtual AudioFormat? GetFormat(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (_cache.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Mtime == info.LastWriteTimeUtc)
        {
            return cached.Format;
        }

        var format = Probe(path);
        if (format is not null)
        {
            _cache[path] = (info.Length, info.LastWriteTimeUtc, format);
        }

        return format;
    }

    private AudioFormat? Probe(string path)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_raw_sample,bits_per_sample,sample_fmt,duration_ts,time_base,duration:format=duration",
            "-of", "json", path
        })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            var json = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                _logger.LogWarning("ffprobe timed out on {Path}", path);
                return null;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffprobe failed on {Path}: {Error}", path, stderrTask.Result);
                return null;
            }

            return ParseProbeOutput(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error probing {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Parses the JSON output of ffprobe.
    /// </summary>
    /// <param name="json">ffprobe JSON output.</param>
    /// <returns>The format, or null if no usable audio stream.</returns>
    internal static AudioFormat? ParseProbeOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return null;
        }

        var stream = streams[0];
        var sampleRate = GetInt(stream, "sample_rate");
        var channels = GetInt(stream, "channels");
        if (sampleRate is null or <= 0 || channels is null or <= 0)
        {
            return null;
        }

        var codec = GetString(stream, "codec_name") ?? "unknown";
        var bits = GetInt(stream, "bits_per_raw_sample") ?? 0;
        if (bits <= 0)
        {
            bits = GetInt(stream, "bits_per_sample") ?? 0;
        }

        var sampleFmt = GetString(stream, "sample_fmt") ?? string.Empty;
        if (bits <= 0)
        {
            bits = sampleFmt.StartsWith("s32", StringComparison.Ordinal) ? 32
                : sampleFmt.StartsWith("s24", StringComparison.Ordinal) ? 24
                : 16;
        }

        // Normalise to a PCM depth we can stream; floats and odd depths go to the next container size.
        bits = bits switch
        {
            <= 16 => 16,
            <= 24 => 24,
            _ => 32
        };
        if (sampleFmt.StartsWith("flt", StringComparison.Ordinal) || sampleFmt.StartsWith("dbl", StringComparison.Ordinal))
        {
            bits = 24;
        }

        long totalSamples = 0;
        var durationTs = GetLong(stream, "duration_ts");
        var timeBase = GetString(stream, "time_base");
        if (durationTs is > 0 && TryParseRational(timeBase, out var num, out var den) && den > 0)
        {
            totalSamples = (long)Math.Round((decimal)durationTs.Value * num * sampleRate.Value / den);
        }

        if (totalSamples <= 0)
        {
            var duration = GetDouble(stream, "duration");
            if (duration is null or <= 0 && doc.RootElement.TryGetProperty("format", out var formatElement))
            {
                duration = GetDouble(formatElement, "duration");
            }

            if (duration is > 0)
            {
                totalSamples = (long)Math.Round(duration.Value * sampleRate.Value);
            }
        }

        if (totalSamples <= 0)
        {
            return null;
        }

        return new AudioFormat(sampleRate.Value, channels.Value, bits, totalSamples, codec);
    }

    private static bool TryParseRational(string? value, out long num, out long den)
    {
        num = 0;
        den = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('/');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out num)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out den);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()) : null;

    private static int? GetInt(JsonElement e, string name)
        => int.TryParse(GetString(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? GetLong(JsonElement e, string name)
        => long.TryParse(GetString(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? GetDouble(JsonElement e, string name)
        => double.TryParse(GetString(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
