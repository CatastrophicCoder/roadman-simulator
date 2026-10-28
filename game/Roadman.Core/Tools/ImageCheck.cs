namespace Roadman.Core.Tools;

/// <summary>Sanity checks on captured images.</summary>
public static class ImageCheck
{
    /// <summary>
    /// True when every pixel has the same colour, within <paramref name="tolerance"/> per channel. A screenshot
    /// like that means nothing was drawn (a black or cleared viewport, or a camera inside the terrain).
    /// </summary>
    /// <param name="pixels">Interleaved 8-bit pixel data, for example RGBA8.</param>
    /// <param name="channels">Bytes per pixel (1 to 4).</param>
    /// <param name="tolerance">Largest per-channel difference still counted as the same colour.</param>
    public static bool IsFlatColour(ReadOnlySpan<byte> pixels, int channels, int tolerance = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        if (pixels.Length % channels != 0)
        {
            throw new ArgumentException($"pixel data length {pixels.Length} isn't a multiple of {channels} channels", nameof(pixels));
        }

        Span<byte> min = stackalloc byte[channels];
        Span<byte> max = stackalloc byte[channels];
        min.Fill(byte.MaxValue);
        max.Clear();
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = i % channels;
            var value = pixels[i];
            if (value < min[c])
            {
                min[c] = value;
            }

            if (value > max[c])
            {
                max[c] = value;
            }

            if (max[c] - min[c] > tolerance)
            {
                return false;
            }
        }

        return true;
    }
}
