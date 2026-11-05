using Godot;
using Roadman.Core.Settings;
using SettingsWindowMode = Roadman.Core.Settings.WindowMode;

namespace Roadman.Settings;

/// <summary>
/// Applies <see cref="GameSettings"/> to the engine (<c>docs/specs/menu-settings-save.md</c>, "Settings view"): the
/// display group through an <see cref="IDisplayBackend"/> and on a <see cref="Viewport"/>, the audio buses on
/// <see cref="AudioServer"/>. The camera values are applied by <c>World</c> at every spawn and load
/// (<c>World.CameraSettings</c>), the sun's quality values by <c>World.QualitySettings</c>.
/// </summary>
public static class SettingsApplier
{
    public const string MasterBus = "Master";
    public const string MusicBus = "Music";
    public const string EffectsBus = "Effects";

    /// <summary>The project setting with the directional shadow atlas depth (16 or 32 bits).</summary>
    public const string DirectionalShadow16BitsSetting = "rendering/lights_and_shadows/directional_shadow/16_bits";

    /// <summary>The project setting with the directional soft shadow filter quality (Godot's default: soft low).</summary>
    public const string DirectionalSoftShadowQualitySetting =
        "rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality";

    /// <summary>Applies the display group (<see cref="ApplyDisplay"/>, no previous values) and the audio group.</summary>
    public static void ApplyAll(GameSettings settings, IDisplayBackend backend, Viewport viewport)
    {
        ApplyDisplay(settings.Display, backend, viewport);
        ApplyAudio(settings.Audio);
    }

    /// <summary>
    /// Applies the display group: the window mode and the window size (centred) as <see cref="DisplayChange.Plan"/>
    /// decides (the mode only when the backend's differs; the size in a window only at startup or when the size or mode
    /// changed, so a vsync change doesn't move the window), vsync, <see cref="Engine.MaxFps"/> (0 is no cap) and the quality preset
    /// (<see cref="ApplyQuality"/>).
    /// </summary>
    /// <param name="display">The values to apply.</param>
    /// <param name="backend">The window operations.</param>
    /// <param name="viewport">The viewport that renders the game (the main window).</param>
    /// <param name="previous">The values applied before, or null to apply everything (startup).</param>
    public static void ApplyDisplay(DisplaySettings display, IDisplayBackend backend, Viewport viewport, DisplaySettings? previous = null)
    {
        var plan = DisplayChange.Plan(display, backend.GetWindowMode(), previous);
        if (plan.WindowMode is { } mode)
        {
            backend.SetWindowMode(mode);
        }

        if (plan.WindowSize is { } size)
        {
            backend.SetWindowSize(size);
        }

        backend.SetVSync(display.VSync);
        Engine.MaxFps = display.FrameCap;
        ApplyQuality(display, viewport);
    }

    /// <summary>
    /// Applies <see cref="QualityPreset.For"/> the display's quality: on <paramref name="viewport"/> the MSAA, FXAA and
    /// 3D render scale (<see cref="QualityPreset.EffectiveRenderScale(QualityLevel, int, bool)"/>: the preset's factor ×
    /// the render scale in fullscreen, the factor alone in a window); on <see cref="RenderingServer"/> the directional
    /// shadow atlas size (depth from <see cref="DirectionalShadow16BitsSetting"/>) and the soft shadow filter: soft high
    /// for High, the project's <see cref="DirectionalSoftShadowQualitySetting"/> (Godot's default, soft low) for Low and
    /// Medium, so those keep today's filter. The sun's splits and distance are <c>World.QualitySettings</c>.
    /// </summary>
    public static void ApplyQuality(DisplaySettings display, Viewport viewport)
    {
        var preset = QualityPreset.For(display.Quality);
        viewport.Msaa3D = preset.AntiAliasing switch
        {
            AntiAliasing.Msaa2x => Viewport.Msaa.Msaa2X,
            AntiAliasing.Msaa4x => Viewport.Msaa.Msaa4X,
            _ => Viewport.Msaa.Disabled,
        };
        viewport.ScreenSpaceAA = preset.AntiAliasing == AntiAliasing.Fxaa
            ? Viewport.ScreenSpaceAAEnum.Fxaa
            : Viewport.ScreenSpaceAAEnum.Disabled;
        viewport.Scaling3DScale = (float)QualityPreset.EffectiveRenderScale(
            display.Quality, display.RenderScale, display.WindowMode == SettingsWindowMode.BorderlessFullscreen);

        var sixteenBits = ProjectSettings.GetSetting(DirectionalShadow16BitsSetting, true).AsBool();
        RenderingServer.DirectionalShadowAtlasSetSize(preset.ShadowAtlasSize, sixteenBits);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(SoftShadowQuality(preset));
    }

    /// <summary>The directional soft shadow filter quality <see cref="ApplyQuality"/> sets for <paramref name="preset"/>.</summary>
    public static RenderingServer.ShadowQuality SoftShadowQuality(QualityPreset preset) =>
        preset.SoftShadowFilterHigh
            ? RenderingServer.ShadowQuality.SoftHigh
            : (RenderingServer.ShadowQuality)ProjectSettings.GetSetting(
                DirectionalSoftShadowQualitySetting, (int)RenderingServer.ShadowQuality.SoftLow).AsInt32();

    /// <summary>
    /// Sets each bus's volume (<see cref="VolumeMath.ToDb"/>) and mute flag (<see cref="VolumeMath.IsMuted"/>, 0 %). A
    /// bus missing from the layout is skipped with a warning.
    /// </summary>
    public static void ApplyAudio(AudioSettings audio)
    {
        SetBus(MasterBus, audio.MasterVolume);
        SetBus(MusicBus, audio.MusicVolume);
        SetBus(EffectsBus, audio.EffectsVolume);
    }

    private static void SetBus(string name, int percent)
    {
        var index = AudioServer.GetBusIndex(name);
        if (index < 0)
        {
            GD.PushWarning($"Settings: no audio bus '{name}' (default_bus_layout.tres); its volume isn't applied");
            return;
        }

        var muted = VolumeMath.IsMuted(percent);
        AudioServer.SetBusMute(index, muted);
        if (!muted)
        {
            AudioServer.SetBusVolumeDb(index, (float)VolumeMath.ToDb(percent));
        }
    }
}
