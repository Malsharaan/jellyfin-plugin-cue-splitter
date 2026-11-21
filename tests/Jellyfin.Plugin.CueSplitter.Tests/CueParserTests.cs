using System.Text;
using Jellyfin.Plugin.CueSplitter.Cue;

namespace Jellyfin.Plugin.CueSplitter.Tests;

public class CueParserTests
{
    private const string EacSheet = """
        REM GENRE "Synth-pop, Disco"
        REM DATE 1977
        REM DISCID A30DF10C
        REM COMMENT "ExactAudioCopy v1.0b1"
        PERFORMER "Space"
        TITLE "Deliverance"
        FILE "Space - Deliverance.flac" WAVE
          TRACK 01 AUDIO
            TITLE "Prison"
            PERFORMER "Space"
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "Let Me Know The Wonder"
            PERFORMER "Space"
            FLAGS DCP
            INDEX 00 06:18:20
            INDEX 01 06:20:51
          TRACK 03 AUDIO
            TITLE "Running The City"
            INDEX 01 13:24:72
        """;

    [Fact]
    public void Parse_ReadsAlbumAndTrackFields()
    {
        var sheet = CueParser.Parse(EacSheet);

        Assert.Equal("Deliverance", sheet.Title);
        Assert.Equal("Space", sheet.Performer);
        Assert.Equal("Synth-pop, Disco", sheet.Genre);
        Assert.Equal("1977", sheet.Date);
        var file = Assert.Single(sheet.Files);
        Assert.Equal("Space - Deliverance.flac", file.FileName);
        Assert.Equal("WAVE", file.FileType);
        Assert.Equal(3, file.Tracks.Count);
        Assert.Equal("Let Me Know The Wonder", file.Tracks[1].Title);
        Assert.Null(file.Tracks[2].Performer);
    }

    [Fact]
    public void Parse_UsesIndex01AsTrackStart()
    {
        var track = CueParser.Parse(EacSheet).Files[0].Tracks[1];

        Assert.Equal((((6 * 60) + 18) * 75) + 20, track.Indexes[0]);
        Assert.Equal((((6 * 60) + 20) * 75) + 51, track.StartFrame);
    }

    [Theory]
    [InlineData("00:00:00", 0)]
    [InlineData("01:02:03", ((62 * 75) + 3))]
    [InlineData("120:00:74", ((7200 * 75) + 74))]
    public void TryParseTime_ParsesFrames(string value, long expected)
    {
        Assert.True(CueParser.TryParseTime(value, out var frames));
        Assert.Equal(expected, frames);
    }

    [Theory]
    [InlineData("00:60:00")]
    [InlineData("00:00:75")]
    [InlineData("1:2")]
    [InlineData("aa:00:00")]
    public void TryParseTime_RejectsInvalid(string value)
    {
        Assert.False(CueParser.TryParseTime(value, out _));
    }

    [Fact]
    public void Tokenize_KeepsQuotedStrings()
    {
        var tokens = CueParser.Tokenize("  FILE \"My Album - Disc 1.flac\" WAVE ");
        Assert.Equal(new[] { "FILE", "My Album - Disc 1.flac", "WAVE" }, tokens);
    }

    [Fact]
    public void Parse_UnquotedValuesAndRemFields()
    {
        var sheet = CueParser.Parse("REM DISCNUMBER 2\nREM TOTALDISCS 3\nTITLE Plain Title\nFILE image.ape WAVE\n TRACK 1 AUDIO\n INDEX 01 00:00:00\n TRACK 2 AUDIO\n INDEX 01 00:10:00");
        Assert.Equal(2, sheet.DiscNumber);
        Assert.Equal(3, sheet.TotalDiscs);
        Assert.Equal("Plain Title", sheet.Title);
        Assert.Equal("image.ape", sheet.Files[0].FileName);
        Assert.Equal(2, sheet.Files[0].Tracks.Count);
    }

    private const string DiscBody = "\nFILE image.flac WAVE\n TRACK 1 AUDIO\n INDEX 01 00:00:00";

    [Fact]
    public void Parse_DiscNumberWithSlash_SetsDiscAndTotal()
    {
        var sheet = CueParser.Parse("REM DISCNUMBER \"1/2\"" + DiscBody);
        Assert.Equal(1, sheet.DiscNumber);
        Assert.Equal(2, sheet.TotalDiscs);
    }

    [Theory]
    [InlineData("REM DISCNUMBER \"1/2\"\nREM TOTALDISCS 3")]
    [InlineData("REM TOTALDISCS 3\nREM DISCNUMBER \"1/2\"")]
    public void Parse_ExplicitTotalDiscsWinsOverSlashValue(string header)
    {
        var sheet = CueParser.Parse(header + DiscBody);
        Assert.Equal(1, sheet.DiscNumber);
        Assert.Equal(3, sheet.TotalDiscs);
    }

    [Fact]
    public void Parse_PlainDiscNumber_StillWorks()
    {
        var sheet = CueParser.Parse("REM DISCNUMBER 1" + DiscBody);
        Assert.Equal(1, sheet.DiscNumber);
        Assert.Null(sheet.TotalDiscs);
    }

