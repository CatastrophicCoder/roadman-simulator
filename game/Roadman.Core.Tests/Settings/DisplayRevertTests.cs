using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="DisplayRevert"/>, the logic of the display confirm-or-revert dialog: <c>docs/specs/menu-settings-save.md</c>,
/// acceptance criterion 20 (a change to window mode, window size or render scale asks, with a 10-second countdown;
/// Keep keeps it; Revert and the countdown running out restore those three values; quality, vsync and the frame cap
/// apply with no dialog).
/// </summary>
public class DisplayRevertTests
{
    private static readonly DisplaySettings Before =
        new(WindowMode.Windowed, new WindowSize(1600, 900), 100, QualityLevel.Medium, true, 0);

    private static readonly DisplaySettings AllChanged =
        new(WindowMode.BorderlessFullscreen, new WindowSize(1920, 1080), 75, QualityLevel.High, false, 60);

    public static TheoryData<string, DisplaySettings> ConfirmChanges => new()
    {
        { "window mode", Before with { WindowMode = WindowMode.BorderlessFullscreen } },
        { "window size", Before with { WindowSize = new WindowSize(1280, 720) } },
        { "render scale", Before with { RenderScale = 50 } },
        { "all", AllChanged },
    };

    public static TheoryData<string, DisplaySettings> NoConfirmChanges => new()
    {
        { "no change", Before with { } },
        { "quality", Before with { Quality = QualityLevel.Low } },
        { "vsync", Before with { VSync = false } },
        { "frame cap", Before with { FrameCap = 30 } },
        { "quality, vsync and frame cap", Before with { Quality = QualityLevel.High, VSync = false, FrameCap = 120 } },
    };

    [Theory]
    [MemberData(nameof(ConfirmChanges))]
    public void Ac20_NeedsConfirm_WindowModeSizeOrRenderScaleChange_IsTrue(string what, DisplaySettings after)
    {
        Assert.True(DisplayRevert.NeedsConfirm(Before, after), what);
        Assert.True(DisplayRevert.NeedsConfirm(after, Before), what);
    }

    [Theory]
    [MemberData(nameof(NoConfirmChanges))]
    public void Ac20_NeedsConfirm_QualityVsyncFrameCapOrNoChange_IsFalse(string what, DisplaySettings after)
    {
        Assert.False(DisplayRevert.NeedsConfirm(Before, after), what);
    }

