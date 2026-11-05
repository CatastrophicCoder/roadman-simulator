using System.Globalization;

namespace Roadman.Core.Saves;

/// <summary>Counts play seconds; the game advances it each frame while a game runs and isn't paused.</summary>
public sealed class PlayTime
{
    /// <param name="startSeconds">A loaded save's <see cref="SaveGame.PlaySeconds"/>, or 0 for a new game.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startSeconds"/> is negative or not finite.</exception>
    public PlayTime(double startSeconds = 0)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), startSeconds, "must be finite and ≥ 0");
        }

        Seconds = startSeconds;
    }

    /// <summary>Total play time in seconds.</summary>
    public double Seconds { get; private set; }

    /// <summary>Adds <paramref name="delta"/> seconds.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> is negative or not finite; nothing changes.</exception>
    public void Advance(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "must be finite and ≥ 0");
        }

        Seconds += delta;
    }
}

/// <summary>The slot list's play time text.</summary>
public static class PlayTimeText
{
    /// <summary>
    /// Whole hours and minutes as <c>h:mm</c>, rounding down: 0 s → <c>0:00</c>, 300 s → <c>0:05</c>,
    /// 4,980 s → <c>1:23</c>, 90,420 s → <c>25:07</c> (hours aren't wrapped at a day).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is negative or not finite.</exception>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "must be finite and ≥ 0");
        }

        var minutes = (long)Math.Floor(seconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}:{minutes % 60:00}");
    }
}
