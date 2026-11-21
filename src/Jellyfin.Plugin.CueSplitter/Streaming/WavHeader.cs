using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// Builds RIFF/WAVE headers for PCM streams whose length is known in advance,
/// which makes the virtual track byte-addressable (HTTP Range support).
/// </summary>
public static class WavHeader
{
    private static readonly byte[] PcmSubFormat =
    {
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
    };

    /// <summary>
    /// Gets the size of the header that <see cref="Build"/> produces for a format.
    /// </summary>
    /// <param name="format">The PCM format.</param>
    /// <returns>Header length in bytes.</returns>
    public static int GetHeaderLength(AudioFormat format) => UseExtensible(format) ? 68 : 44;

    /// <summary>
    /// Builds the header for a PCM stream.
    /// </summary>
    /// <param name="format">The PCM format.</param>
    /// <param name="sampleCount">Number of samples (per channel) in the data chunk.</param>
    /// <returns>The header bytes.</returns>
    public static byte[] Build(AudioFormat format, long sampleCount)
    {
        var extensible = UseExtensible(format);
        var headerLength = extensible ? 68 : 44;
        var fmtLength = extensible ? 40 : 16;
        var dataLength = sampleCount * format.BlockAlign;

        // RIFF sizes are 32-bit; saturate for (very) long tracks.
        var dataSize = (uint)Math.Min(dataLength, uint.MaxValue - headerLength);
        var riffSize = (uint)Math.Min(dataLength + headerLength - 8, uint.MaxValue);

        var h = new byte[headerLength];
        var s = h.AsSpan();
        WriteAscii(s, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), riffSize);
        WriteAscii(s, 8, "WAVE");
        WriteAscii(s, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), (uint)fmtLength);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(20), extensible ? (ushort)0xFFFE : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(22), (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), (uint)format.ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(32), (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(34), (ushort)format.BitsPerSample);

        var offset = 36;
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(36), 22); // cbSize
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(38), (ushort)format.BitsPerSample); // valid bits
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40), GetChannelMask(format.Channels));
            PcmSubFormat.CopyTo(s.Slice(44));
            offset = 60;
        }

        WriteAscii(s, offset, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(offset + 4), dataSize);
        return h;
    }

    private static bool UseExtensible(AudioFormat format) => format.Channels > 2 || format.BitsPerSample > 16;

    private static uint GetChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x13F,
        8 => 0x63F,
        _ => 0
    };

    private static void WriteAscii(Span<byte> span, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            span[offset + i] = (byte)value[i];
        }
    }
}
