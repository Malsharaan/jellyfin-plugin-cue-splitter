using System;
using System.Globalization;

namespace Jellyfin.Plugin.CueSplitter.Streaming;

/// <summary>
/// An inclusive byte range of a resource.
/// </summary>
/// <param name="Start">First byte (inclusive).</param>
/// <param name="End">Last byte (inclusive).</param>
public readonly record struct ByteRange(long Start, long End)
{
    /// <summary>Gets the number of bytes in the range.</summary>
    public long Length => End - Start + 1;

    /// <summary>
    /// Parses an HTTP Range header for a resource of known length. Only the first range of a
    /// multi-range request is honoured.
    /// </summary>
    /// <param name="header">The Range header value (may be null).</param>
    /// <param name="totalLength">Resource length.</param>
    /// <param name="range">The resolved range.</param>
    /// <returns>
    /// <see cref="RangeParseResult.Full"/> when there is no (usable) range,
    /// <see cref="RangeParseResult.Partial"/> for a satisfiable range,
    /// <see cref="RangeParseResult.Unsatisfiable"/> otherwise.
    /// </returns>
    public static RangeParseResult TryParse(string? header, long totalLength, out ByteRange range)
    {
        range = new ByteRange(0, totalLength - 1);
        if (string.IsNullOrWhiteSpace(header))
        {
            return RangeParseResult.Full;
        }

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParseResult.Full;
        }

        var spec = value.Substring(6).Split(',')[0].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return RangeParseResult.Full;
        }

        var first = spec.Substring(0, dash).Trim();
        var last = spec.Substring(dash + 1).Trim();

        if (first.Length == 0)
        {
            // Suffix range: last N bytes.
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return RangeParseResult.Unsatisfiable;
            }

            var start = Math.Max(0, totalLength - suffix);
            range = new ByteRange(start, totalLength - 1);
            return RangeParseResult.Partial;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from >= totalLength)
        {
            return RangeParseResult.Unsatisfiable;
        }

        var to = totalLength - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from)
            {
                return RangeParseResult.Unsatisfiable;
            }

            to = Math.Min(to, totalLength - 1);
        }

        range = new ByteRange(from, to);
        return RangeParseResult.Partial;
    }
}

/// <summary>
/// Result of parsing a Range header.
/// </summary>
public enum RangeParseResult
{
    /// <summary>Serve the whole resource.</summary>
    Full,

    /// <summary>Serve a partial range (206).</summary>
    Partial,

    /// <summary>Range not satisfiable (416).</summary>
    Unsatisfiable
}
