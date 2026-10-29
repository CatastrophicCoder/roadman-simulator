using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class BuildingBodiesTests
{
    private const string Tile = "tile_x0_y0";

    private static readonly string[] Expected =
        ["bld_osm_way_1_c0-colonly", "bld_osm_relation_3_c0-colonly", "bld_osm_relation_3_c1-colonly", "bld_osm_way_2_c0-colonly"];

    private static TileBodyCheck ResolveAndCheck(IReadOnlyList<string> bodyNames, IReadOnlyList<string> expected)
    {
        var bodies = BuildingBodies.Resolve(bodyNames, new HashSet<string>(expected));
        return BuildingBodies.Check(Tile, bodies, expected);
    }

    [Theory]
    [InlineData("bld_osm_way_1_c0", "bld_osm_way_1_c0-colonly")]
    [InlineData("bld_osm_relation_3_c1", "bld_osm_relation_3_c1-colonly")]
    [InlineData("bld_osm_way_1_c0-colonly", null)]
    [InlineData("@StaticBody3D@12", null)]
    [InlineData("terrain", null)]
    [InlineData("", null)]
    public void CollisionNodeOf_ReappendsTheStrippedSuffixToBuildingNamesOnly(string bodyName, string? expected)
    {
        Assert.Equal(expected, BuildingBodies.CollisionNodeOf(bodyName));
    }

    [Fact]
    public void ImportedNames_AreABijection()
    {
        string[] names = ["bld_osm_way_1_c0", "bld_osm_relation_3_c0", "bld_osm_relation_3_c1", "bld_osm_way_2_c0"];

        var bodies = BuildingBodies.Resolve(names, new HashSet<string>(Expected));
        Assert.Equal(Expected, bodies.Select(body => body.CollisionNode));
        Assert.Equal([0, 1, 2, 3], bodies.Select(body => body.Index));
        Assert.Equal(names, bodies.Select(body => body.OriginalName));

        var check = BuildingBodies.Check(Tile, bodies, Expected);
        Assert.True(check.IsBijection, check.Describe());
        Assert.All(check.Accepted, Assert.True);
    }

    [Fact]
    public void BodyOrder_DoesNotMatter()
    {
        string[] names = ["bld_osm_way_2_c0", "bld_osm_relation_3_c1", "bld_osm_way_1_c0", "bld_osm_relation_3_c0"];

        var check = ResolveAndCheck(names, Expected);
        Assert.True(check.IsBijection, check.Describe());
    }

    [Fact]
    public void RenamedBody_IsUnresolvedAndItsNodeMissing()
    {
        // A body Godot renamed on import has lost its glTF name; it is never matched to a mesh or a node.
        string[] names = ["bld_osm_way_1_c0", "bld_osm_relation_3_c0", "bld_osm_relation_3_c1", "@StaticBody3D@12"];

        var check = ResolveAndCheck(names, Expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["@StaticBody3D@12"], check.Unresolved);
        Assert.Equal(["bld_osm_way_2_c0-colonly"], check.Missing);
        Assert.Equal([true, true, true, false], check.Accepted);
        Assert.Contains(Tile, check.Describe());
        Assert.Contains("@StaticBody3D@12", check.Describe());
        Assert.Contains("bld_osm_way_2_c0-colonly", check.Describe());
    }

    [Fact]
    public void OldUnsuffixedPartName_IsUnresolved()
    {
        // A pack from before every collision node had a _c<k> part: bld_osm_way_1-colonly isn't a collision node.
        string[] names = ["bld_osm_way_1"];
        string[] expected = ["bld_osm_way_1_c0-colonly"];

        var check = ResolveAndCheck(names, expected);
        Assert.Equal(["bld_osm_way_1"], check.Unresolved);
        Assert.Equal(["bld_osm_way_1_c0-colonly"], check.Missing);
        Assert.Equal([false], check.Accepted);
    }

    [Fact]
    public void NameWithSuffixStillOn_IsUnresolved()
    {
        string[] names = ["bld_osm_way_1_c0-colonly"];
        string[] expected = ["bld_osm_way_1_c0-colonly"];

        var check = ResolveAndCheck(names, expected);
        Assert.Equal(["bld_osm_way_1_c0-colonly"], check.Unresolved);
        Assert.Equal(["bld_osm_way_1_c0-colonly"], check.Missing);
    }

    [Fact]
    public void Duplicate_IsDetectedAndBothBodiesAreRejected()
    {
        string[] names = ["bld_osm_relation_3_c0", "bld_osm_relation_3_c0"];
        string[] expected = ["bld_osm_relation_3_c0-colonly"];

        var check = ResolveAndCheck(names, expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_relation_3_c0-colonly"], check.Duplicates);
        Assert.Equal([false, false], check.Accepted);
    }

    [Fact]
    public void Missing_IsDetected()
    {
        string[] expected = ["bld_osm_way_1_c0-colonly", "bld_osm_way_2_c0-colonly"];

        var check = ResolveAndCheck(["bld_osm_way_1_c0"], expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_way_2_c0-colonly"], check.Missing);
        Assert.Empty(check.Unresolved);
        Assert.Equal([true], check.Accepted);
    }

    [Fact]
    public void Extra_FromAnotherTileIsDetectedAndRejected()
    {
        string[] tileNodes = ["bld_osm_way_1_c0-colonly", "bld_osm_way_2_c0-colonly"];
        string[] allTiles = [.. tileNodes, "bld_osm_way_9_c0-colonly"];
        string[] names = ["bld_osm_way_1_c0", "bld_osm_way_2_c0", "bld_osm_way_9_c0"];

        var bodies = BuildingBodies.Resolve(names, new HashSet<string>(allTiles));
        var check = BuildingBodies.Check(Tile, bodies, tileNodes);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_way_9_c0-colonly"], check.Extra);
        Assert.Equal([true, true, false], check.Accepted);
    }

    [Fact]
    public void BldNamedBodyNotInBuildings_IsUnresolved()
    {
        string[] names = ["bld_osm_relation_3_c0", "bld_osm_relation_3_c7"];
        string[] expected = ["bld_osm_relation_3_c0-colonly"];

        var check = ResolveAndCheck(names, expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_relation_3_c7"], check.Unresolved);
        // The body with a valid, unique name of this tile stays usable.
        Assert.Equal([true, false], check.Accepted);
    }

    [Fact]
    public void Resolve_WithoutCollisionNodes_ResolvesEveryBuildingName()
    {
        var bodies = BuildingBodies.Resolve(["bld_osm_way_1_c0", "@StaticBody3D@1"], collisionNodes: null);
        Assert.Equal(["bld_osm_way_1_c0-colonly", null], bodies.Select(body => body.CollisionNode));
    }

    [Fact]
    public void Describe_ListsAtMostTheGivenNumberOfNames()
    {
        var expected = Enumerable.Range(1, 8).Select(i => $"bld_osm_way_{i}_c0-colonly").ToList();
        var check = BuildingBodies.Check(Tile, [], expected);

        var text = check.Describe(maxNames: 3);
        Assert.Contains("8 collision nodes without a body", text);
        Assert.Contains("bld_osm_way_1_c0-colonly, bld_osm_way_2_c0-colonly, bld_osm_way_3_c0-colonly, … (5 more)", text);
        Assert.DoesNotContain("bld_osm_way_4_c0-colonly", text);
    }

    [Fact]
    public void EmptyTile_IsABijection()
    {
        Assert.True(BuildingBodies.Check(Tile, [], []).IsBijection);
    }
}
