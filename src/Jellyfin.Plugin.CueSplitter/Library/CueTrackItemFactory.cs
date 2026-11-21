using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Maps CUE tracks onto Jellyfin <see cref="AudioItem"/> entities.
/// </summary>
public static class CueTrackItemFactory
{
    /// <summary>Provider id key marking items created by this plugin (value "1").</summary>
    public const string MarkerKey = "CueSplitter";

    /// <summary>Provider id key holding the CUE sheet path.</summary>
    public const string CueKey = "CueSplitterCue";

    /// <summary>Provider id key holding the track key within the sheet.</summary>
    public const string TrackKey = "CueSplitterTrack";

    /// <summary>Provider id key holding a signature of the applied values (change detection).</summary>
    public const string SignatureKey = "CueSplitterSig";

    private static readonly char[] GenreSeparators = { ';', '/', '|', ',' };

    /// <summary>
    /// Computes the stable item id of a virtual track.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="cuePath">CUE sheet path.</param>
    /// <param name="trackKey">Track key.</param>
    /// <returns>The item id.</returns>
    public static Guid GetItemId(ILibraryManager libraryManager, string cuePath, string trackKey)
    {
        return libraryManager.GetNewItemId("cuesplitter|" + cuePath.ToLowerInvariant() + "|" + trackKey, typeof(AudioItem));
    }

