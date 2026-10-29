using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class PlaceIndexTests
{
    private const string Shop = "osm:node/500";
    private const string School = "osm:way/600";
    private const string Park = "osm:way/700";

    [Fact]
    public void Create_ValidFiles_JoinsThem()
    {
        var places = Create();

        Assert.Equal(3, places.Buildings.Items.Count);
        Assert.Equal(5, places.Pois.Items.Count);
        Assert.Equal("osm:way/100", places.Building("osm:way/100").Id);
        Assert.True(places.TryGetBuilding("osm:relation/300", out var relation));
        Assert.Equal("t_1_0", relation.Tile);
        Assert.False(places.TryGetBuilding("osm:way/999", out _));
        Assert.Throws<KeyNotFoundException>(() => places.Building("osm:way/999"));
        Assert.Equal(School, places.Poi(School).Id);
        Assert.False(places.TryGetPoi("osm:node/1", out _));
        Assert.Throws<KeyNotFoundException>(() => places.Poi("osm:node/1"));
    }

    [Theory]
    [InlineData("bld_osm_way_100", "osm:way/100")]
    [InlineData("bld_osm_way_100-colonly", "osm:way/100")]
    [InlineData("bld_osm_relation_300", "osm:relation/300")]
    [InlineData("bld_osm_relation_300_p1-colonly", "osm:relation/300")]
    public void TryGetBuildingByNode_FindsVisualAndCollisionNodes(string node, string buildingId)
    {
        Assert.True(Create().TryGetBuildingByNode(node, out var building));
        Assert.Equal(buildingId, building.Id);
    }

    [Fact]
    public void TryGetBuildingByNode_UnknownNode_IsFalse()
    {
        Assert.False(Create().TryGetBuildingByNode("terrain", out _));
    }

    [Fact]
    public void PoisOnOrIn_AndPoiAreasContaining_FollowTheLinks()
    {
        var places = Create();

        Assert.Equal([Shop], places.PoisOnOrIn(places.Building("osm:way/100")).Select(poi => poi.Id));
        Assert.Empty(places.PoiAreasContaining(places.Building("osm:way/100")));
        Assert.Empty(places.PoisOnOrIn(places.Building("osm:way/200")));
        Assert.Equal([School], places.PoiAreasContaining(places.Building("osm:way/200")).Select(poi => poi.Id));
    }

    [Fact]
    public void PoiAreaAt_FindsInsideButNotInHole()
    {
        var places = Create();

        Assert.Equal(School, places.PoiAreaAt(30, 30)?.Id);
        Assert.Null(places.PoiAreaAt(50, 50)); // the school ground's hole
        Assert.Null(places.PoiAreaAt(500, 500));
        Assert.Equal(Park, places.PoiAreaAt(205, 205)?.Id); // the park's second polygon
    }

    [Fact]
    public void PoiAreaAt_SmallestAreaFirst()
    {
        var places = Create();

        // (15, 15) is in the park (200 m²) and in the school ground (9,600 m²).
        Assert.Equal(Park, places.PoiAreaAt(15, 15)?.Id);
        Assert.Equal([Park, School], places.PoiAreasAt(15, 15).Select(poi => poi.Id));
    }

    [Fact]
    public void NearestPoiPoint_WithinRadius()
    {
        var places = Create();

        Assert.Equal("osm:node/800", places.NearestPoiPoint(new LocalPoint(299, 90, 300), 3)?.Id);
        Assert.Equal("osm:node/900", places.NearestPoiPoint(new LocalPoint(302, 90, 300), 3)?.Id);
        Assert.Equal(Shop, places.NearestPoiPoint(new LocalPoint(150, 100, 150), 3)?.Id);

        // 3 m away exactly counts; just over doesn't, and areas are never returned.
        Assert.Equal(Shop, places.NearestPoiPoint(new LocalPoint(150, 101.25, 153), 3)?.Id);
        Assert.Null(places.NearestPoiPoint(new LocalPoint(150, 101.25, 153.01), 3));
        Assert.Null(places.NearestPoiPoint(new LocalPoint(20, 100, 20), 3));

        // Height counts: 5 m above the shop is out of a 3 m radius.
        Assert.Null(places.NearestPoiPoint(new LocalPoint(150, 106.25, 150), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => places.NearestPoiPoint(default, -1));
    }

    [Fact]
    public void NearestPoiPoint_TieGoesToTheLowerId()
    {
        // 800 and 900 are 1 m apart; the midpoint is equally far from both.
        Assert.Equal("osm:node/800", Create().NearestPoiPoint(new LocalPoint(300.5, 90, 300), 3)?.Id);
    }

    [Fact]
    public void Create_DifferentMaps_IsRejected()
    {
        var pois = Pois(root => root["map_id"] = "other");

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), pois));
        Assert.Contains("buildings.json is for map 'somero', but pois.json is for map 'other'", ex.Message);
    }

    [Fact]
    public void Create_BuildingLinksToMissingPoi_IsRejected()
    {
        var buildings = Buildings(root => root["buildings"]![0]!["poi_ids"] = new JsonArray("osm:node/501"));

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(buildings, Pois()));
        Assert.Contains("buildings.json: building 'osm:way/100' lists POI 'osm:node/501' in poi_ids, which isn't in pois.json", ex.Message);
    }

    [Fact]
    public void Create_BuildingPoiNotLinkingBack_IsRejected()
    {
        // Building 300 claims the artwork, whose building_id is null.
        var buildings = Buildings(root => root["buildings"]![2]!["poi_ids"] = new JsonArray("osm:node/800"));

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(buildings, Pois()));
        Assert.Contains("building 'osm:relation/300' lists POI 'osm:node/800' in poi_ids, but its building_id is null", ex.Message);
    }

    [Fact]
    public void Create_BuildingAreaLinks_AreChecked()
    {
        var missing = Buildings(root => root["buildings"]![1]!["area_poi_ids"] = new JsonArray("osm:way/601"));
        Assert.Contains(
            "lists POI 'osm:way/601' in area_poi_ids, which isn't in pois.json",
            Assert.Throws<MapPackException>(() => PlaceIndex.Create(missing, Pois())).Message);

        var point = Buildings(root => root["buildings"]![1]!["area_poi_ids"] = new JsonArray("osm:node/800"));
        Assert.Contains(
            "lists POI 'osm:node/800' in area_poi_ids, but it is a point",
            Assert.Throws<MapPackException>(() => PlaceIndex.Create(point, Pois())).Message);

        var oneWay = Buildings(root => root["buildings"]![1]!["area_poi_ids"] = new JsonArray(School, Park));
        Assert.Contains(
            "building 'osm:way/200' lists POI 'osm:way/700' in area_poi_ids, but the POI's contains_building_ids doesn't list the building",
            Assert.Throws<MapPackException>(() => PlaceIndex.Create(oneWay, Pois())).Message);
    }

    [Fact]
    public void Create_PoiLinksToMissingBuilding_IsRejected()
    {
        var pois = Pois(root => root["pois"]![4]!["building_id"] = "osm:way/101");

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), pois));
        Assert.Contains("pois.json: POI 'osm:node/900' has building_id 'osm:way/101', which isn't in buildings.json", ex.Message);
    }

    [Fact]
    public void Create_PoiBuildingNotLinkingBack_IsRejected()
    {
        var pois = Pois(root => root["pois"]![4]!["building_id"] = "osm:way/200");

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), pois));
        Assert.Contains("POI 'osm:node/900' has building_id 'osm:way/200', but the building's poi_ids doesn't list the POI", ex.Message);
    }

    [Fact]
    public void Create_PoiContainsLinks_AreChecked()
    {
        var missing = Pois(root => root["pois"]![2]!["contains_building_ids"] = new JsonArray("osm:way/201"));
        Assert.Contains(
            "POI 'osm:way/700' lists building 'osm:way/201' in contains_building_ids, which isn't in buildings.json",
            Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), missing)).Message);

        var oneWay = Pois(root => root["pois"]![2]!["contains_building_ids"] = new JsonArray("osm:way/100"));
        Assert.Contains(
            "POI 'osm:way/700' lists building 'osm:way/100' in contains_building_ids, but the building's area_poi_ids doesn't list the POI",
            Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), oneWay)).Message);
    }

    [Fact]
    public void Create_BuildingAddressThatDiffers_IsRejected()
    {
        var pois = Pois(root => root["pois"]![0]!["address"]!["housenumber"] = "12b");

        var ex = Assert.Throws<MapPackException>(() => PlaceIndex.Create(Buildings(), pois));
        Assert.Contains("POI 'osm:node/500' has address_source 'building', but its address differs from building 'osm:way/100'", ex.Message);
    }

    private static PlaceIndex Create() => PlaceIndex.Create(Buildings(), Pois());

    private static Buildings Buildings(Action<JsonNode>? change = null) =>
        BuildingsReader.Parse(Apply(BuildingsReaderTests.ValidBuildings, change));

    private static Pois Pois(Action<JsonNode>? change = null) => PoisReader.Parse(Apply(PoisReaderTests.ValidPois, change));

    private static string Apply(string json, Action<JsonNode>? change)
    {
        if (change is null)
        {
            return json;
        }

        var root = JsonNode.Parse(json)!;
        change(root);
        return root.ToJsonString();
    }
}
