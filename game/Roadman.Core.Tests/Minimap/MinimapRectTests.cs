using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapRect.IsInsideCircle"/>, which the minimap calls for every stored label each frame.</summary>
public class MinimapRectTests
{
    [Fact]
    public void IsInsideCircle_AgreesWithTheCorners()
    {
        var random = new Random(21);
        for (var i = 0; i < 2000; i++)
        {
            var rect = new MinimapRect(
                new PanelPoint(-60 + (random.NextDouble() * 120), -60 + (random.NextDouble() * 120)),
                random.NextDouble() * 30,
                random.NextDouble() * 10,
                (random.NextDouble() - 0.5) * 2 * Math.PI);
            var radius = 20 + (random.NextDouble() * 60);

            var expected = rect.Corners().All(corner => corner.Length <= radius);

            Assert.Equal(expected, rect.IsInsideCircle(radius));
        }
    }

    [Fact]
    public void IsInsideCircle_TurnedRectangle_UsesItsTurnedCorners()
    {
        var rect = new MinimapRect(new PanelPoint(0, 30), 10, 1, Math.PI / 2);

        // 20 × 2 centred 30 below the panel centre, turned 90°: it spans y 20..40 and x −1..1, so the farthest corners are √(1 + 1600) ≈ 40.01 out.
        Assert.False(rect.IsInsideCircle(40));
        Assert.True(rect.IsInsideCircle(40.02));
    }

    [Fact]
    public void IsInsideCircle_NegativeRadius_IsFalse()
    {
        Assert.False(MinimapRect.AxisAligned(new PanelPoint(0, 0), 0, 0).IsInsideCircle(-1));
    }
}
