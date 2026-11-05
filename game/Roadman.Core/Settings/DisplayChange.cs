namespace Roadman.Core.Settings;

/// <summary>
/// What applying a display group sends to the window (<see cref="DisplayChange.Plan"/>): the window mode and the
/// window size, each null when it isn't sent. VSync, the frame cap and the quality preset are always applied.
/// </summary>
/// <param name="WindowMode">The mode to set, or null to leave the window's mode alone.</param>
/// <param name="WindowSize">The window size to set (centred), or null to leave the window's size alone.</param>
public readonly record struct DisplayApply(WindowMode? WindowMode, WindowSize? WindowSize);

/// <summary>
/// When applying the display group resends the window mode and size (<c>docs/specs/menu-settings-save.md</c>,
/// "Settings view", display).
/// </summary>
public static class DisplayChange
{
    /// <summary>
    /// The window operations for applying <paramref name="display"/>: the mode only when the window's mode
    /// (<paramref name="windowMode"/>) differs; the size only in a window, and then only at startup
    /// (<paramref name="previous"/> null) or when the size or mode changed, so a vsync or quality change doesn't move
    /// the window.
    /// </summary>
    /// <param name="display">The values to apply.</param>
    /// <param name="windowMode">The window's mode now.</param>
    /// <param name="previous">The values applied before, or null to apply everything (startup).</param>
    /// <exception cref="ArgumentNullException"><paramref name="display"/> is null.</exception>
    public static DisplayApply Plan(DisplaySettings display, WindowMode windowMode, DisplaySettings? previous)
    {
        ArgumentNullException.ThrowIfNull(display);
        WindowMode? mode = windowMode != display.WindowMode ? display.WindowMode : null;
        var sendSize = display.WindowMode == WindowMode.Windowed
            && (previous is null || previous.WindowMode != display.WindowMode || previous.WindowSize != display.WindowSize);
        return new DisplayApply(mode, sendSize ? display.WindowSize : null);
    }
}
