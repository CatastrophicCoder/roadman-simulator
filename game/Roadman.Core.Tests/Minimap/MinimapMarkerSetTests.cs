using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapMarkerSet"/> and <see cref="MinimapMarker"/>: <c>docs/specs/minimap.md</c>, acceptance criterion 9.</summary>
public class MinimapMarkerSetTests
{
    [Fact]
    public void Ac9_AddOfARepeatedId_ThrowsNamingItAndChangesNothing()
    {
        var set = new MinimapMarkerSet();
        var first = new MinimapMarker("quest:shop", 1, 2);
        set.Add(first);
        var changes = Count(set);

        var ex = Assert.Throws<ArgumentException>(() => set.Add(new MinimapMarker("quest:shop", 5, 6, clampToRim: true)));

        Assert.Contains("'quest:shop'", ex.Message);
        Assert.Equal(0, changes());
        Assert.Same(first, Assert.Single(set.All));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Ac9_ABlankId_Throws(string id)
    {
        var set = new MinimapMarkerSet();
        var changes = Count(set);

        Assert.Throws<ArgumentException>(() => set.Add(new MinimapMarker(id, 0, 0)));
        Assert.Throws<ArgumentException>(() => new MinimapMarker("ok", 0, 0) with { Id = id });

        Assert.Empty(set.All);
        Assert.Equal(0, changes());
    }

    [Fact]
    public void Ac9_RemoveOfAnUnknownId_ReturnsFalseWithoutAChange()
    {
        var set = new MinimapMarkerSet();
        set.Add(new MinimapMarker("a", 0, 0));
        var changes = Count(set);

        Assert.False(set.Remove("b"));
        Assert.Equal(0, changes());
        Assert.True(set.Remove("a"));
        Assert.Equal(1, changes());
        Assert.False(set.Remove("a"));
        Assert.Equal(1, changes());
    }

    [Fact]
    public void Ac9_All_IsOrderedById()
    {
        var set = new MinimapMarkerSet();
        foreach (var id in new[] { "quest:b", "Quest:z", "quest:a", "marker:10", "marker:9" })
        {
            set.Add(new MinimapMarker(id, 0, 0));
        }

        Assert.Equal(["Quest:z", "marker:10", "marker:9", "quest:a", "quest:b"], set.All.Select(marker => marker.Id));
    }

    [Fact]
    public void Ac9_Changed_FiresOncePerChangeAndNotForANoOp()
    {
        var set = new MinimapMarkerSet();
        var changes = Count(set);

        set.Clear(); // empty: a no-op
        Assert.Equal(0, changes());

        set.Add(new MinimapMarker("a", 0, 0));
        Assert.Equal(1, changes());
        set.Add(new MinimapMarker("b", 0, 0));
        Assert.Equal(2, changes());

        set.Clear(); // two markers, one change
        Assert.Equal(3, changes());
        Assert.Empty(set.All);

        set.Clear();
        Assert.False(set.Remove("a"));
        Assert.Equal(3, changes());
    }

    [Fact]
    public void Marker_KeepsItsValues()
    {
        var marker = new MinimapMarker("quest:shop", 12.5, -40, MinimapMarkerKind.Generic, clampToRim: true);

        Assert.Equal("quest:shop", marker.Id);
        Assert.Equal(12.5, marker.X);
        Assert.Equal(-40, marker.Z);
        Assert.Equal(MinimapMarkerKind.Generic, marker.Kind);
        Assert.True(marker.ClampToRim);
        Assert.False(new MinimapMarker("b", 0, 0).ClampToRim);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void Marker_NonFinitePosition_Throws(double x, double z)
    {
        Assert.Throws<ArgumentException>(() => new MinimapMarker("a", x, z));
    }

    [Fact]
    public void TryGetAndContains_FindMarkersById()
    {
        var set = new MinimapMarkerSet();
        var marker = new MinimapMarker("a", 1, 2);
        set.Add(marker);

        Assert.True(set.Contains("a"));
        Assert.True(set.TryGet("a", out var found));
        Assert.Same(marker, found);
        Assert.False(set.TryGet("b", out _));
        Assert.Equal(1, set.Count);
    }

    /// <summary>Subscribes to <see cref="MinimapMarkerSet.Changed"/>; the function returns the count so far.</summary>
    private static Func<int> Count(MinimapMarkerSet set)
    {
        var count = 0;
        set.Changed += () => count++;
        return () => count;
    }
}
