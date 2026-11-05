namespace Roadman.Core.Settings;

/// <summary>What <see cref="DisplayRevert.Tick"/> found.</summary>
public enum DisplayRevertTick
{
    /// <summary>No confirmation is pending; nothing changed.</summary>
    NotPending,

    /// <summary>The countdown is running.</summary>
    Counting,

    /// <summary>The countdown has run out: the caller reverts (<see cref="DisplayRevert.Revert"/>).</summary>
    TimedOut,
}

/// <summary>
/// The state of the "Keep these display settings?" dialog (<c>docs/specs/menu-settings-save.md</c>, "Display",
/// confirm or revert): after a change to the window mode, window size or render scale
/// (<see cref="NeedsConfirm"/>), <see cref="Begin"/> starts a <see cref="TimeoutSeconds"/> countdown. Keep ends it;
/// Revert, cancel or the countdown running out restore those three values from before the change. Further display
/// changes are blocked while it is pending (<see cref="IsPending"/>).
/// </summary>
public sealed class DisplayRevert
{
    /// <summary>The countdown in seconds.</summary>
    public const double TimeoutSeconds = 10;

    /// <summary>True from <see cref="Begin"/> until <see cref="Keep"/> or <see cref="Revert"/>.</summary>
    public bool IsPending => Previous is not null;

    /// <summary>The display settings from before the change while pending, else null.</summary>
    public DisplaySettings? Previous { get; private set; }

    /// <summary>
    /// Seconds until the countdown runs out (<see cref="TimeoutSeconds"/> at <see cref="Begin"/>, never below 0); 0
    /// when not pending. Show it rounded up (<see cref="Math.Ceiling(double)"/>).
    /// </summary>
    public double SecondsLeft { get; private set; }

    /// <summary>True when <paramref name="before"/> and <paramref name="after"/> differ in window mode, window size or render scale.</summary>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public static bool NeedsConfirm(DisplaySettings before, DisplaySettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.WindowMode != after.WindowMode
            || before.WindowSize != after.WindowSize
            || before.RenderScale != after.RenderScale;
    }

    /// <summary>
    /// <paramref name="current"/> with the window mode, window size and render scale of <paramref name="previous"/>;
    /// its quality, vsync and frame cap are kept.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public static DisplaySettings Restore(DisplaySettings previous, DisplaySettings current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            WindowMode = previous.WindowMode,
            WindowSize = previous.WindowSize,
            RenderScale = previous.RenderScale,
        };
    }

    /// <summary>Starts the countdown, remembering <paramref name="previous"/>.</summary>
    /// <returns>False, changing nothing, when a confirmation is already pending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> is null.</exception>
    public bool Begin(DisplaySettings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (IsPending)
        {
            return false;
        }

        Previous = previous;
        SecondsLeft = TimeoutSeconds;
        return true;
    }

    /// <summary>Counts <paramref name="delta"/> seconds down.</summary>
    /// <returns>
    /// <see cref="DisplayRevertTick.TimedOut"/> once <see cref="SecondsLeft"/> reaches 0 (and on every later tick until
    /// <see cref="Revert"/> or <see cref="Keep"/>), <see cref="DisplayRevertTick.Counting"/> before that,
    /// <see cref="DisplayRevertTick.NotPending"/> when nothing is pending.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> is negative or not finite; nothing changes.</exception>
    public DisplayRevertTick Tick(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "must be finite and ≥ 0");
        }

        if (!IsPending)
        {
            return DisplayRevertTick.NotPending;
        }

        SecondsLeft = Math.Max(0, SecondsLeft - delta);
        return SecondsLeft <= 0 ? DisplayRevertTick.TimedOut : DisplayRevertTick.Counting;
    }

    /// <summary>Keeps the new values and ends the confirmation.</summary>
    /// <returns>False when nothing was pending.</returns>
    public bool Keep()
    {
        if (!IsPending)
        {
            return false;
        }

        Previous = null;
        SecondsLeft = 0;
        return true;
    }

    /// <summary>
    /// Ends the confirmation and gives the settings to apply: <paramref name="current"/> with the window mode, window
    /// size and render scale from before the change (<see cref="Restore"/>).
    /// </summary>
    /// <returns>Null, changing nothing, when nothing was pending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public DisplaySettings? Revert(DisplaySettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Previous is not { } previous)
        {
            return null;
        }

        Previous = null;
        SecondsLeft = 0;
        return Restore(previous, current);
    }
}
