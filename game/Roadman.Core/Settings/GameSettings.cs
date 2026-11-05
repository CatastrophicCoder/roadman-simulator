using System.Globalization;

namespace Roadman.Core.Settings;

/// <summary>How the game window is shown.</summary>
public enum WindowMode
{
    Windowed,
    BorderlessFullscreen,
}

/// <summary>A graphics quality preset (<see cref="QualityPreset"/>).</summary>
public enum QualityLevel
{
    Low,
    Medium,
    High,
}

/// <summary>The screen corner the minimap uses (read by the minimap spec).</summary>
public enum MinimapCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>A window size in pixels.</summary>
public readonly record struct WindowSize(int Width, int Height)
{
    /// <summary>True when this size fits inside <paramref name="area"/> in both dimensions.</summary>
    public bool FitsIn(WindowSize area) => Width <= area.Width && Height <= area.Height;

    /// <summary><c>1600×900</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height}");
}

/// <summary>The window sizes the settings offer.</summary>
public static class WindowSizes
{
    /// <summary>The window size when it fits the screen.</summary>
    public static readonly WindowSize Default = new(1600, 900);

    /// <summary>1280×720, 1600×900, 1920×1080, 2560×1440, ascending.</summary>
    public static IReadOnlyList<WindowSize> All { get; } =
        [new(1280, 720), Default, new(1920, 1080), new(2560, 1440)];

    /// <summary>
    /// The sizes of <see cref="All"/> that fit <paramref name="usable"/> (the screen's usable area, window decorations
    /// not counted), ascending; just the smallest (1280×720) when none fits.
    /// </summary>
    public static IReadOnlyList<WindowSize> Fitting(WindowSize usable)
    {
        var fitting = All.Where(size => size.FitsIn(usable)).ToList();
        return fitting.Count > 0 ? fitting : [All[0]];
    }

    public static bool IsKnown(WindowSize size) => All.Contains(size);

    /// <summary>
    /// The default size: <see cref="Default"/> when <paramref name="usable"/> is null or it fits, else the largest
    /// of <see cref="Fitting"/>.
    /// </summary>
    public static WindowSize DefaultFor(WindowSize? usable) =>
        usable is not { } area || Default.FitsIn(area) ? Default : Fitting(area)[^1];
}

/// <summary>Value ranges and steps of the settings, shared by the reader, the writer and the settings view.</summary>
public static class SettingsRules
{
    public const int MinRenderScale = 50;
    public const int MaxRenderScale = 100;
    public const int RenderScaleStep = 5;
    public const int MinVolume = 0;
    public const int MaxVolume = 100;
    public const int VolumeStep = 5;
    public const double MinSensitivity = 0.25;
    public const double MaxSensitivity = 3.0;
    public const double SensitivityStep = 0.05;

    /// <summary>How far a sensitivity may be from a step and still count as on it.</summary>
    public const double SensitivityTolerance = 1e-6;

    /// <summary>The frame caps: 0 (off), 30, 60, 120.</summary>
    public static IReadOnlyList<int> FrameCaps { get; } = [0, 30, 60, 120];

    public static bool IsValidRenderScale(int percent) =>
        percent is >= MinRenderScale and <= MaxRenderScale && percent % RenderScaleStep == 0;

    public static bool IsValidVolume(int percent) =>
        percent is >= MinVolume and <= MaxVolume && percent % VolumeStep == 0;

    public static bool IsValidFrameCap(int cap) => FrameCaps.Contains(cap);

    /// <summary>
    /// True when <paramref name="multiplier"/> is finite, in <see cref="MinSensitivity"/>..<see cref="MaxSensitivity"/>
    /// and within <see cref="SensitivityTolerance"/> of a multiple of <see cref="SensitivityStep"/>.
    /// </summary>
    public static bool IsValidSensitivity(double multiplier)
    {
        if (!double.IsFinite(multiplier)
            || multiplier < MinSensitivity - SensitivityTolerance
            || multiplier > MaxSensitivity + SensitivityTolerance)
        {
            return false;
        }

        return Math.Abs(multiplier - SnapSensitivity(multiplier)) <= SensitivityTolerance;
    }