    [Fact]
    public void Parse_InvalidDiscNumber_IsIgnored()
    {
        var sheet = CueParser.Parse("REM DISCNUMBER abc" + DiscBody);
        Assert.Null(sheet.DiscNumber);
        Assert.Null(sheet.TotalDiscs);
    }

    // EAC "gaps appended" (non-compliant) sheet of a split rip: the INDEX 00 of a track is at the
    // end of the previous file, its INDEX 01 right after the next FILE line.
    private const string EacNonCompliantSplit = """
        PERFORMER "Jacques Brel"
        TITLE "Grand Jacques"
        FILE "01 Grand Jacques.flac" WAVE
          TRACK 01 AUDIO
            TITLE "Grand Jacques"
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "La haine"
            INDEX 00 02:28:40
        FILE "02 La haine.flac" WAVE
            INDEX 01 00:00:00
          TRACK 03 AUDIO
            TITLE "Il y a"
            INDEX 00 02:41:10
        FILE "03 Il y a.flac" WAVE
            INDEX 01 00:00:00
          TRACK 04 AUDIO
            TITLE "Le diable"
        FILE "04 Le diable.flac" WAVE
            INDEX 01 00:00:00
        """;

    [Fact]
    public void Parse_EacNonCompliant_AttachesTracksToTheFileOfTheirIndex01()
    {
        var sheet = CueParser.Parse(EacNonCompliantSplit);

        Assert.Equal(4, sheet.Files.Count);
        for (var i = 0; i < 4; i++)
        {
            var track = Assert.Single(sheet.Files[i].Tracks);
            Assert.Equal(i + 1, track.Number);
            Assert.Equal(0, track.StartFrame);
        }

        // The pregap of track 2 lived in file 01: it is not an index of track 2 in file 02.
        Assert.False(sheet.Files[1].Tracks[0].Indexes.ContainsKey(0));
        Assert.Equal("La haine", sheet.Files[1].Tracks[0].Title);
        Assert.True(CueLibraryService.IsAlreadySplit(sheet));
    }

    [Fact]
    public void Parse_EacCompliantSplit_PregapStaysInOwnFile()
    {
        // "Gaps left out" / compliant: each file starts with the pregap, INDEX 01 later in the same file.
        var sheet = CueParser.Parse("FILE \"01.flac\" WAVE\n TRACK 01 AUDIO\n INDEX 01 00:00:00\nFILE \"02.flac\" WAVE\n TRACK 02 AUDIO\n INDEX 00 00:00:00\n INDEX 01 00:02:00");
        Assert.Single(sheet.Files[1].Tracks);
        Assert.Equal(150, sheet.Files[1].Tracks[0].StartFrame);
        Assert.True(CueLibraryService.IsAlreadySplit(sheet));
    }

    [Fact]
    public void IsAlreadySplit_FalseForImagesIncludingMultiDiscSheets()
    {
        var single = CueParser.Parse("FILE \"img.flac\" WAVE\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n INDEX 00 03:00:00\n INDEX 01 03:02:00");
        Assert.False(CueLibraryService.IsAlreadySplit(single));

        var twoDiscs = CueParser.Parse(
            "FILE \"cd1.flac\" WAVE\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n INDEX 01 04:00:00\n" +
            "FILE \"cd2.flac\" WAVE\n TRACK 03 AUDIO\n INDEX 01 00:00:00\n TRACK 04 AUDIO\n INDEX 01 05:00:00");
        Assert.Equal(2, twoDiscs.Files[0].Tracks.Count);
        Assert.Equal(2, twoDiscs.Files[1].Tracks.Count);
        Assert.False(CueLibraryService.IsAlreadySplit(twoDiscs));
    }

    [Fact]
    public void IsAlreadySplit_OneFilePerTrackEvenWithOddIndexes()
    {
        // Hidden track one audio: track 1 starts after a pregap in its own file.
        var sheet = CueParser.Parse("FILE \"01.flac\" WAVE\n TRACK 01 AUDIO\n INDEX 00 00:00:00\n INDEX 01 00:32:00\nFILE \"02.flac\" WAVE\n TRACK 02 AUDIO\n INDEX 01 00:00:00");
        Assert.True(CueLibraryService.IsAlreadySplit(sheet));
    }

    [Fact]
    public void Parse_SkipsDataTracksFlagging()
    {
        var sheet = CueParser.Parse("FILE \"a.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n INDEX 01 10:00:00");
        Assert.False(sheet.Files[0].Tracks[0].IsAudio);
        Assert.True(sheet.Files[0].Tracks[1].IsAudio);
    }

    [Fact]
    public void DecodeText_FallsBackToWindows1252()
    {
        var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        var bytes = cp1252.GetBytes("TITLE \"Étoile, là-bas\"");
        Assert.Equal("TITLE \"Étoile, là-bas\"", CueParser.DecodeText(bytes));
    }

    [Fact]
    public void DecodeText_HandlesUtf8WithAndWithoutBom()
    {
        var text = "TITLE \"Féminin\"";
        Assert.Equal(text, CueParser.DecodeText(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, CueParser.DecodeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));
        Assert.Equal(text, CueParser.DecodeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
    }
}