    [Fact]
    public void NeedsConfirm_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DisplayRevert.NeedsConfirm(null!, Before));
        Assert.Throws<ArgumentNullException>(() => DisplayRevert.NeedsConfirm(Before, null!));
    }

    [Fact]
    public void New_IsNotPending()
    {
        var revert = new DisplayRevert();

        Assert.False(revert.IsPending);
        Assert.Null(revert.Previous);
        Assert.Equal(0, revert.SecondsLeft);
    }

    [Fact]
    public void Ac20_Begin_StartsATenSecondCountdownRememberingThePrevious()
    {
        var revert = new DisplayRevert();

        Assert.True(revert.Begin(Before));

        Assert.True(revert.IsPending);
        Assert.Same(Before, revert.Previous);
        Assert.Equal(10, DisplayRevert.TimeoutSeconds);
        Assert.Equal(10, revert.SecondsLeft);
    }

    [Fact]
    public void Ac20_Tick_CountsDownAndIsCountingBeforeTenSeconds()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);

        Assert.Equal(DisplayRevertTick.Counting, revert.Tick(0));
        Assert.Equal(10, revert.SecondsLeft);
        Assert.Equal(DisplayRevertTick.Counting, revert.Tick(2.5));
        Assert.Equal(7.5, revert.SecondsLeft, 9);
        Assert.Equal(DisplayRevertTick.Counting, revert.Tick(7.4));
        Assert.Equal(0.1, revert.SecondsLeft, 9);
        Assert.True(revert.IsPending);
    }

    [Fact]
    public void Ac20_Tick_TimesOutAtTenSecondsAndOnEveryLaterTick()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);

        Assert.Equal(DisplayRevertTick.Counting, revert.Tick(9.5));
        Assert.Equal(DisplayRevertTick.TimedOut, revert.Tick(0.5));
        Assert.Equal(0, revert.SecondsLeft);
        Assert.Equal(DisplayRevertTick.TimedOut, revert.Tick(1));
        Assert.Equal(DisplayRevertTick.TimedOut, revert.Tick(0));
        Assert.Equal(0, revert.SecondsLeft);
        Assert.True(revert.IsPending);
    }

    [Fact]
    public void Ac20_Tick_OneLongStepPastTenSeconds_TimesOutWithZeroLeft()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);

        Assert.Equal(DisplayRevertTick.TimedOut, revert.Tick(25));
        Assert.Equal(0, revert.SecondsLeft);
    }

    [Fact]
    public void Ac20_Tick_SixtyFramesASecondForTenSeconds_TimesOutOnlyAtTheEnd()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);
        var counting = 0;
        var result = DisplayRevertTick.Counting;
        for (var frame = 0; frame < 700 && result == DisplayRevertTick.Counting; frame++)
        {
            result = revert.Tick(1.0 / 60.0);
            counting += result == DisplayRevertTick.Counting ? 1 : 0;
        }

        Assert.Equal(DisplayRevertTick.TimedOut, result);
        Assert.InRange(counting, 598, 600);
    }

    [Fact]
    public void Tick_NotPending_IsNotPendingAndChangesNothing()
    {
        var revert = new DisplayRevert();

        Assert.Equal(DisplayRevertTick.NotPending, revert.Tick(1));
        Assert.False(revert.IsPending);
        Assert.Equal(0, revert.SecondsLeft);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Tick_NegativeOrNotFinite_ThrowsAndChangesNothing(double delta)
    {
        var revert = new DisplayRevert();
        Assert.Throws<ArgumentOutOfRangeException>(() => revert.Tick(delta));

        revert.Begin(Before);
        revert.Tick(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => revert.Tick(delta));
        Assert.Equal(7, revert.SecondsLeft, 9);
        Assert.True(revert.IsPending);
    }

    [Fact]
    public void Begin_WhilePending_IsRefusedAndChangesNothing()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);
        revert.Tick(4);

        Assert.False(revert.Begin(AllChanged));

        Assert.Same(Before, revert.Previous);
        Assert.Equal(6, revert.SecondsLeft, 9);
    }

    [Fact]
    public void Begin_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DisplayRevert().Begin(null!));
    }

    [Fact]
    public void Ac20_Keep_EndsTheConfirmation()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);
        revert.Tick(3);

        Assert.True(revert.Keep());

        Assert.False(revert.IsPending);
        Assert.Null(revert.Previous);
        Assert.Equal(0, revert.SecondsLeft);
        Assert.Equal(DisplayRevertTick.NotPending, revert.Tick(20));
        Assert.Null(revert.Revert(AllChanged));
    }

    [Fact]
    public void Keep_NotPending_IsFalse()
    {
        Assert.False(new DisplayRevert().Keep());
    }

    [Fact]
    public void Ac20_Revert_RestoresModeSizeAndScaleFromBefore_KeepsQualityVsyncAndCapFromNow()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);

        var restored = revert.Revert(AllChanged);

        Assert.NotNull(restored);
        Assert.Equal(WindowMode.Windowed, restored.WindowMode);
        Assert.Equal(new WindowSize(1600, 900), restored.WindowSize);
        Assert.Equal(100, restored.RenderScale);
        Assert.Equal(QualityLevel.High, restored.Quality);
        Assert.False(restored.VSync);
        Assert.Equal(60, restored.FrameCap);
        Assert.Equal(DisplayRevert.Restore(Before, AllChanged), restored);
        Assert.False(revert.IsPending);
        Assert.Null(revert.Previous);
        Assert.Equal(0, revert.SecondsLeft);
    }

    [Fact]
    public void Ac20_Revert_AfterTheTimeout_Restores()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);
        Assert.Equal(DisplayRevertTick.TimedOut, revert.Tick(10));

        var restored = revert.Revert(AllChanged);

        Assert.Equal(Before with { Quality = QualityLevel.High, VSync = false, FrameCap = 60 }, restored);
        Assert.False(revert.IsPending);
    }

    [Fact]
    public void Revert_NotPending_IsNullAndChangesNothing()
    {
        var revert = new DisplayRevert();

        Assert.Null(revert.Revert(AllChanged));
        Assert.False(revert.IsPending);
    }

    [Fact]
    public void Revert_Null_Throws()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);

        Assert.Throws<ArgumentNullException>(() => revert.Revert(null!));
        Assert.True(revert.IsPending);
    }

    [Fact]
    public void Begin_AfterKeepOrRevert_StartsANewCountdown()
    {
        var revert = new DisplayRevert();
        revert.Begin(Before);
        revert.Keep();
        Assert.True(revert.Begin(AllChanged));
        Assert.Same(AllChanged, revert.Previous);
        Assert.Equal(10, revert.SecondsLeft);

        revert.Revert(Before);
        Assert.True(revert.Begin(Before));
        Assert.Equal(10, revert.SecondsLeft);
    }

    [Fact]
    public void Restore_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DisplayRevert.Restore(null!, Before));
        Assert.Throws<ArgumentNullException>(() => DisplayRevert.Restore(Before, null!));
    }
}
