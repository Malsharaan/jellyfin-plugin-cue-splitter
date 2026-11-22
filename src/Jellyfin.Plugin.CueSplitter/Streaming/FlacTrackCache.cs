using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CueSplitter.Cue;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Encodes virtual tracks to FLAC once and keeps them in a size-capped disk cache, so they can be
/// served as regular, seekable FLAC files (direct play in virtually every client).
/// The original album image is only read; cache files live under Jellyfin's cache directory.
/// </summary>
public class FlacTrackCache
{
    private const long DefaultMaxBytes = 2048L * 1024 * 1024;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FlacTrackCache> _logger;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pending = new(StringComparer.Ordinal);
    private int _evicting;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlacTrackCache"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder (gives the ffmpeg path).</param>
    /// <param name="appPaths">Application paths (cache directory).</param>
    /// <param name="logger">Logger.</param>
    public FlacTrackCache(IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<FlacTrackCache> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _directory = Path.Combine(appPaths.CachePath, "cuesplitter");
    }

    /// <summary>
    /// Gets the cache directory.
    /// </summary>
    public string Directory => _directory;

    /// <summary>
    /// Gets the FLAC file of a track, encoding it first if needed.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="tags">Tags to embed (title, artist, album, track...).</param>
    /// <param name="cancellationToken">Cancels the wait (not a shared encode in progress).</param>
    /// <returns>The path of the FLAC file, or null if encoding failed.</returns>
    public async Task<string?> GetOrCreateAsync(CueTrackPlan track, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, GetCacheKey(track) + ".flac");
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        // One encode per track, shared by concurrent requests (seek, HEAD, prefetch...).
        var lazy = _pending.GetOrAdd(path, p => new Lazy<Task<string?>>(() => EncodeAsync(track, tags, p)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                _pending.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(path, lazy));
            }
        }
    }

    /// <summary>
    /// Encodes a track in the background if it is not cached yet (e.g. the next track of an album).
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="tags">Tags to embed.</param>
    public void Prefetch(CueTrackPlan track, IReadOnlyDictionary<string, string> tags)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetOrCreateAsync(track, tags, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prefetch of track {Key} failed", track.Key);
            }
        });
    }

    /// <summary>
    /// Computes the cache key of a track: it changes whenever the audio it represents changes
    /// (source file replaced, new boundaries...).
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>A hex key.</returns>
    internal static string GetCacheKey(CueTrackPlan track)
    {
        var info = new FileInfo(track.SourcePath);
        var raw = string.Join(
            '|',
            "v1",
            track.SourcePath.ToLowerInvariant(),
            info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0",
            info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "0",
            track.StartSample.ToString(CultureInfo.InvariantCulture),
            track.EndSample.ToString(CultureInfo.InvariantCulture),
            track.Format.SampleRate.ToString(CultureInfo.InvariantCulture),
            track.Format.Channels.ToString(CultureInfo.InvariantCulture),
            track.Format.BitsPerSample.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32].ToLowerInvariant();
    }

    /// <summary>
    /// Builds the ffmpeg arguments that extract exactly the samples of a track to FLAC.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="tags">Tags to embed.</param>
    /// <param name="outputPath">Output file.</param>
    /// <returns>Arguments.</returns>
    internal static string[] BuildArguments(CueTrackPlan track, IReadOnlyDictionary<string, string> tags, string outputPath)
    {
        var format = track.Format;
        var count = track.SampleCount.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        if (track.StartSample > 0)
        {
            // Input seeking is sample-exact for lossless sources (accurate_seek).
            args.Add("-ss");
            args.Add(Math.Round((decimal)track.StartSample / format.SampleRate, 6, MidpointRounding.AwayFromZero).ToString("0.000000", CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-i", track.SourcePath,
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-map_metadata", "-1",

            // Exactly SampleCount samples: cut at the end, pad with silence if the decoder stops early.
            "-af", string.Create(CultureInfo.InvariantCulture, $"atrim=end_sample={count},apad=whole_len={count}"),
            "-ac", format.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "flac",
            "-compression_level", "5"
        });

        if (format.BitsPerSample > 16)
        {
            args.AddRange(new[] { "-sample_fmt", "s32", "-bits_per_raw_sample", format.BitsPerSample.ToString(CultureInfo.InvariantCulture) });
        }
        else
        {
            args.AddRange(new[] { "-sample_fmt", "s16" });
        }

        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag.Value))
            {
                args.Add("-metadata");
                args.Add(tag.Key + "=" + tag.Value);
            }
        }

        args.AddRange(new[] { "-f", "flac", "-y", outputPath });
        return args.ToArray();
    }

    /// <summary>
    /// Selects the cache files to delete so that the total size fits in the budget,
    /// least recently used first.
    /// </summary>
    /// <param name="files">Cache files (path, size, last use).</param>
    /// <param name="maxBytes">Size budget.</param>
    /// <param name="keep">A path that must not be evicted (the file just created).</param>
    /// <returns>Paths to delete.</returns>
    internal static IReadOnlyList<string> SelectEvictions(IEnumerable<(string Path, long Size, DateTime LastUse)> files, long maxBytes, string? keep)
    {
        var list = files.OrderBy(f => f.LastUse).ToList();
        var total = list.Sum(f => f.Size);
        var result = new List<string>();
        foreach (var file in list)
        {
            if (total <= maxBytes)
            {
                break;
            }

            if (keep is not null && string.Equals(file.Path, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(file.Path);
            total -= file.Size;
        }

        return result;
    }

    private async Task<string?> EncodeAsync(CueTrackPlan track, IReadOnlyDictionary<string, string> tags, string path)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + ".tmp";
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArguments(track, tags, temp))
        {
            psi.ArgumentList.Add(arg);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                _logger.LogError("FLAC encode of track {Key} of {Source} timed out", track.Key, track.SourcePath);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                _logger.LogError("FLAC encode of track {Key} of {Source} failed: {Error}", track.Key, track.SourcePath, await stderr.ConfigureAwait(false));
                return null;
            }

            File.Move(temp, path, true);
            _logger.LogInformation("Encoded track {Key} of {Source} to FLAC in {Ms} ms ({Size:N0} bytes)", track.Key, Path.GetFileName(track.SourcePath), stopwatch.ElapsedMilliseconds, new FileInfo(path).Length);
            Evict(path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FLAC encode of track {Key} of {Source} failed", track.Key, track.SourcePath);
            return null;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void Evict(string justCreated)
    {
        if (Interlocked.Exchange(ref _evicting, 1) == 1)
        {
            return;
        }

        try
        {
            var configured = Plugin.Instance?.Configuration.CacheSizeMb ?? 0;
            var maxBytes = configured > 0 ? configured * 1024L * 1024L : DefaultMaxBytes;
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.flac")
                .Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc))
                .ToList();
            foreach (var victim in SelectEvictions(files, maxBytes, justCreated))
            {
                TryDelete(victim);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cache eviction failed");
        }
        finally
        {
            Interlocked.Exchange(ref _evicting, 0);
        }
    }

    /// <summary>
    /// Marks a cache file as used now (the last write time serves as last-use stamp: access times
    /// are often disabled on Linux file systems).
    /// </summary>
    private void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cannot touch {Path}", path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (being served): it will be evicted next time.
            _logger.LogDebug(ex, "Cannot delete cache file {Path}", path);
        }
    }

    /// <summary>
    /// Deletes every cache file (plugin uninstall).
    /// </summary>
    public void Clear()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory))
        {
            TryDelete(file);
        }
    }
}
