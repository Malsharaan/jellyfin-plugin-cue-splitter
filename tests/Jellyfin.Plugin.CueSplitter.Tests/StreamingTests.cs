using System.Buffers.Binary;
using Jellyfin.Plugin.CueSplitter.Streaming;

namespace Jellyfin.Plugin.CueSplitter.Tests;

public class StreamingTests
{
    [Theory]
    [InlineData(null, RangeParseResult.Full, 0, 999)]
    [InlineData("bytes=0-", RangeParseResult.Partial, 0, 999)]
    [InlineData("bytes=100-199", RangeParseResult.Partial, 100, 199)]
    [InlineData("bytes=900-5000", RangeParseResult.Partial, 900, 999)]
    [InlineData("bytes=-100", RangeParseResult.Partial, 900, 999)]
    [InlineData("bytes=-5000", RangeParseResult.Partial, 0, 999)]
    [InlineData("bytes=10-20, 30-40", RangeParseResult.Partial, 10, 20)]
    [InlineData("items=0-10", RangeParseResult.Full, 0, 999)]
    public void ByteRange_Parse(string? header, RangeParseResult expected, long start, long end)
    {
        var result = ByteRange.TryParse(header, 1000, out var range);
        Assert.Equal(expected, result);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=50-10")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=abc-")]
    public void ByteRange_Unsatisfiable(string header)
    {
        Assert.Equal(RangeParseResult.Unsatisfiable, ByteRange.TryParse(header, 1000, out _));
    }

    [Fact]
    public void WavHeader_Cd()
    {
        var format = new AudioFormat(44100, 2, 16, 0, "flac");
        var header = WavHeader.Build(format, 18710748);
        Assert.Equal(44, header.Length);
        Assert.Equal(44, WavHeader.GetHeaderLength(format));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal(176400u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
        Assert.Equal(18710748u * 4, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
        Assert.Equal((18710748u * 4) + 36, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
    }

    [Fact]
    public void WavHeader_HighResUsesExtensible()
    {
        var format = new AudioFormat(96000, 2, 24, 0, "flac");
        var header = WavHeader.Build(format, 1000);
        Assert.Equal(68, header.Length);
        Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(header, 60, 4));
        Assert.Equal(6000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64)));
    }

    [Fact]
    public void BuildArguments_SeeksWithMicrosecondPrecision()
    {
        var format = new AudioFormat(44100, 2, 16, 0, "flac");
        var args = CueTrackStreamer.BuildArguments("in.flac", format, 16787988, 100);
        var ss = Array.IndexOf(args, "-ss");
        Assert.Equal("380.680000", args[ss + 1]);
        Assert.Contains("atrim=end_sample=100", args);
        Assert.Equal("s16le", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void GetDownloadFileName_IsNumberedAndSafe()
    {
        var format = new AudioFormat(44100, 2, 16, 1000, "flac");
        var track = new Jellyfin.Plugin.CueSplitter.Cue.CueTrackPlan(3, "03", "AC/DC: Live? \"Yes\"", null, null, "x.flac", 0, 10, format);
        Assert.Equal("03 - AC_DC_ Live_ _Yes_.wav", CueTrackServer.GetDownloadFileName(track, TrackFormat.Wav));
        Assert.Equal("03 - AC_DC_ Live_ _Yes_.flac", CueTrackServer.GetDownloadFileName(track, TrackFormat.Flac));
    }

    private static Jellyfin.Plugin.CueSplitter.Cue.CueTrackPlan Plan(int bits, long start, long end, string source = "img.ape")
        => new(2, "02", "Dreams", "Quench", null, source, start, end, new AudioFormat(44100, 2, bits, 50_000_000, "ape"));

    [Fact]
    public void FlacArguments_ExactSampleCountAndSeek()
    {
        var tags = new Dictionary<string, string> { ["TITLE"] = "Dreams", ["ARTIST"] = "Quench", ["ALBUM"] = "" };
        var args = FlacTrackCache.BuildArguments(Plan(16, 16787988, 16787988 + 18710748), tags, "out.flac");

        Assert.Equal("380.680000", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.Contains("atrim=end_sample=18710748,apad=whole_len=18710748", args);
        Assert.Equal("flac", args[Array.IndexOf(args, "-c:a") + 1]);
        Assert.Equal("s16", args[Array.IndexOf(args, "-sample_fmt") + 1]);
        Assert.Contains("TITLE=Dreams", args);
        Assert.Contains("ARTIST=Quench", args);
        Assert.DoesNotContain("ALBUM=", args);
        Assert.Equal("out.flac", args[^1]);
    }

    [Fact]
    public void FlacArguments_HighResKeepsBitDepth()
    {
        var args = FlacTrackCache.BuildArguments(Plan(24, 0, 1000), new Dictionary<string, string>(), "o.flac");
        Assert.DoesNotContain("-ss", args);
        Assert.Equal("s32", args[Array.IndexOf(args, "-sample_fmt") + 1]);
        Assert.Equal("24", args[Array.IndexOf(args, "-bits_per_raw_sample") + 1]);
    }

    [Fact]
    public void CacheKey_ChangesWithTrackBoundaries()
    {
        var a = FlacTrackCache.GetCacheKey(Plan(16, 0, 1000));
        Assert.Equal(a, FlacTrackCache.GetCacheKey(Plan(16, 0, 1000)));
        Assert.NotEqual(a, FlacTrackCache.GetCacheKey(Plan(16, 0, 1001)));
        Assert.NotEqual(a, FlacTrackCache.GetCacheKey(Plan(16, 0, 1000, "other.ape")));
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void Eviction_RemovesLeastRecentlyUsedUntilUnderBudget()
    {
        var now = DateTime.UtcNow;
        var files = new[]
        {
            ("new", 40L, now),
            ("old", 40L, now.AddDays(-3)),
            ("mid", 40L, now.AddDays(-1)),
            ("just-created", 40L, now.AddDays(-10))
        };

        Assert.Equal(new[] { "old", "mid" }, FlacTrackCache.SelectEvictions(files, 90, keep: "just-created"));
        Assert.Empty(FlacTrackCache.SelectEvictions(files, 1000, keep: null));
    }

    [Fact]
    public void BuildArguments_NoSeekAtStart()
    {
        var args = CueTrackStreamer.BuildArguments("in.flac", new AudioFormat(44100, 2, 16, 0, "flac"), 0, 100);
        Assert.DoesNotContain("-ss", args);
    }
}
