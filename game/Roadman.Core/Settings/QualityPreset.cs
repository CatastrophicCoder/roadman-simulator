namespace Roadman.Core.Settings;

/// <summary>The anti-aliasing a quality preset uses.</summary>
public enum AntiAliasing
{
    /// <summary>FXAA (screen-space), MSAA off.</summary>
    Fxaa,

    /// <summary>MSAA 2×, FXAA off.</summary>
    Msaa2x,

    /// <summary>MSAA 4×, FXAA off.</summary>
    Msaa4x,
}

/// <summary>
/// The values one quality preset applies (<c>docs/specs/menu-settings-save.md</c>, "QualityPreset"); starting
/// values, tuned in the playtest.
/// </summary>
/// <param name="Level">The preset.</param>
/// <param name="DirectionalShadows">The sun casts shadows.</param>
/// <param name="ShadowSplits">Directional shadow splits: 1, 2 or 4.</param>
/// <param name="ShadowMaxDistance">Directional shadow max distance in metres.</param>
/// <param name="ShadowAtlasSize">Directional shadow atlas size in pixels.</param>
/// <param name="SoftShadowFilterHigh">Use the high soft shadow filter quality (High only).</param>
/// <param name="AntiAliasing">The anti-aliasing.</param>
/// <param name="RenderScaleFactor">The 3D render scale factor of the preset.</param>
public sealed record QualityPreset(
    QualityLevel Level,
    bool DirectionalShadows,
    int ShadowSplits,
    double ShadowMaxDistance,
    int ShadowAtlasSize,
    bool SoftShadowFilterHigh,
    AntiAliasing AntiAliasing,
    double RenderScaleFactor)
{
    public static QualityPreset Low { get; } = new(QualityLevel.Low, true, 1, 300, 2048, false, AntiAliasing.Fxaa, 0.75);

    public static QualityPreset Medium { get; } = new(QualityLevel.Medium, true, 2, 800, 4096, false, AntiAliasing.Msaa2x, 1.0);

    /// <summary>High; its shadow distance is today's <c>World.SunShadowMaxDistance</c> (1500 m).</summary>
    public static QualityPreset High { get; } = new(QualityLevel.High, true, 4, 1500, 4096, true, AntiAliasing.Msaa4x, 1.0);

    /// <exception cref="ArgumentOutOfRangeException">Not a defined <see cref="QualityLevel"/>.</exception>
    public static QualityPreset For(QualityLevel level) =>
        level switch
        {
            QualityLevel.Low => Low,
            QualityLevel.Medium => Medium,
            QualityLevel.High => High,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "not a quality level"),
        };

    /// <summary>
    /// The 3D render scale (<c>Viewport.Scaling3DScale</c>): <see cref="RenderScaleFactor"/> ×
    /// <paramref name="renderScalePercent"/> / 100 in fullscreen, the factor alone in a window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="renderScalePercent"/> isn't valid (<see cref="SettingsRules.IsValidRenderScale"/>).
    /// </exception>
    public double EffectiveRenderScale(int renderScalePercent, bool fullscreen)
    {
        if (!SettingsRules.IsValidRenderScale(renderScalePercent))
        {
            throw new ArgumentOutOfRangeException(nameof(renderScalePercent), renderScalePercent, "must be 50..100 in steps of 5");
        }

        return fullscreen ? RenderScaleFactor * renderScalePercent / 100.0 : RenderScaleFactor;
    }

    /// <summary><see cref="For"/>(<paramref name="level"/>).<see cref="EffectiveRenderScale(int, bool)"/>.</summary>
    public static double EffectiveRenderScale(QualityLevel level, int renderScalePercent, bool fullscreen) =>
        For(level).EffectiveRenderScale(renderScalePercent, fullscreen);
}
