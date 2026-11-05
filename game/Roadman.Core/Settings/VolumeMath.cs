namespace Roadman.Core.Settings;

/// <summary>Bus volume percent to decibels.</summary>
public static class VolumeMath
{
    /// <summary>
    /// 100 % is 0 dB; 0 % is <see cref="double.NegativeInfinity"/> (set the bus's mute flag instead, see
    /// <see cref="IsMuted"/>); in between, linear amplitude: 20·log10(p / 100) (50 % ≈ −6.02 dB). Strictly
    /// increasing in <paramref name="percent"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is outside 0..100.</exception>
    public static double ToDb(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return percent == 0 ? double.NegativeInfinity : 20 * Math.Log10(percent / 100.0);
    }

    /// <summary>True for 0 %: the bus is muted.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is outside 0..100.</exception>
    public static bool IsMuted(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return percent == 0;
    }
}
