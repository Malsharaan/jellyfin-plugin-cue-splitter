using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.CueSplitter.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CueSplitter.Cue;

/// <summary>
/// Turns CUE sheets on disk into playable track plans, with caching keyed on file timestamps.
/// </summary>
public class CueLibraryService
{
    /// <summary>
    /// Audio file extensions that a CUE sheet may reference.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".ape", ".wv", ".wav", ".tta", ".tak", ".m4a", ".alac", ".aiff", ".aif", ".aifc",
        ".mp3", ".ogg", ".oga", ".opus", ".wma", ".dsf", ".dff", ".mpc", ".ofr", ".shn", ".mka"
    };

    private readonly AudioProbe _probe;
    private readonly ILogger<CueLibraryService> _logger;
    private readonly ConcurrentDictionary<string, (string Stamp, CueAlbum? Album)> _albumCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="CueLibraryService"/> class.
    /// </summary>
    /// <param name="probe">Audio probe.</param>
    /// <param name="logger">Logger.</param>
    public CueLibraryService(AudioProbe probe, ILogger<CueLibraryService> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// Gets the usable CUE albums of a directory (duplicated sheets pointing at the same image are dropped).
    /// </summary>
    /// <param name="directory">Directory path.</param>
    /// <returns>The albums, ordered by sheet file name.</returns>
    public IReadOnlyList<CueAlbum> GetAlbumsInDirectory(string directory)
    {
        string[] cueFiles;
        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<CueAlbum>();
            }

            cueFiles = Directory.GetFiles(directory, "*.cue", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cannot list {Directory}", directory);
            return Array.Empty<CueAlbum>();
        }

        if (cueFiles.Length == 0)
        {
            return Array.Empty<CueAlbum>();
        }

        var albums = new List<CueAlbum>();
        var seenSourceSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer the sheet named like its image, then alphabetical order.
        foreach (var cuePath in cueFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var album = GetAlbum(cuePath);
            if (album is null)
            {
                continue;
            }

            var sourceSet = string.Join('|', album.Tracks.Select(t => t.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            if (!seenSourceSets.Add(sourceSet))
            {
                _logger.LogInformation("Ignoring {Cue}: another CUE sheet in the same folder already describes the same audio file(s)", cuePath);
                continue;
            }

            albums.Add(album);
        }

        return albums;
    }

    /// <summary>
    /// Gets the audio files of a directory that are described by a usable CUE sheet.
    /// </summary>
    /// <param name="directory">Directory path.</param>
    /// <returns>Set of full paths.</returns>
    public IReadOnlySet<string> GetConsumedSourceFiles(string directory)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in GetAlbumsInDirectory(directory))
        {
            foreach (var track in album.Tracks)
            {
                set.Add(track.SourcePath);
            }
        }

        return set;
    }

    /// <summary>
    /// Gets the album described by a CUE sheet.
    /// </summary>
    /// <param name="cuePath">Full path of the sheet.</param>
    /// <returns>The album, or null if the sheet is unusable (no audio, file missing, already split...).</returns>
    public CueAlbum? GetAlbum(string cuePath)
    {
        try
        {
            var cueInfo = new FileInfo(cuePath);
            if (!cueInfo.Exists)
            {
                return null;
            }

            var directory = cueInfo.DirectoryName!;
            var dirFiles = Directory.GetFiles(directory);

            // Cheap stamp: sheet + every audio file of the folder (timestamps and sizes).
            var sb = new StringBuilder();
            sb.Append(cueInfo.Length).Append('@').Append(cueInfo.LastWriteTimeUtc.Ticks);
            foreach (var f in dirFiles.Where(f => AudioExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                sb.Append('|').Append(fi.Name).Append(':').Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
            }

            var stamp = sb.ToString();
            if (_albumCache.TryGetValue(cuePath, out var cached) && cached.Stamp == stamp)
            {
                return cached.Album;
            }

            var album = BuildAlbum(cuePath, dirFiles);
            _albumCache[cuePath] = (stamp, album);
            return album;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read CUE sheet {Cue}", cuePath);
            return null;
        }
    }

    /// <summary>
    /// Finds a track of a sheet by its key.
    /// </summary>
    /// <param name="cuePath">Sheet path.</param>
    /// <param name="key">Track key.</param>
    /// <returns>The track, or null.</returns>
    public CueTrackPlan? GetTrack(string cuePath, string key)
    {
        return GetAlbum(cuePath)?.Tracks.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
    }

    private CueAlbum? BuildAlbum(string cuePath, string[] dirFiles)
    {
        var sheet = CueParser.ParseFile(cuePath);

        var filesWithAudio = GetAudioFiles(sheet);
        if (filesWithAudio.Count == 0)
        {
            _logger.LogDebug("{Cue} has no audio track", cuePath);
            return null;
        }

        if (IsAlreadySplit(sheet))
        {
            _logger.LogDebug("{Cue} describes already split files, skipping", cuePath);
            return null;
        }

        var tracks = new List<CueTrackPlan>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, fileTracks) in filesWithAudio)
        {
            var source = ResolveSourcePath(cuePath, file.FileName, sheet.Files.Count, dirFiles);
            if (source is null)
            {
                _logger.LogWarning("{Cue}: audio file \"{File}\" not found", cuePath, file.FileName);
                continue;
            }

            var format = _probe.GetFormat(source);
            if (format is null)
            {
                _logger.LogWarning("{Cue}: cannot read audio format of {Source}", cuePath, source);
                continue;
            }

            // A track starting beyond the end of the file means the sheet does not describe this
            // file (wrong file resolved, or a sheet left over from another rip): leave it alone.
            var lastStart = format.FramesToSamples(fileTracks[^1].StartFrame!.Value);
            if (lastStart >= format.TotalSamples)
            {
                _logger.LogWarning("{Cue}: track {Track} starts after the end of {Source}; the sheet does not match this file, skipping it", cuePath, fileTracks[^1].Number, source);
                continue;
            }

            for (var i = 0; i < fileTracks.Count; i++)
            {
                var track = fileTracks[i];
                var start = format.FramesToSamples(track.StartFrame!.Value);
                var end = i + 1 < fileTracks.Count
                    ? format.FramesToSamples(fileTracks[i + 1].StartFrame!.Value)
                    : format.TotalSamples;
                start = Math.Clamp(start, 0, format.TotalSamples);
                end = Math.Clamp(end, 0, format.TotalSamples);
                if (end <= start)
                {
                    _logger.LogWarning("{Cue}: track {Track} has an empty or negative range, skipping", cuePath, track.Number);
                    continue;
                }

                var key = track.Number.ToString("D2", CultureInfo.InvariantCulture);
                var suffix = 2;
                while (!keys.Add(key))
                {
                    key = string.Create(CultureInfo.InvariantCulture, $"{track.Number:D2}-{suffix++}");
                }

                tracks.Add(new CueTrackPlan(
                    track.Number,
                    key,
                    string.IsNullOrWhiteSpace(track.Title) ? string.Create(CultureInfo.InvariantCulture, $"Track {track.Number:D2}") : track.Title!,
                    string.IsNullOrWhiteSpace(track.Performer) ? sheet.Performer : track.Performer,
                    string.IsNullOrWhiteSpace(track.Songwriter) ? sheet.Songwriter : track.Songwriter,
                    source,
                    start,
                    end,
                    format));
            }
        }

        if (tracks.Count == 0)
        {
            return null;
        }

        return new CueAlbum(cuePath, sheet, tracks);
    }

    /// <summary>
    /// Gets the FILE entries of a sheet that contain audio tracks, with those tracks.
    /// </summary>
    /// <param name="sheet">The sheet.</param>
    /// <returns>Files and their audio tracks.</returns>
    internal static List<(CueFile File, List<CueTrack> Tracks)> GetAudioFiles(CueSheet sheet)
    {
        return sheet.Files
            .Select(f => (File: f, Tracks: f.Tracks.Where(t => t.IsAudio && t.StartFrame.HasValue).ToList()))
            .Where(x => x.Tracks.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Gets a value indicating whether a sheet describes files that are already split
    /// (one file per track), in which case there is nothing to do.
    /// </summary>
    /// <param name="sheet">The sheet.</param>
    /// <returns>True if already split.</returns>
    internal static bool IsAlreadySplit(CueSheet sheet)
    {
        var files = GetAudioFiles(sheet);
        if (files.Count == 0)
        {
            return false;
        }

        // Every file holds exactly one track starting at its beginning.
        if (files.All(x => x.Tracks.Count == 1 && x.Tracks[0].StartFrame == 0))
        {
            return true;
        }

        // Safety net: several files and as many files as tracks is a split rip, whatever the
        // indexes say (pregaps, HTOA, non-standard sheets). An image never looks like this.
        var trackCount = files.Sum(x => x.Tracks.Count);
        return sheet.Files.Count > 1 && sheet.Files.Count >= trackCount;
    }

    /// <summary>
    /// Resolves the FILE entry of a sheet to a file of the folder, tolerating renamed or
    /// re-encoded images (sheet says .wav, folder holds .flac...).
    /// </summary>
    /// <param name="cuePath">Sheet path.</param>
    /// <param name="fileName">FILE value from the sheet.</param>
    /// <param name="fileEntryCount">Number of FILE entries in the sheet.</param>
    /// <param name="dirFiles">Files of the sheet folder.</param>
    /// <returns>The resolved path, or null.</returns>
    internal static string? ResolveSourcePath(string cuePath, string fileName, int fileEntryCount, IReadOnlyList<string> dirFiles)
    {
        var directory = Path.GetDirectoryName(cuePath)!;
        var audioFiles = dirFiles.Where(f => AudioExtensions.Contains(Path.GetExtension(f))).ToList();

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var normalized = fileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

            // 1. Exact name (relative to the sheet).
            var direct = Path.GetFullPath(Path.Combine(directory, normalized));
            var exact = audioFiles.FirstOrDefault(f => string.Equals(f, direct, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            // 2. Same base name, any audio extension.
            var baseName = Path.GetFileNameWithoutExtension(normalized);
            var sameBase = audioFiles.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
            if (sameBase is not null)
            {
                return sameBase;
            }
        }

        if (fileEntryCount != 1)
        {
            return null;
        }

        // 3. Single-file sheet: an audio file named like the sheet.
        var cueBase = Path.GetFileNameWithoutExtension(cuePath);
        var likeCue = audioFiles.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), cueBase, StringComparison.OrdinalIgnoreCase));
        if (likeCue is not null)
        {
            return likeCue;
        }

        // 4. Single-file sheet and a single audio file in the folder.
        return audioFiles.Count == 1 ? audioFiles[0] : null;
    }
}