    /// <summary>
    /// The nearest step (k / 20, the double nearest the decimal value, so 1.15 stays 1.15), clamped to the range.
    /// </summary>
    public static double SnapSensitivity(double multiplier) =>
        Math.Clamp(Math.Round(multiplier / SensitivityStep, MidpointRounding.AwayFromZero), 5, 60) / 20.0;
}

/// <summary>The display group.</summary>
/// <param name="WindowMode">Windowed or borderless fullscreen.</param>
/// <param name="WindowSize">The window size, one of <see cref="WindowSizes.All"/>; used in a window.</param>
/// <param name="RenderScale">3D render scale in percent, 50..100 in steps of 5; applies in fullscreen only.</param>
/// <param name="Quality">The quality preset.</param>
/// <param name="VSync">VSync on or off.</param>
/// <param name="FrameCap">The frame cap in frames per second, 0 for off; one of <see cref="SettingsRules.FrameCaps"/>.</param>
public sealed record DisplaySettings(
    WindowMode WindowMode,
    WindowSize WindowSize,
    int RenderScale,
    QualityLevel Quality,
    bool VSync,
    int FrameCap);

/// <summary>The audio group: bus volumes in percent, 0..100 in steps of 5.</summary>
public sealed record AudioSettings(int MasterVolume, int MusicVolume, int EffectsVolume);

/// <summary>The gameplay group.</summary>
/// <param name="InvertMouseY">Invert the mouse's vertical camera orbit.</param>
/// <param name="InvertStickY">Invert the right stick's vertical camera orbit.</param>
/// <param name="MouseSensitivity">Multiplier of the camera's default mouse sensitivity, 0.25..3.0 in steps of 0.05.</param>
/// <param name="StickSensitivity">Multiplier of the camera's default stick sensitivity, 0.25..3.0 in steps of 0.05.</param>
/// <param name="MinimapCorner">The minimap's corner.</param>
public sealed record GameplaySettings(
    bool InvertMouseY,
    bool InvertStickY,
    double MouseSensitivity,
    double StickSensitivity,
    MinimapCorner MinimapCorner);

/// <summary>
/// The player's settings (<c>user://settings.json</c>), immutable. Read with <see cref="SettingsReader"/>, write with
/// <see cref="SettingsWriter"/>; change one value with <c>with</c> on the group.
/// </summary>
public sealed record GameSettings(DisplaySettings Display, AudioSettings Audio, GameplaySettings Gameplay)
{
    /// <summary>The settings file format version this game writes and reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The defaults with a 1600×900 window (no screen known).</summary>
    public static GameSettings Default { get; } = Defaults();

    /// <summary>The format version (always <see cref="CurrentFormatVersion"/>; not part of equality).</summary>
    public int FormatVersion => CurrentFormatVersion;

    /// <summary>
    /// The spec's defaults: windowed, <see cref="WindowSizes.DefaultFor"/> <paramref name="usableScreen"/>
    /// (1600×900, or the largest that fits), render scale 100, quality Medium, vsync on, no frame cap; volumes 100,
    /// 70, 80; no inverts, sensitivities 1.0, minimap bottom left.
    /// </summary>
    /// <param name="usableScreen">The screen's usable area, or null when unknown (gives 1600×900).</param>
    public static GameSettings Defaults(WindowSize? usableScreen = null) =>
        new(
            new DisplaySettings(WindowMode.Windowed, WindowSizes.DefaultFor(usableScreen), 100, QualityLevel.Medium, true, 0),
            new AudioSettings(100, 70, 80),
            new GameplaySettings(false, false, 1.0, 1.0, MinimapCorner.BottomLeft));
}
