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
