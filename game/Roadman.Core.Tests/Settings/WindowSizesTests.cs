using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="WindowSizes"/>: <c>docs/specs/menu-settings-save.md</c>, acceptance criterion 7.
/// </summary>
public class WindowSizesTests
{
    [Fact]
    public void All_IsTheFourSpecSizesAscending()
    {
        Assert.Equal(
            [new WindowSize(1280, 720), new WindowSize(1600, 900), new WindowSize(1920, 1080), new WindowSize(2560, 1440)],
            WindowSizes.All);
    }

    [Theory]
    [InlineData(3840, 2160, 4)]
    [InlineData(2560, 1440, 4)]
    [InlineData(2560, 1415, 3)]
    [InlineData(1920, 1080, 3)]
    [InlineData(1920, 1050, 2)]
    [InlineData(1600, 900, 2)]
    [InlineData(1599, 900, 1)]
    [InlineData(1280, 720, 1)]
    public void Fitting_GivesTheSizesThatFitInAscendingOrder(int width, int height, int expectedCount)
    {
        var fitting = WindowSizes.Fitting(new WindowSize(width, height));

        Assert.Equal(WindowSizes.All.Take(expectedCount), fitting);
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(1280, 719)]
    [InlineData(1279, 1000)]
    [InlineData(0, 0)]
    public void Fitting_NoneFits_GivesThe1280x720(int width, int height)
    {
        Assert.Equal([new WindowSize(1280, 720)], WindowSizes.Fitting(new WindowSize(width, height)));
    }

    [Fact]
    public void Fitting_TallNarrowScreen_ChecksBothDimensions()
    {
        // 1600 wide but only 1000 high: 1920x1080 is too wide and too high; 1600x900 fits.
        Assert.Equal(
            [new WindowSize(1280, 720), new WindowSize(1600, 900)],
            WindowSizes.Fitting(new WindowSize(1600, 2000)));
    }
}
