using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

/// <summary>
/// docs/specs/mml-only-buildings.md, criterion 14: a building made from an MML record that no OSM
/// building matched (id <c>mml:rakennus/&lt;n&gt;</c>) reads and resolves like any other building.
/// </summary>
public class MmlOnlyBuildingTests
{
    private const string Id = "mml:rakennus/339002621";
    private const string Node = "bld_mml_rakennus_339002621";

    // As the pipeline writes it: own record as mml_building with overlap 1.0, no OSM building value,
    // no address, a cut footprint with two parts (two collision boxes), the default height.
    private const string MmlBuilding = """
        {
          "id": "mml:rakennus/339002621", "tile": "t_1_0", "node": "bld_mml_rakennus_339002621",
          "collision_nodes": ["bld_mml_rakennus_339002621_c0-colonly", "bld_mml_rakennus_339002621_c1-colonly"],
          "height_m": 6, "height_source": "default", "base_z": 98.75, "base_source": "dem",
          "roof_shape": "flat", "roof_shape_source": "default",
          "lidar_rejected": "few_points",
          "category": "residential", "category_source": "mml_use_class",
          "category_raw": {
            "osm_building": null, "osm_poi": [], "osm_poi_area": [],
            "mml_feature_class": 42211, "mml_use_class": 1, "osm_landuse": null, "override": null
          },
          "name": null, "name_sv": null, "ref": null, "name_source": null,
          "address": {"street": null, "housenumber": null}, "address_source": null,
          "poi_ids": [], "area_poi_ids": [],
          "mml_building": "mml:rakennus/339002621", "mml_overlap": 1.0,
          "overrides": []
        }
        """;

    [Fact]
    public void BuildingsReader_AcceptsAnMmlOnlyBuilding()
    {
        var file = BuildingsReader.Parse(WithMmlBuilding());

        Assert.Equal(2, file.SchemaVersion);
        Assert.Equal(4, file.Items.Count);
        var building = Assert.Single(file.Items, item => item.Id == Id);
        Assert.Equal("t_1_0", building.Tile);
        Assert.Equal(Node, building.Node);
        Assert.Equal([$"{Node}_c0-colonly", $"{Node}_c1-colonly"], building.CollisionNodes);
        Assert.Equal(BuildingHeightSource.Default, building.HeightSource);
        Assert.Equal(LidarRejectReason.FewPoints, building.LidarRejected);
        Assert.Null(building.Lidar);
        Assert.Equal(BuildingCategory.Residential, building.Category);
        Assert.Equal(BuildingCategorySource.MmlUseClass, building.CategorySource);
        Assert.Null(building.CategoryRaw.OsmBuilding);
        Assert.Equal(42211L, building.CategoryRaw.MmlFeatureClass);
        Assert.Equal(1L, building.CategoryRaw.MmlUseClass);
        Assert.Equal(FeatureNames.None, building.Names);
        Assert.Null(building.NameSource);
        Assert.Equal(Address.None, building.Address);
        Assert.Null(building.AddressSource);
        Assert.Equal(Id, building.MmlBuilding);
        Assert.Equal(1.0, building.MmlOverlap);
    }

    [Fact]
    public void PlaceIndex_ResolvesAnMmlOnlyBuildingById()
    {
        var places = Create();

        Assert.Equal(Id, places.Building(Id).Id);
        Assert.True(places.TryGetBuilding(Id, out var building));
        Assert.Equal(Node, building.Node);
        Assert.Empty(places.PoisOnOrIn(building));
        Assert.Empty(places.PoiAreasContaining(building));
    }

    [Theory]
    [InlineData(Node)]
    [InlineData(Node + "_c0-colonly")]
    [InlineData(Node + "_c1-colonly")]
    public void PlaceIndex_ResolvesAnMmlOnlyBuildingByNodeName(string node)
    {
        Assert.True(Create().TryGetBuildingByNode(node, out var building));
        Assert.Equal(Id, building.Id);
    }

    [Fact]
    public void PlaceIndex_KeepsTheOsmBuildingsNextToAnMmlOnlyOne()
    {
        var places = Create();

        Assert.True(places.TryGetBuildingByNode("bld_osm_way_100", out var shop));
        Assert.Equal("osm:way/100", shop.Id);
        Assert.False(places.TryGetBuildingByNode("bld_mml_rakennus_339002621_c2-colonly", out _));
    }

    private static PlaceIndex Create() =>
        PlaceIndex.Create(BuildingsReader.Parse(WithMmlBuilding()), PoisReader.Parse(PoisReaderTests.ValidPois));

    private static string WithMmlBuilding()
    {
        var root = JsonNode.Parse(BuildingsReaderTests.ValidBuildings)!;
        root["buildings"]!.AsArray().Add(JsonNode.Parse(MmlBuilding));
        return root.ToJsonString();
    }
}
