using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="DisplayChange.Plan"/>: when applying the display group resends the window mode and size
/// (<c>docs/specs/menu-settings-save.md</c>, acceptance criterion 19).
/// </summary>
public class DisplayChangeTests
{
    private static readonly DisplaySettings Windowed =
        new(WindowMode.Windowed, new WindowSize(1600, 900), 100, QualityLevel.Medium, true, 0);

    private static readonly DisplaySettings Fullscreen = Windowed with { WindowMode = WindowMode.BorderlessFullscreen };

    [Fact]
    public void Startup_Windowed_SendsTheSize_AndTheModeOnlyWhenTheWindowDiffers()
    {
        Assert.Equal(new DisplayApply(null, Windowed.WindowSize), DisplayChange.Plan(Windowed, WindowMode.Windowed, null));
        Assert.Equal(
            new DisplayApply(WindowMode.Windowed, Windowed.WindowSize),
            DisplayChange.Plan(Windowed, WindowMode.BorderlessFullscreen, null));
    }

    [Fact]
    public void Startup_Fullscreen_SendsTheModeAndNoSize()
    {
        Assert.Equal(
            new DisplayApply(WindowMode.BorderlessFullscreen, null),
            DisplayChange.Plan(Fullscreen, WindowMode.Windowed, null));
        Assert.Equal(new DisplayApply(null, null), DisplayChange.Plan(Fullscreen, WindowMode.BorderlessFullscreen, null));
    }

    [Fact]
    public void SizeChangeInAWindow_SendsTheSizeOnly()
    {
        var next = Windowed with { WindowSize = new WindowSize(1920, 1080) };

        Assert.Equal(new DisplayApply(null, next.WindowSize), DisplayChange.Plan(next, WindowMode.Windowed, Windowed));
    }

    [Fact]
    public void BackToAWindow_SendsTheModeAndTheSize()
    {
        Assert.Equal(
            new DisplayApply(WindowMode.Windowed, Windowed.WindowSize),
            DisplayChange.Plan(Windowed, WindowMode.BorderlessFullscreen, Fullscreen));
    }

    [Fact]
    public void ToFullscreen_SendsTheModeAndNoSize()
    {
        Assert.Equal(
            new DisplayApply(WindowMode.BorderlessFullscreen, null),
            DisplayChange.Plan(Fullscreen, WindowMode.Windowed, Windowed));
    }

    public static TheoryData<string, DisplaySettings> OtherChanges => new()
    {
        { "no change", Windowed with { } },
        { "vsync", Windowed with { VSync = false } },
        { "frame cap", Windowed with { FrameCap = 60 } },
        { "quality", Windowed with { Quality = QualityLevel.High } },
        { "render scale", Windowed with { RenderScale = 50 } },
    };

    [Theory]
    [MemberData(nameof(OtherChanges))]
    public void OtherChangesInAWindow_SendNeitherModeNorSize(string what, DisplaySettings next)
    {
        Assert.True(DisplayChange.Plan(next, WindowMode.Windowed, Windowed) == new DisplayApply(null, null), what);
    }

    [Fact]
    public void NullDisplay_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DisplayChange.Plan(null!, WindowMode.Windowed, null));
    }
}
