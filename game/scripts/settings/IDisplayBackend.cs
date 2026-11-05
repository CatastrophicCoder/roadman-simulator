using Godot;
using Roadman.Core.Settings;
using SettingsWindowMode = Roadman.Core.Settings.WindowMode;

namespace Roadman.Settings;

/// <summary>
/// The window operations the display settings need (<c>docs/specs/menu-settings-save.md</c>, "Display"), behind an
/// interface so tests can record them: the headless display server ignores window modes and sizes.
/// <see cref="GodotDisplayBackend"/> is the real one; <c>Main.DisplayBackend</c> holds the one in use.
/// </summary>
public interface IDisplayBackend
{
    /// <summary>The window's mode now.</summary>
    SettingsWindowMode GetWindowMode();

    /// <summary>Sets the window's mode: a normal window, or borderless (non-exclusive) fullscreen.</summary>
    void SetWindowMode(SettingsWindowMode mode);

    /// <summary>Sets the window's size and centres it in the usable area of the screen it is on.</summary>
    void SetWindowSize(WindowSize size);

    /// <summary>Switches vsync on or off.</summary>
    void SetVSync(bool enabled);

    /// <summary>The usable area of the window's screen (decorations not counted); null when unknown (headless).</summary>
    WindowSize? GetUsableScreenSize();
}

/// <summary>
/// <see cref="IDisplayBackend"/> over <see cref="DisplayServer"/> and the main window: borderless fullscreen is
/// <see cref="DisplayServer.WindowMode.Fullscreen"/> (Godot's non-exclusive fullscreen; exclusive fullscreen is
/// never used), windowed is <see cref="DisplayServer.WindowMode.Windowed"/>.
/// </summary>
public sealed class GodotDisplayBackend : IDisplayBackend
{
    public SettingsWindowMode GetWindowMode() =>
        DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen
            ? SettingsWindowMode.BorderlessFullscreen
            : SettingsWindowMode.Windowed;

    public void SetWindowMode(SettingsWindowMode mode) =>
        DisplayServer.WindowSetMode(
            mode == SettingsWindowMode.BorderlessFullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);

    public void SetWindowSize(WindowSize size)
    {
        var requested = new Vector2I(size.Width, size.Height);
        DisplayServer.WindowSetSize(requested);

        // Centre the decorated window in the usable area of its screen; never above or left of that area.
        var usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        if (usable.Size.X <= 0 || usable.Size.Y <= 0)
        {
            return;
        }

        var decorated = DisplayServer.WindowGetSizeWithDecorations();
        var outer = new Vector2I(Mathf.Max(decorated.X, requested.X), Mathf.Max(decorated.Y, requested.Y));
        var decoration = outer - requested;
        var topLeft = usable.Position + ((usable.Size - outer) / 2);
        topLeft = new Vector2I(Mathf.Max(topLeft.X, usable.Position.X), Mathf.Max(topLeft.Y, usable.Position.Y));
        // WindowSetPosition places the client area; the title bar sits above it.
        DisplayServer.WindowSetPosition(topLeft + new Vector2I(decoration.X / 2, decoration.Y - (decoration.X / 2)));
    }

    public void SetVSync(bool enabled) =>
        DisplayServer.WindowSetVsyncMode(enabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

    public WindowSize? GetUsableScreenSize()
    {
        var size = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen()).Size;
        return size.X > 0 && size.Y > 0 ? new WindowSize(size.X, size.Y) : null;
    }
}
