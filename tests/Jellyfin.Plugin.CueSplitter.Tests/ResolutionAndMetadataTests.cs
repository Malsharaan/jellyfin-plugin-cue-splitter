using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Library;
using Jellyfin.Plugin.CueSplitter.Streaming;

namespace Jellyfin.Plugin.CueSplitter.Tests;

public class ResolutionAndMetadataTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "album");

    private static string P(string name) => Path.Combine(Dir, name);

    [Fact]
    public void ResolveSourcePath_ExactName()
    {
        var files = new[] { P("a.cue"), P("Image.flac"), P("cover.jpg") };
        Assert.Equal(P("Image.flac"), CueLibraryService.ResolveSourcePath(P("a.cue"), "image.FLAC", 1, files));
    }

    [Fact]
    public void ResolveSourcePath_ReEncodedImage()
    {
        var files = new[] { P("a.cue"), P("Image.flac") };
        Assert.Equal(P("Image.flac"), CueLibraryService.ResolveSourcePath(P("a.cue"), "Image.wav", 1, files));
    }

    [Fact]
    public void ResolveSourcePath_SingleFileFallbacks()
    {
        Assert.Equal(P("a.ape"), CueLibraryService.ResolveSourcePath(P("a.cue"), "CDImage.wav", 1, new[] { P("a.cue"), P("a.ape"), P("b.flac") }));
        Assert.Equal(P("x.wv"), CueLibraryService.ResolveSourcePath(P("a.cue"), "CDImage.wav", 1, new[] { P("a.cue"), P("x.wv") }));
        Assert.Null(CueLibraryService.ResolveSourcePath(P("a.cue"), "CDImage.wav", 1, new[] { P("a.cue"), P("x.wv"), P("y.flac") }));
        Assert.Null(CueLibraryService.ResolveSourcePath(P("a.cue"), "CDImage.wav", 2, new[] { P("a.cue"), P("x.wv") }));
    }

    [Theory]
    [InlineData("Synth-pop, Disco", new[] { "Synth-pop", "Disco" })]
    [InlineData("Rock/Pop; rock", new[] { "Rock", "Pop" })]
    [InlineData("Trance", new[] { "Trance" })]
    [InlineData("", new string[0])]
    public void SplitGenres(string raw, string[] expected)
    {
        Assert.Equal(expected, CueTrackItemFactory.SplitGenres(raw));
    }

    [Theory]
    [InlineData("1977", 1977)]
    [InlineData("2009-05-01", 2009)]
    [InlineData("05/1999", 1999)]
    [InlineData("unknown", null)]
    [InlineData("123456", null)]
    public void ParseYear(string raw, int? expected)
    {
        Assert.Equal(expected, CueTrackItemFactory.ParseYear(raw));
    }

    [Fact]
    public void AudioFormat_FrameConversionIsExactForCdRates()
    {
        var cd = new AudioFormat(44100, 2, 16, 1000000, "flac");
        Assert.Equal(588, cd.FramesToSamples(1));
        Assert.Equal(((380L * 75) + 51) * 588, cd.FramesToSamples((380 * 75) + 51));
        var hd = new AudioFormat(96000, 2, 24, 1000000, "flac");
        Assert.Equal(1280, hd.FramesToSamples(1));
        Assert.Equal(6, hd.BlockAlign);
    }

    [Fact]
    public void ParseProbeOutput_UsesDurationTsForExactSampleCount()
    {
        const string Json = """
            {"streams":[{"codec_name":"ape","sample_fmt":"s16p","sample_rate":"44100","channels":2,"bits_per_sample":0,
            "time_base":"1/44100","duration_ts":179524044,"duration":"4070.840000","bits_per_raw_sample":"16"}],"format":{"duration":"4070.840000"}}
            """;
        var format = AudioProbe.ParseProbeOutput(Json);
        Assert.NotNull(format);
        Assert.Equal(179524044, format!.TotalSamples);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal("ape", format.SourceCodec);
    }

    [Fact]
    public void ParseProbeOutput_HighResAndFallbackDuration()
    {
        const string Json = """
            {"streams":[{"codec_name":"wavpack","sample_fmt":"s32p","sample_rate":"96000","channels":2,"bits_per_raw_sample":"24","duration":"10.5"}]}
            """;
        var format = AudioProbe.ParseProbeOutput(Json)!;
        Assert.Equal(24, format.BitsPerSample);
        Assert.Equal(1008000, format.TotalSamples);
        Assert.Equal("pcm_s24le", format.PcmCodec);
    }

    [Fact]
    public void ParseProbeOutput_NoAudioStream()
    {
        Assert.Null(AudioProbe.ParseProbeOutput("{\"streams\":[]}"));
    }
}
