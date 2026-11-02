using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class PlanExtentTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, -20)]
    [InlineData(10, 20)]
    [InlineData(-10, 20)]
    public void Contains_InsideOrOnEdge_IsTrue(double x, double z)
    {
        var extent = new PlanExtent(-10, -20, 10, 20);

        Assert.True(extent.Contains(x, z));
    }

    [Theory]
    [InlineData(-10.01, 0)]
    [InlineData(10.01, 0)]
    [InlineData(0, -20.01)]
    [InlineData(0, 20.01)]
    public void Contains_Outside_IsFalse(double x, double z)
    {
        var extent = new PlanExtent(-10, -20, 10, 20);

        Assert.False(extent.Contains(x, z));
    }

    [Fact]
    public void UnionOf_CoversEveryExtent()
    {
        var union = PlanExtent.UnionOf([new PlanExtent(0, 0, 1, 1), new PlanExtent(-5, 2, -4, 3), new PlanExtent(0, -7, 9, 0)]);

        Assert.Equal(new PlanExtent(-5, -7, 9, 3), union);
    }

    [Fact]
    public void UnionOf_None_IsNull()
    {
        Assert.Null(PlanExtent.UnionOf([]));
    }
}
