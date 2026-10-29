using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class BuildingBodiesTests
{
    private const string Tile = "tile_x0_y0";

    private static TileChild Mesh(string name) => new(TileChildKind.Mesh, name);

    private static TileChild Body(string name) => new(TileChildKind.Body, name);

    private static TileChild Other(string name) => new(TileChildKind.Other, name);

    private static readonly string[] SinglePart = ["bld_osm_way_1-colonly", "bld_osm_way_2-colonly"];

    private static readonly string[] WithMultiPart =
        ["bld_osm_way_1-colonly", "bld_osm_relation_3_c0-colonly", "bld_osm_relation_3_c1-colonly", "bld_osm_way_2-colonly"];

    private static TileBodyCheck ResolveAndCheck(IReadOnlyList<TileChild> children, IReadOnlyList<string> expected)
    {
        var bodies = BuildingBodies.Resolve(children, new HashSet<string>(expected));
        return BuildingBodies.Check(Tile, bodies, expected);
    }

    [Fact]
    public void NormalOrder_EachRenamedBodyTakesItsMesh()
    {
        TileChild[] children =
        [
            Mesh("terrain"), Mesh("road"),
            Mesh("bld_osm_way_1"), Body("@StaticBody3D@12"),
            Mesh("bld_osm_way_2"), Body("@StaticBody3D@13"),
        ];

        var bodies = BuildingBodies.Resolve(children, new HashSet<string>(SinglePart));
        Assert.Equal(["bld_osm_way_1-colonly", "bld_osm_way_2-colonly"], bodies.Select(body => body.CollisionNode));
        Assert.Equal([3, 5], bodies.Select(body => body.ChildIndex));
        Assert.All(bodies, body => Assert.True(body.FromOrder));

        var check = BuildingBodies.Check(Tile, bodies, SinglePart);
        Assert.True(check.IsBijection, check.Describe());
        Assert.All(check.Accepted, Assert.True);
    }

    [Fact]
    public void MultiPart_KeepsImporterNamesAndResetsThePreviousMesh()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_way_1"), Body("@StaticBody3D@1"),
            Mesh("bld_osm_relation_3"), Body("bld_osm_relation_3_c0"), Body("bld_osm_relation_3_c1"),
            Mesh("bld_osm_way_2"), Body("@StaticBody3D@2"),
        ];

        var bodies = BuildingBodies.Resolve(children, new HashSet<string>(WithMultiPart));
        Assert.Equal(
            ["bld_osm_way_1-colonly", "bld_osm_relation_3_c0-colonly", "bld_osm_relation_3_c1-colonly", "bld_osm_way_2-colonly"],
            bodies.Select(body => body.CollisionNode));
        Assert.Equal([true, false, false, true], bodies.Select(body => body.FromOrder));
        Assert.True(BuildingBodies.Check(Tile, bodies, WithMultiPart).IsBijection);
    }

    [Fact]
    public void RenamedBodyAfterMultiPartBodies_DoesNotInheritTheMultiPartMesh()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_relation_3"), Body("bld_osm_relation_3_c0"), Body("bld_osm_relation_3_c1"),
            Body("@StaticBody3D@9"),
        ];
        string[] expected = ["bld_osm_relation_3_c0-colonly", "bld_osm_relation_3_c1-colonly"];

        var bodies = BuildingBodies.Resolve(children, collisionNodes: null);
        Assert.Null(bodies[2].CollisionNode);

        var check = BuildingBodies.Check(Tile, bodies, expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["@StaticBody3D@9"], check.Unresolved);
        Assert.Equal([true, true, false], check.Accepted);
    }

    [Fact]
    public void NonBuildingNodeInBetween_ClearsThePreviousMesh()
    {
        TileChild[] children = [Mesh("bld_osm_way_1"), Other("Marker"), Body("@StaticBody3D@1")];
        Assert.Null(BuildingBodies.Resolve(children, collisionNodes: null)[0].CollisionNode);

        TileChild[] afterRoad = [Mesh("bld_osm_way_1"), Mesh("road"), Body("@StaticBody3D@1")];
        Assert.Null(BuildingBodies.Resolve(afterRoad, collisionNodes: null)[0].CollisionNode);
    }

    [Fact]
    public void ShiftedOrder_IsDetectedAndOrderNamedBodiesAreRejected()
    {
        // Bodies before their meshes: each renamed body would take the previous building's mesh.
        TileChild[] children =
        [
            Body("@StaticBody3D@1"), Mesh("bld_osm_way_1"),
            Body("@StaticBody3D@2"), Mesh("bld_osm_way_2"),
        ];

        var check = ResolveAndCheck(children, SinglePart);
        Assert.False(check.IsBijection);
        Assert.Equal(["@StaticBody3D@1"], check.Unresolved);
        Assert.Equal(["bld_osm_way_2-colonly"], check.Missing);
        Assert.All(check.Accepted, Assert.False);
        Assert.Contains(Tile, check.Describe());
        Assert.Contains("bld_osm_way_2-colonly", check.Describe());
    }

    [Fact]
    public void ShiftedOrder_TwoMeshesThenTwoBodies_IsDetected()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_way_1"), Mesh("bld_osm_way_2"),
            Body("@StaticBody3D@1"), Body("@StaticBody3D@2"),
        ];

        var check = ResolveAndCheck(children, SinglePart);
        Assert.False(check.IsBijection);
        Assert.Equal(["@StaticBody3D@2"], check.Unresolved);
        Assert.Equal(["bld_osm_way_1-colonly"], check.Missing);
        Assert.All(check.Accepted, Assert.False);
    }

    [Fact]
    public void Duplicate_IsDetectedAndBothBodiesAreRejected()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_relation_3"), Body("bld_osm_relation_3_c0"), Body("bld_osm_relation_3_c0"),
        ];
        string[] expected = ["bld_osm_relation_3_c0-colonly"];

        var check = ResolveAndCheck(children, expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_relation_3_c0-colonly"], check.Duplicates);
        Assert.Equal([false, false], check.Accepted);
    }

    [Fact]
    public void Missing_IsDetected()
    {
        TileChild[] children = [Mesh("bld_osm_way_1"), Body("@StaticBody3D@1"), Mesh("bld_osm_way_2")];

        var check = ResolveAndCheck(children, SinglePart);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_way_2-colonly"], check.Missing);
        Assert.Empty(check.Unresolved);
        Assert.Equal([false], check.Accepted);
    }

    [Fact]
    public void Extra_FromAnotherTileIsDetected()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_way_1"), Body("@StaticBody3D@1"),
            Mesh("bld_osm_way_2"), Body("@StaticBody3D@2"),
            Mesh("bld_osm_way_9"), Body("@StaticBody3D@3"),
        ];
        string[] allTiles = [.. SinglePart, "bld_osm_way_9-colonly"];

        var bodies = BuildingBodies.Resolve(children, new HashSet<string>(allTiles));
        var check = BuildingBodies.Check(Tile, bodies, SinglePart);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_way_9-colonly"], check.Extra);
        Assert.All(check.Accepted, Assert.False);
    }

    [Fact]
    public void BldNamedBodyNotInBuildings_IsUnresolved()
    {
        TileChild[] children =
        [
            Mesh("bld_osm_relation_3"), Body("bld_osm_relation_3_c0"), Body("bld_osm_relation_3_c7"),
        ];
        string[] expected = ["bld_osm_relation_3_c0-colonly"];

        var check = ResolveAndCheck(children, expected);
        Assert.False(check.IsBijection);
        Assert.Equal(["bld_osm_relation_3_c7"], check.Unresolved);
        // The importer-named body with a valid, unique name stays usable.
        Assert.Equal([true, false], check.Accepted);
    }

    [Fact]
    public void RenamedBodyOfAMultiPartMesh_IsUnresolved()
    {
        // bld_osm_relation_3-colonly isn't a collision node: the building has only _c<k> parts.
        TileChild[] children = [Mesh("bld_osm_relation_3"), Body("@StaticBody3D@1")];
        string[] expected = ["bld_osm_relation_3_c0-colonly"];

        var check = ResolveAndCheck(children, expected);
        Assert.Equal(["@StaticBody3D@1"], check.Unresolved);
        Assert.Equal(["bld_osm_relation_3_c0-colonly"], check.Missing);
    }

    [Fact]
    public void Describe_ListsAtMostTheGivenNumberOfNames()
    {
        var expected = Enumerable.Range(1, 8).Select(i => $"bld_osm_way_{i}-colonly").ToList();
        var check = BuildingBodies.Check(Tile, [], expected);

        var text = check.Describe(maxNames: 3);
        Assert.Contains("8 collision nodes without a body", text);
        Assert.Contains("bld_osm_way_1-colonly, bld_osm_way_2-colonly, bld_osm_way_3-colonly, … (5 more)", text);
        Assert.DoesNotContain("bld_osm_way_4-colonly", text);
    }

    [Fact]
    public void EmptyTile_IsABijection()
    {
        Assert.True(BuildingBodies.Check(Tile, [], []).IsBijection);
    }
}
