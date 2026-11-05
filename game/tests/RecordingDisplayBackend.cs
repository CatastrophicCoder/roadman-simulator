using System.Collections.Generic;
using Roadman.Core.Settings;
using Roadman.Settings;

namespace Roadman.Tests;

/// <summary>
/// An <see cref="IDisplayBackend"/> for tests: records every call in <see cref="Calls"/> (as text, in order) and keeps
/// the window mode, size and vsync it was given, since the headless display server ignores them. Its usable screen
/// size is <see cref="UsableSize"/> (configurable; null = unknown).
/// </summary>
public sealed class RecordingDisplayBackend : IDisplayBackend
{
    /// <summary>Every call, in order: <c>SetWindowMode(BorderlessFullscreen)</c>, <c>SetWindowSize(1600×900)</c>, <c>SetVSync(True)</c>.</summary>
    public List<string> Calls { get; } = [];

    public WindowMode Mode { get; set; } = WindowMode.Windowed;

    /// <summary>The last size set; null before any.</summary>
    public WindowSize? Size { get; private set; }

    /// <summary>The last vsync value set; null before any.</summary>
    public bool? VSync { get; private set; }

    /// <summary>What <see cref="GetUsableScreenSize"/> returns.</summary>
    public WindowSize? UsableSize { get; set; } = new(2560, 1415);

    public WindowMode GetWindowMode() => Mode;

    public void SetWindowMode(WindowMode mode)
    {
        Calls.Add($"SetWindowMode({mode})");
        Mode = mode;
    }

    public void SetWindowSize(WindowSize size)
    {
        Calls.Add($"SetWindowSize({size})");
        Size = size;
    }

    public void SetVSync(bool enabled)
    {
        Calls.Add($"SetVSync({enabled})");
        VSync = enabled;
    }

    public WindowSize? GetUsableScreenSize() => UsableSize;
}
