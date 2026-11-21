using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.CueSplitter.Cue;

/// <summary>
/// Parser for CUE sheets, tolerant to the variations produced by common rippers
/// (EAC, XLD, foobar2000, CUETools...).
/// </summary>
public static class CueParser
{
    /// <summary>Number of CD frames per second.</summary>
    public const int FramesPerSecond = 75;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Reads and parses a CUE sheet from disk, detecting its text encoding.
    /// </summary>
    /// <param name="path">The CUE file path.</param>
    /// <returns>The parsed sheet.</returns>
    public static CueSheet ParseFile(string path)
    {
        return Parse(DecodeText(File.ReadAllBytes(path)));
    }

    /// <summary>
    /// Decodes the raw bytes of a CUE sheet: BOM if present, otherwise UTF-8 when valid,
    /// otherwise Windows-1252 (the usual encoding of EAC sheets with accents).
    /// </summary>
    /// <param name="bytes">Raw file content.</param>
    /// <returns>The decoded text.</returns>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
            return cp1252.GetString(bytes);
        }
    }

    /// <summary>
    /// Parses the text of a CUE sheet.
    /// </summary>
    /// <param name="text">The sheet content.</param>
    /// <returns>The parsed sheet.</returns>
    public static CueSheet Parse(string text)
    {
        var sheet = new CueSheet();
        CueFile? currentFile = null;
        CueTrack? currentTrack = null;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            var command = tokens[0].ToUpperInvariant();
            switch (command)
            {
                case "REM":
                    if (tokens.Count >= 3)
                    {
                        ApplyRem(sheet, currentTrack, tokens[1].ToUpperInvariant(), JoinFrom(tokens, 2));
                    }

                    break;

                case "TITLE":
                    if (tokens.Count >= 2)
                    {
                        var value = Clean(JoinFrom(tokens, 1));
                        if (currentTrack is not null)
                        {
                            currentTrack.Title = value;
                        }
                        else
                        {
                            sheet.Title = value;
                        }
                    }

                    break;

                case "PERFORMER":
                    if (tokens.Count >= 2)
                    {
                        var value = Clean(JoinFrom(tokens, 1));
                        if (currentTrack is not null)
                        {
                            currentTrack.Performer = value;
                        }
                        else
                        {
                            sheet.Performer = value;
                        }
                    }

                    break;

                case "SONGWRITER":
                    if (tokens.Count >= 2)
                    {
                        var value = Clean(JoinFrom(tokens, 1));
                        if (currentTrack is not null)
                        {
                            currentTrack.Songwriter = value;
                        }
                        else
                        {
                            sheet.Songwriter = value;
                        }
                    }

                    break;

                case "ISRC":
                    if (currentTrack is not null && tokens.Count >= 2)
                    {
                        currentTrack.Isrc = Clean(tokens[1]);
                    }

                    break;

                case "FILE":
                    if (tokens.Count >= 2)
                    {
                        // FILE "name with spaces.flac" WAVE  — the type is the last token when there are 3+.
                        string fileName;
                        string? fileType = null;
                        if (tokens.Count >= 3)
                        {
                            fileType = tokens[^1];
                            fileName = string.Join(' ', tokens.GetRange(1, tokens.Count - 2));
                        }
                        else
                        {
                            fileName = tokens[1];
                        }

                        currentFile = new CueFile(fileName.Trim(), fileType);
                        sheet.Files.Add(currentFile);
                        currentTrack = null;
                    }

                    break;

                case "TRACK":
                    if (currentFile is not null && tokens.Count >= 2
                        && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        currentTrack = new CueTrack(number, tokens.Count >= 3 ? tokens[2] : "AUDIO");
                        currentFile.Tracks.Add(currentTrack);
                    }

                    break;

                case "INDEX":
                    if (currentTrack is not null && tokens.Count >= 3
                        && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var indexNumber)
                        && TryParseTime(tokens[2], out var frames))
                    {
                        currentTrack.Indexes[indexNumber] = frames;
                    }

                    break;
            }
        }

        return sheet;
    }

    /// <summary>
    /// Parses a CUE time stamp "mm:ss:ff" (minutes may exceed 99) into CD frames.
    /// </summary>
    /// <param name="value">The time stamp.</param>
    /// <param name="frames">The position in frames.</param>
    /// <returns>True if parsed.</returns>
    public static bool TryParseTime(string value, out long frames)
    {
        frames = 0;
        var parts = value.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ff)
            || mm < 0 || ss < 0 || ss >= 60 || ff < 0 || ff >= FramesPerSecond)
        {
            return false;
        }

        frames = (((mm * 60) + ss) * FramesPerSecond) + ff;
        return true;
    }

    private static void ApplyRem(CueSheet sheet, CueTrack? track, string key, string rawValue)
    {
        var value = Clean(rawValue);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        switch (key)
        {
            case "GENRE":
                if (track is null)
                {
                    sheet.Genre = value;
                }

                break;
            case "DATE":
                if (track is null)
                {
                    sheet.Date = value;
                }

                break;
            case "DISCNUMBER":
                if (track is null)
                {
                    // Accept "N" as well as "N/M" (disc N of M).
                    var parts = value.Split('/', 2);
                    if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var disc))
                    {
                        sheet.DiscNumber = disc;
                        if (parts.Length == 2
                            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slashTotal))
                        {
                            // An explicit REM TOTALDISCS (before or after) takes precedence.
                            sheet.TotalDiscs ??= slashTotal;
                        }
                    }
                }

                break;
            case "TOTALDISCS":
                if (track is null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                {
                    sheet.TotalDiscs = total;
                }

                break;
            case "COMPOSER":
                if (track is not null)
                {
                    track.Songwriter ??= value;
                }
                else
                {
                    sheet.Songwriter ??= value;
                }

                break;
        }
    }

    private static string JoinFrom(List<string> tokens, int start)
    {
        return start >= tokens.Count ? string.Empty : string.Join(' ', tokens.GetRange(start, tokens.Count - start));
    }

    private static string Clean(string value)
    {
        return value.Trim();
    }

    /// <summary>
    /// Splits a line into tokens; double-quoted strings form a single token (quotes removed).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The tokens.</returns>
    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var span = line.AsSpan().Trim();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
            {
                i++;
            }

            if (i >= span.Length)
            {
                break;
            }

            if (span[i] == '"')
            {
                var end = span.Slice(i + 1).IndexOf('"');
                if (end < 0)
                {
                    tokens.Add(span.Slice(i + 1).ToString());
                    break;
                }

                tokens.Add(span.Slice(i + 1, end).ToString());
                i += end + 2;
            }
            else
            {
                var start = i;
                while (i < span.Length && !char.IsWhiteSpace(span[i]))
                {
                    i++;
                }

                tokens.Add(span.Slice(start, i - start).ToString());
            }
        }

        return tokens;
    }
}
