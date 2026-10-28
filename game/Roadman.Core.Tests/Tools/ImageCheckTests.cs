using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

public class ImageCheckTests
{
    [Fact]
    public void IsFlatColour_SingleColour_IsTrue()
    {
        byte[] rgba = [10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255];

        Assert.True(ImageCheck.IsFlatColour(rgba, 4));
    }

    [Fact]
    public void IsFlatColour_TwoColours_IsFalse()
    {
        byte[] rgb = [0, 0, 0, 0, 0, 0, 0, 0, 1];

        Assert.False(ImageCheck.IsFlatColour(rgb, 3));
    }

    [Fact]
    public void IsFlatColour_DifferenceWithinTolerance_IsTrue()
    {
        byte[] rgb = [100, 100, 100, 102, 99, 101];

        Assert.True(ImageCheck.IsFlatColour(rgb, 3, tolerance: 2));
        Assert.False(ImageCheck.IsFlatColour(rgb, 3, tolerance: 1));
    }

    [Fact]
    public void IsFlatColour_ChannelsAreComparedSeparately()
    {
        // Each pixel is grey-ish, but the red channel alone spans 0..200.
        byte[] rgb = [0, 100, 100, 200, 100, 100];

        Assert.False(ImageCheck.IsFlatColour(rgb, 3, tolerance: 10));
    }

    [Fact]
    public void IsFlatColour_LengthNotMultipleOfChannels_Throws()
    {
        Assert.Throws<ArgumentException>(() => ImageCheck.IsFlatColour(new byte[5], 4));
    }
}
