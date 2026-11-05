using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

/// <summary>
/// <see cref="CanvasStretch.ExpandedSize"/>: the 2D size the <c>--menu</c> screenshots lay the menus out in
/// (<c>docs/specs/menu-settings-save.md</c>, "Screenshots").
/// </summary>
public class CanvasStretchTests
{
    [Theory]
    [InlineData(1152, 648, 1600, 900, 1152, 648)]
    [InlineData(1152, 648, 1152, 648, 1152, 648)]
    [InlineData(1152, 648, 2304, 1296, 1152, 648)]
    public void SameAspect_IsTheBaseSize(int baseW, int baseH, int imageW, int imageH, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), CanvasStretch.ExpandedSize(baseW, baseH, imageW, imageH));
    }

    [Fact]
    public void WiderImage_WidensTheBase()
    {
        // 1920x800 against 1152x648: height limits (scale 800/648), so the width grows to 1920 * 648 / 800 = 1555.2.
        Assert.Equal((1555, 648), CanvasStretch.ExpandedSize(1152, 648, 1920, 800));
    }

    [Fact]
    public void TallerImage_HeightensTheBase()
    {
        // 1152x1152 against 1152x648: width limits (scale 1), so the height is the image's 1152.
        Assert.Equal((1152, 1152), CanvasStretch.ExpandedSize(1152, 648, 1152, 1152));
    }

    [Fact]
    public void HalfPixel_RoundsAwayFromZero()
    {
        // 5x4 against 2x2: height limits (scale 2), so 2.5 x 2; the half rounds up to 3.
        Assert.Equal((3, 2), CanvasStretch.ExpandedSize(2, 2, 5, 4));
    }

    [Theory]
    [InlineData(0, 648, 1600, 900)]
    [InlineData(1152, -1, 1600, 900)]
    [InlineData(1152, 648, 0, 900)]
    [InlineData(1152, 648, 1600, 0)]
    public void NonPositiveSize_GivesTheImageSize(int baseW, int baseH, int imageW, int imageH)
    {
        Assert.Equal((imageW, imageH), CanvasStretch.ExpandedSize(baseW, baseH, imageW, imageH));
    }
}
