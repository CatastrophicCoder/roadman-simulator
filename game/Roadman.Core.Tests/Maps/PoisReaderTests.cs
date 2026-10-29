using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class PoisReaderTests
{
    // 500: a shop node in building osm:way/100, with the building's address.
    // 600: a school ground (100 m square with a 20 m hole in the middle) containing building osm:way/200.
    // 700: a park of two 10 m squares, one inside the school ground, one outside.
    // 800, 900: unlinked points 1 m apart; 900 is a fuel station with a shop.
    // The links agree with BuildingsReaderTests.ValidBuildings.
    internal const string ValidPois = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "area": "test",
          "pois": [
            {
              "id": "osm:node/500", "name": "Kulmakauppa", "name_sv": null, "ref": null,
              "type": "shop", "subtype": "convenience", "other_tags": [],
              "geometry": "point", "position": [150, 101.25, 150], "outline": null,
              "address": {"street": "Härkälänkatu", "housenumber": "12a"}, "address_source": "building",
              "building_id": "osm:way/100", "contains_building_ids": []
            },
            {
              "id": "osm:way/600", "name": "Koulu", "name_sv": "Skola", "ref": null,
              "type": "amenity", "subtype": "school", "other_tags": [],
              "geometry": "area", "position": [20, 100, 20],
              "outline": [
                [
                  [[0, 0], [100, 0], [100, 100], [0, 100], [0, 0]],
                  [[40, 40], [60, 40], [60, 60], [40, 60], [40, 40]]
                ]
              ],
              "address": {"street": "Koulukatu", "housenumber": null}, "address_source": "osm",
              "building_id": null, "contains_building_ids": ["osm:way/200"]
            },
            {
              "id": "osm:way/700", "name": null, "name_sv": null, "ref": null,
              "type": "leisure", "subtype": "park", "other_tags": [],
              "geometry": "area", "position": [15, 100, 15],
              "outline": [
                [[[10, 10], [20, 10], [20, 20], [10, 20], [10, 10]]],
                [[[200, 200], [210, 200], [210, 210], [200, 210], [200, 200]]]
              ],
              "address": {"street": null, "housenumber": null}, "address_source": null,
              "building_id": null, "contains_building_ids": []
            },
            {
              "id": "osm:node/800", "name": null, "name_sv": null, "ref": null,
              "type": "tourism", "subtype": "artwork", "other_tags": [],
              "geometry": "point", "position": [300, 90, 300], "outline": null,
              "address": {"street": null, "housenumber": null}, "address_source": null,
              "building_id": null, "contains_building_ids": []
            },
            {
              "id": "osm:node/900", "name": "Asema", "name_sv": null, "ref": "7",
              "type": "amenity", "subtype": "fuel", "other_tags": ["shop=convenience"],
              "geometry": "point", "position": [301, 90, 300], "outline": null,
              "address": {"street": null, "housenumber": null}, "address_source": null,
              "building_id": null, "contains_building_ids": []
            }
          ]
        }
        """;

    private const string EmptyPois = """{"schema_version": 1, "map_id": "somero", "area": "full", "pois": []}""";

    [Fact]
    public void Parse_ValidFile_ReadsEveryField()
    {
        var file = PoisReader.Parse(ValidPois);

        Assert.Equal(1, file.SchemaVersion);
        Assert.Equal("somero", file.MapId);
        Assert.Equal("test", file.Area);
        Assert.Equal(5, file.Items.Count);

        var shop = file.Items[0];
        Assert.Equal("osm:node/500", shop.Id);
        Assert.Equal(new FeatureNames("Kulmakauppa", null, null), shop.Names);
        Assert.Equal(PoiType.Shop, shop.Type);
        Assert.Equal("convenience", shop.Subtype);
        Assert.Empty(shop.OtherTags);
        Assert.Equal(PoiGeometry.Point, shop.Geometry);
        Assert.Equal(new LocalPoint(150, 101.25, 150), shop.Position);
        Assert.Null(shop.Outline);
        Assert.Equal(new Address("Härkälänkatu", "12a"), shop.Address);
        Assert.Equal(AddressSource.Building, shop.AddressSource);
        Assert.Equal("osm:way/100", shop.BuildingId);
        Assert.Empty(shop.ContainsBuildingIds);
        Assert.Equal(0, shop.Area);

        var school = file.Items[1];
        Assert.Equal(new FeatureNames("Koulu", "Skola", null), school.Names);
        Assert.Equal(PoiGeometry.Area, school.Geometry);
        var polygon = Assert.Single(school.Outline!);
        Assert.Equal(5, polygon.Exterior.Count);
        Assert.Equal(new PlanarPoint(100, 0), polygon.Exterior[1]);
        Assert.Equal(new PlanarPoint(40, 40), Assert.Single(polygon.Holes)[0]);
        Assert.Equal(10000 - 400, school.Area, 9);
        Assert.Equal(AddressSource.Osm, school.AddressSource);
        Assert.Equal(new Address("Koulukatu", null), school.Address);
        Assert.Equal(["osm:way/200"], school.ContainsBuildingIds);

        var park = file.Items[2];
        Assert.Equal(PoiType.Leisure, park.Type);
        Assert.Equal(2, park.Outline!.Count);
        Assert.Equal(200, park.Area, 9);
        Assert.Null(park.AddressSource);

        var fuel = file.Items[4];
        Assert.Equal(["shop=convenience"], fuel.OtherTags);
        Assert.Equal("7", fuel.Names.Ref);
        Assert.Equal(PoiType.Tourism, file.Items[3].Type);
    }

    [Fact]
    public void Parse_EmptyList_IsValid()
    {
        Assert.Empty(PoisReader.Parse(EmptyPois).Items);
    }

    [Fact]
    public void Contains_RespectsHolesAndPolygons()
    {
        var file = PoisReader.Parse(ValidPois);
        var school = file.Items[1];
        var park = file.Items[2];

        Assert.True(school.Contains(30, 30));
        Assert.False(school.Contains(50, 50)); // in the hole
        Assert.False(school.Contains(150, 50));
        Assert.True(park.Contains(15, 15));
        Assert.True(park.Contains(205, 205)); // second polygon
        Assert.False(park.Contains(100, 100));
        Assert.False(file.Items[0].Contains(150, 150)); // a point has no outline
    }

    [Fact]
    public void Parse_OtherVersion_IsRejected()
    {
        var json = Modify(root => root["schema_version"] = 2);

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json, "res://maps/somero/pois.json"));
        Assert.Contains("res://maps/somero/pois.json", ex.Message);
        Assert.Contains("schema_version 2 is not supported; this game reads version 1", ex.Message);
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("map_id")]
    [InlineData("area")]
    [InlineData("pois")]
    public void Parse_MissingTopLevelField_IsRejected(string field)
    {
        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(Modify(root => root.AsObject().Remove(field))));
        Assert.Contains($"'{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("name_sv")]
    [InlineData("ref")]
    [InlineData("type")]
    [InlineData("subtype")]
    [InlineData("other_tags")]
    [InlineData("geometry")]
    [InlineData("position")]
    [InlineData("outline")]
    [InlineData("address")]
    [InlineData("address_source")]
    [InlineData("building_id")]
    [InlineData("contains_building_ids")]
    public void Parse_MissingPoiField_IsRejected(string field)
    {
        var json = ModifyPoi(3, poi => poi.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains($"'pois[3].{field}' is missing", ex.Message);
    }

    [Fact]
    public void Parse_UnknownField_IsRejected()
    {
        Assert.Contains(
            "'pois[0].opening_hours' is not a known field",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(ModifyPoi(0, poi => poi["opening_hours"] = "24/7"))).Message);
        Assert.Contains(
            "'extra' is not a known field",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(Modify(root => root["extra"] = 1))).Message);
        Assert.Contains(
            "'pois[0].address.city' is not a known field",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(ModifyPoi(0, poi => poi["address"]!["city"] = "Somero"))).Message);
    }

    [Theory]
    [InlineData("type", "healthcare")]
    [InlineData("geometry", "line")]
    [InlineData("address_source", "mml")]
    public void Parse_UnknownValue_IsRejected(string field, string value)
    {
        var json = ModifyPoi(0, poi => poi[field] = value);

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains($"'pois[0].{field}' '{value}' is not a known value", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateId_IsRejected()
    {
        var json = ModifyPoi(4, poi => poi["id"] = "osm:node/800");

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[4].id' POI id 'osm:node/800' appears more than once", ex.Message);
    }

    [Fact]
    public void Parse_OpenRing_IsRejected()
    {
        var json = ModifyPoi(1, poi => poi["outline"]![0]![1]![4] = new JsonArray(40, 41));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[1].outline[0][1]' is not closed", ex.Message);
    }

    [Fact]
    public void Parse_ShortRing_IsRejected()
    {
        var json = ModifyPoi(
            2,
            poi => poi["outline"]![1]![0] = JsonNode.Parse("[[200, 200], [210, 200], [200, 200]]"));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[2].outline[1][0]' must have at least 4 points, has 3", ex.Message);
    }

    [Fact]
    public void Parse_OutlineOnPoint_IsRejected()
    {
        var json = ModifyPoi(3, poi => poi["outline"] = JsonNode.Parse("[[[[0, 0], [1, 0], [1, 1], [0, 0]]]]"));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[3].outline' must be null for a point", ex.Message);
    }

    [Fact]
    public void Parse_AreaWithoutOutline_IsRejected()
    {
        var none = ModifyPoi(2, poi => poi["outline"] = null);
        Assert.Contains(
            "'pois[2].outline' is null, but POI 'osm:way/700' is an area and needs one",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(none)).Message);

        var empty = ModifyPoi(2, poi => poi["outline"] = new JsonArray());
        Assert.Contains(
            "'pois[2].outline' must have at least one polygon",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(empty)).Message);

        var noRings = ModifyPoi(2, poi => poi["outline"]![0] = new JsonArray());
        Assert.Contains(
            "'pois[2].outline[0]' must have at least an exterior ring",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(noRings)).Message);
    }

    [Fact]
    public void Parse_BadPoint_IsRejected()
    {
        var json = ModifyPoi(1, poi => poi["outline"]![0]![0]![2] = new JsonArray(100, 100, 5));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[1].outline[0][0][2]' must be [x, z]", ex.Message);
    }

    [Theory]
    [InlineData("[150, 101.25, 150]", "[150, 1e400, 150]", "pois[0].position[1]")]
    [InlineData("[[0, 0], [100, 0]", "[[0, 0], [1e999, 0]", "pois[1].outline[0][0][1][0]")]
    [InlineData("[210, 210], [200, 210]", "[210, -1e400], [200, 210]", "pois[2].outline[1][0][2][1]")]
    public void Parse_NonFiniteNumber_IsRejected(string original, string replacement, string field)
    {
        Assert.Contains(original, ValidPois);
        var json = ValidPois.Replace(original, replacement, StringComparison.Ordinal);

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains($"'{field}' must be a finite number", ex.Message);
    }

    [Fact]
    public void Parse_ContainsOnPoint_IsRejected()
    {
        var json = ModifyPoi(3, poi => poi["contains_building_ids"] = new JsonArray("osm:way/200"));

        var ex = Assert.Throws<MapPackException>(() => PoisReader.Parse(json));
        Assert.Contains("'pois[3].contains_building_ids' must be empty for a point", ex.Message);
    }

    [Fact]
    public void Parse_AddressSourceDisagreeing_IsRejected()
    {
        var noAddress = ModifyPoi(3, poi => poi["address_source"] = "osm");
        Assert.Contains(
            "'pois[3].address_source' must be null exactly when the address has no value",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(noAddress)).Message);

        var noBuilding = ModifyPoi(0, poi => poi["building_id"] = null);
        Assert.Contains(
            "'pois[0].address_source' is 'building', but POI 'osm:node/500' has no building_id",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(noBuilding)).Message);
    }

    [Fact]
    public void Parse_BadAreaAndMapId_AreRejected()
    {
        Assert.Contains(
            "'area' must be 'full' or 'test'",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(Modify(root => root["area"] = "all"))).Message);
        Assert.Contains(
            "'map_id' 'Somero' is not a lowercase slug",
            Assert.Throws<MapPackException>(() => PoisReader.Parse(Modify(root => root["map_id"] = "Somero"))).Message);
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidPois)!;
        change(root);
        return root.ToJsonString();
    }

    private static string ModifyPoi(int index, Action<JsonNode> change) => Modify(root => change(root["pois"]![index]!));
}