    /// <summary>
    /// Builds the internal stream URL used as the item path.
    /// </summary>
    /// <param name="baseUrl">Server base URL reachable from the server itself.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="streamKey">Secret stream key.</param>
    /// <returns>The URL.</returns>
    public static string BuildStreamUrl(string baseUrl, Guid itemId, string streamKey)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUrl.TrimEnd('/')}/CueSplitter/Tracks/{itemId:N}/stream.wav?key={Uri.EscapeDataString(streamKey)}");
    }

    /// <summary>
    /// Gets a value indicating whether an item is a virtual CUE track.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if created by this plugin.</returns>
    public static bool IsCueTrack(MediaBrowser.Controller.Entities.BaseItem item)
    {
        return item.ProviderIds is not null && item.ProviderIds.ContainsKey(MarkerKey) && item.ProviderIds.ContainsKey(CueKey);
    }

    /// <summary>
    /// Computes the disc number of an album within its folder.
    /// </summary>
    /// <param name="album">The album.</param>
    /// <param name="folderAlbums">All the albums of the folder.</param>
    /// <returns>The disc number, or null for a single-disc folder.</returns>
    public static int? GetDiscNumber(CueAlbum album, IReadOnlyList<CueAlbum> folderAlbums)
    {
        if (album.Sheet.DiscNumber.HasValue)
        {
            return album.Sheet.DiscNumber;
        }

        if (folderAlbums.Count <= 1)
        {
            return null;
        }

        for (var i = 0; i < folderAlbums.Count; i++)
        {
            if (string.Equals(folderAlbums[i].CuePath, album.CuePath, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the CUE data to an item.
    /// </summary>
    /// <param name="item">The item to update.</param>
    /// <param name="album">The CUE album.</param>
    /// <param name="track">The track.</param>
    /// <param name="discNumber">Disc number, if any.</param>
    /// <param name="streamUrl">Internal stream URL (item path).</param>
    /// <returns>True if something changed.</returns>
    public static bool Apply(AudioItem item, CueAlbum album, CueTrackPlan track, int? discNumber, string streamUrl)
    {
        var sheet = album.Sheet;
        var format = track.Format;
        var albumTitle = string.IsNullOrWhiteSpace(sheet.Title)
            ? new DirectoryInfo(Path.GetDirectoryName(album.CuePath)!).Name
            : sheet.Title!;
        var albumArtist = string.IsNullOrWhiteSpace(sheet.Performer) ? track.Performer : sheet.Performer;
        var artists = string.IsNullOrWhiteSpace(track.Performer) ? Array.Empty<string>() : new[] { track.Performer! };
        var albumArtists = string.IsNullOrWhiteSpace(albumArtist) ? Array.Empty<string>() : new[] { albumArtist! };
        var genres = SplitGenres(sheet.Genre);
        var year = ParseYear(sheet.Date);
        var header = WavHeader.GetHeaderLength(format);
        var size = header + (track.SampleCount * format.BlockAlign);
        var runtime = format.SamplesToTicks(track.SampleCount);
        var bitrate = (int)Math.Min(int.MaxValue, format.ByteRate * 8);

        var signature = string.Join(
            '\u001f',
            streamUrl,
            track.Title,
            albumTitle,
            string.Join('|', artists),
            string.Join('|', albumArtists),
            string.Join('|', genres),
            year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            track.TrackNumber.ToString(CultureInfo.InvariantCulture),
            discNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            track.SourcePath,
            track.StartSample.ToString(CultureInfo.InvariantCulture),
            track.EndSample.ToString(CultureInfo.InvariantCulture),
            format.ToString());
        var signatureHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(signature)));

        if (item.ProviderIds is not null
            && item.ProviderIds.TryGetValue(SignatureKey, out var existingSig)
            && existingSig == signatureHash
            && string.Equals(item.Path, streamUrl, StringComparison.Ordinal)
            && string.Equals(item.Name, track.Title, StringComparison.Ordinal))
        {
            return false;
        }

        var locked = item.LockedFields ?? Array.Empty<MetadataField>();

        item.Path = streamUrl;
        if (!locked.Contains(MetadataField.Name))
        {
            item.Name = track.Title;
        }

        item.Album = albumTitle;
        item.Artists = artists;
        item.AlbumArtists = albumArtists;
        if (!locked.Contains(MetadataField.Genres))
        {
            item.Genres = genres;
        }

        item.ProductionYear = year;
        item.IndexNumber = track.TrackNumber;
        item.ParentIndexNumber = discNumber;
        if (!locked.Contains(MetadataField.Runtime))
        {
            item.RunTimeTicks = runtime;
        }

        item.Container = "wav";
        item.Size = size;
        item.TotalBitrate = bitrate;
        item.IsInMixedFolder = false;

        var cueInfo = new FileInfo(album.CuePath);
        var sourceInfo = new FileInfo(track.SourcePath);
        if (item.DateCreated == default)
        {
            item.DateCreated = sourceInfo.Exists ? sourceInfo.CreationTimeUtc : DateTime.UtcNow;
        }

        item.DateModified = new[] { cueInfo.LastWriteTimeUtc, sourceInfo.LastWriteTimeUtc }.Max();

        item.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        item.ProviderIds[MarkerKey] = "1";
        item.ProviderIds[CueKey] = album.CuePath;
        item.ProviderIds[TrackKey] = track.Key;
        item.ProviderIds[SignatureKey] = signatureHash;
        return true;
    }

    /// <summary>
    /// Builds the media stream description of a track (what the stream endpoint serves).
    /// </summary>
    /// <param name="format">The PCM format.</param>
    /// <returns>The media streams.</returns>
    public static IReadOnlyList<MediaStream> BuildMediaStreams(AudioFormat format)
    {
        return new[]
        {
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 0,
                Codec = format.PcmCodec,
                Channels = format.Channels,
                SampleRate = format.SampleRate,
                BitDepth = format.BitsPerSample,
                BitRate = (int)Math.Min(int.MaxValue, format.ByteRate * 8),
                ChannelLayout = format.Channels switch
                {
                    1 => "mono",
                    2 => "stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => null
                },
                IsDefault = true
            }
        };
    }

    /// <summary>
    /// Splits a genre string ("Synth-pop, Disco") into individual genres.
    /// </summary>
    /// <param name="genre">The raw genre.</param>
    /// <returns>Genres.</returns>
    internal static string[] SplitGenres(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return Array.Empty<string>();
        }

        return genre.Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Extracts a year from a REM DATE value ("1977", "1977-05-01", "05/1977").
    /// </summary>
    /// <param name="date">The raw date.</param>
    /// <returns>The year, or null.</returns>
    internal static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        for (var i = 0; i + 4 <= date.Length; i++)
        {
            if (int.TryParse(date.AsSpan(i, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= 1000 and <= 2999
                && (i + 4 == date.Length || !char.IsDigit(date[i + 4])))
            {
                return year;
            }
        }

        return null;
    }
}
