using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="QualityPreset"/> and the effective render scale: <c>docs/specs/menu-settings-save.md</c>,
/// "QualityPreset" and acceptance criterion 7.
/// </summary>
public class QualityPresetTests
{
    [Fact]
    public void Presets_HoldTheSpecTable()
    {
        Assert.Equal(
            new QualityPreset(QualityLevel.Low, true, 1, 300, 2048, false, AntiAliasing.Fxaa, 0.75),
            QualityPreset.For(QualityLevel.Low));
        Assert.Equal(
            new QualityPreset(QualityLevel.Medium, true, 2, 800, 4096, false, AntiAliasing.Msaa2x, 1.0),
            QualityPreset.For(QualityLevel.Medium));
        Assert.Equal(
            new QualityPreset(QualityLevel.High, true, 4, 1500, 4096, true, AntiAliasing.Msaa4x, 1.0),
            QualityPreset.For(QualityLevel.High));
    }

    [Theory]
    [InlineData(QualityLevel.Low, 0.75)]
    [InlineData(QualityLevel.Medium, 1.0)]
    [InlineData(QualityLevel.High, 1.0)]
    public void EffectiveRenderScale_Fullscreen_IsFactorTimesRenderScale(QualityLevel level, double factor)
    {
        for (var percent = 50; percent <= 100; percent += 5)
        {
            Assert.Equal(factor * percent / 100.0, QualityPreset.EffectiveRenderScale(level, percent, fullscreen: true), 12);
        }
    }

    [Theory]
    [InlineData(QualityLevel.Low, 0.75)]
    [InlineData(QualityLevel.Medium, 1.0)]
    [InlineData(QualityLevel.High, 1.0)]
    public void EffectiveRenderScale_Windowed_IsTheFactorAlone(QualityLevel level, double factor)
    {
        for (var percent = 50; percent <= 100; percent += 5)
        {
            Assert.Equal(factor, QualityPreset.EffectiveRenderScale(level, percent, fullscreen: false));
        }
    }

    [Fact]
    public void EffectiveRenderScale_LowAt50PercentFullscreen_Is0Point375()
    {
        Assert.Equal(0.375, QualityPreset.Low.EffectiveRenderScale(50, fullscreen: true), 12);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(77)]
    [InlineData(105)]
    public void EffectiveRenderScale_InvalidRenderScale_Throws(int percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityPreset.Medium.EffectiveRenderScale(percent, fullscreen: true));
    }

    [Fact]
    public void For_UndefinedLevel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityPreset.For((QualityLevel)99));
    }
}
