using System.Collections.Generic;

namespace Jellyfin.Plugin.CueSplitter.Cue;

/// <summary>
/// A parsed CUE sheet.
/// </summary>
public sealed class CueSheet
{
    /// <summary>Gets or sets the album title (TITLE).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the album performer (PERFORMER).</summary>
    public string? Performer { get; set; }

    /// <summary>Gets or sets the album songwriter (SONGWRITER).</summary>
    public string? Songwriter { get; set; }

    /// <summary>Gets or sets the genre (REM GENRE).</summary>
    public string? Genre { get; set; }

    /// <summary>Gets or sets the date (REM DATE).</summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the disc number (REM DISCNUMBER).</summary>
    public int? DiscNumber { get; set; }

    /// <summary>Gets or sets the total number of discs (REM TOTALDISCS).</summary>
    public int? TotalDiscs { get; set; }

    /// <summary>Gets the FILE entries, in order.</summary>
    public List<CueFile> Files { get; } = new();
}

/// <summary>
/// A FILE entry of a CUE sheet.
/// </summary>
public sealed class CueFile
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CueFile"/> class.
    /// </summary>
    /// <param name="fileName">The file name as written in the sheet.</param>
    /// <param name="fileType">The file type (WAVE, MP3, ...).</param>
    public CueFile(string fileName, string? fileType)
    {
        FileName = fileName;
        FileType = fileType;
    }

    /// <summary>Gets the file name as written in the sheet.</summary>
    public string FileName { get; }

    /// <summary>Gets the declared file type.</summary>
    public string? FileType { get; }

    /// <summary>Gets the tracks contained in this file.</summary>
    public List<CueTrack> Tracks { get; } = new();
}

/// <summary>
/// A TRACK entry of a CUE sheet.
/// </summary>
public sealed class CueTrack
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CueTrack"/> class.
    /// </summary>
    /// <param name="number">Track number.</param>
    /// <param name="dataType">Track data type (AUDIO, MODE1/2352, ...).</param>
    public CueTrack(int number, string dataType)
    {
        Number = number;
        DataType = dataType;
    }

    /// <summary>Gets the track number.</summary>
    public int Number { get; }

    /// <summary>Gets the data type.</summary>
    public string DataType { get; }

    /// <summary>Gets a value indicating whether this is an audio track.</summary>
    public bool IsAudio => string.Equals(DataType, "AUDIO", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the performer.</summary>
    public string? Performer { get; set; }

    /// <summary>Gets or sets the songwriter.</summary>
    public string? Songwriter { get; set; }

    /// <summary>Gets or sets the ISRC code.</summary>
    public string? Isrc { get; set; }

    /// <summary>Gets the INDEX entries: index number to position in CD frames (1/75 s).</summary>
    public SortedDictionary<int, long> Indexes { get; } = new();

    /// <summary>
    /// Gets the position (in CD frames) where the track starts: INDEX 01, or INDEX 00 if 01 is absent.
    /// </summary>
    public long? StartFrame
    {
        get
        {
            if (Indexes.TryGetValue(1, out var start))
            {
                return start;
            }

            foreach (var kv in Indexes)
            {
                return kv.Value;
            }

            return null;
        }
    }
}
