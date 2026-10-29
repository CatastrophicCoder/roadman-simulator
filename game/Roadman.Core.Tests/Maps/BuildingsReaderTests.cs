using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class BuildingsReaderTests
{
    // B1: lidar height, retail from the shop node inside it (osm_poi), name taken from the shop, own address, MML match.
    // B2: lidar rejected, education from the school area it is inside (osm_poi_area), no name or address.
    // B3: a relation with two footprint parts and no information at all (unknown).
    // The links agree with PoisReaderTests.ValidPois.
    internal const string ValidBuildings = """
        {
          "schema_version": 2,
          "map_id": "somero",
          "buildings": [
            {
              "id": "osm:way/100", "tile": "t_0_0", "node": "bld_osm_way_100", "collision_nodes": ["bld_osm_way_100_c0-colonly"],
              "height_m": 7.5, "height_source": "mml_lidar", "base_z": 101.25, "base_source": "dem",
              "roof_shape": "flat", "roof_shape_source": "default",
              "lidar_points": 412, "lidar_buffer_m": 0.5, "lidar_returns": "all", "roof_z_median": 108.5, "roof_z_p90": 109.1,
              "category": "retail", "category_source": "osm_poi",
              "category_raw": {
                "osm_building": "yes", "osm_poi": ["shop=convenience"], "osm_poi_area": [],
                "mml_feature_class": 42221, "mml_use_class": 2, "osm_landuse": "retail", "override": null
              },
              "name": "Kulmakauppa", "name_sv": null, "ref": null, "name_source": "osm_poi",
              "address": {"street": "Härkälänkatu", "housenumber": "12a"}, "address_source": "osm",
              "poi_ids": ["osm:node/500"], "area_poi_ids": [],
              "mml_building": "mml:rakennus/9001", "mml_overlap": 0.92,
              "overrides": []
            },
            {
              "id": "osm:way/200", "tile": "t_0_0", "node": "bld_osm_way_200", "collision_nodes": ["bld_osm_way_200_c0-colonly"],
              "height_m": 6, "height_source": "default", "base_z": 100, "base_source": "dem",
              "roof_shape": "flat", "roof_shape_source": "default",
              "lidar_rejected": "tree_cover",
              "category": "education", "category_source": "osm_poi_area",
              "category_raw": {
                "osm_building": "yes", "osm_poi": [], "osm_poi_area": ["amenity=school"],
                "mml_feature_class": null, "mml_use_class": null, "osm_landuse": null, "override": null
              },
              "name": null, "name_sv": null, "ref": null, "name_source": null,
              "address": {"street": null, "housenumber": null}, "address_source": null,
              "poi_ids": [], "area_poi_ids": ["osm:way/600"],
              "mml_building": null, "mml_overlap": null,
              "overrides": []
            },
            {
              "id": "osm:relation/300", "tile": "t_1_0", "node": "bld_osm_relation_300",
              "collision_nodes": ["bld_osm_relation_300_c0-colonly", "bld_osm_relation_300_c1-colonly"],
              "height_m": 6, "height_source": "default", "base_z": 99.5, "base_source": "dem",
              "roof_shape": "flat", "roof_shape_source": "default",
              "category": "unknown", "category_source": null,
              "category_raw": {
                "osm_building": null, "osm_poi": [], "osm_poi_area": [],
                "mml_feature_class": null, "mml_use_class": null, "osm_landuse": null, "override": null
              },
              "name": null, "name_sv": null, "ref": null, "name_source": null,
              "address": {"street": null, "housenumber": "3-5"}, "address_source": "osm",
              "poi_ids": [], "area_poi_ids": [],
              "mml_building": null, "mml_overlap": null,
              "overrides": []
            }
          ]
        }
        """;

    private const string EmptyBuildings = """{"schema_version": 2, "map_id": "somero", "buildings": []}""";

    /// <summary>A version 1 file, as packs built before the place information spec have it.</summary>
    private const string Version1Buildings = """
        {
          "schema_version": 1,
          "map_id": "somero",
          "buildings": [
            {
              "id": "osm:way/100", "tile": "t_0_0", "node": "bld_osm_way_100", "collision_nodes": ["bld_osm_way_100_c0-colonly"],
              "height_m": 6, "height_source": "default", "base_z": 100, "base_source": "dem",
              "roof_shape": "flat", "roof_shape_source": "default"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidFile_ReadsEveryField()
    {
        var file = BuildingsReader.Parse(ValidBuildings);

        Assert.Equal(2, file.SchemaVersion);
        Assert.Equal("somero", file.MapId);
        Assert.Equal(["osm:way/100", "osm:way/200", "osm:relation/300"], file.Items.Select(building => building.Id));

        var shop = file.Items[0];
        Assert.Equal("t_0_0", shop.Tile);
        Assert.Equal("bld_osm_way_100", shop.Node);
        Assert.Equal(["bld_osm_way_100_c0-colonly"], shop.CollisionNodes);
        Assert.Equal(7.5, shop.HeightM);
        Assert.Equal(BuildingHeightSource.MmlLidar, shop.HeightSource);
        Assert.Equal(101.25, shop.BaseZ);
        Assert.Equal(BuildingBaseSource.Dem, shop.BaseSource);
        Assert.Equal(RoofShape.Flat, shop.RoofShape);
        Assert.Equal(RoofShapeSource.Default, shop.RoofShapeSource);
        Assert.Equal(new BuildingLidar(412, 0.5, LidarReturns.All, 108.5, 109.1), shop.Lidar);
        Assert.Null(shop.LidarRejected);
        Assert.Equal(BuildingCategory.Retail, shop.Category);
        Assert.Equal(BuildingCategorySource.OsmPoi, shop.CategorySource);
        Assert.Equal("yes", shop.CategoryRaw.OsmBuilding);
        Assert.Equal(["shop=convenience"], shop.CategoryRaw.OsmPoi);
        Assert.Empty(shop.CategoryRaw.OsmPoiArea);
        Assert.Equal(42221L, shop.CategoryRaw.MmlFeatureClass);
        Assert.Equal(2L, shop.CategoryRaw.MmlUseClass);
        Assert.Equal("retail", shop.CategoryRaw.OsmLanduse);
        Assert.Null(shop.CategoryRaw.Override);
        Assert.Equal(new FeatureNames("Kulmakauppa", null, null), shop.Names);
        Assert.Equal(BuildingNameSource.OsmPoi, shop.NameSource);
        Assert.Equal(new Address("Härkälänkatu", "12a"), shop.Address);
        Assert.Equal(AddressSource.Osm, shop.AddressSource);
        Assert.Equal(["osm:node/500"], shop.PoiIds);
        Assert.Empty(shop.AreaPoiIds);
        Assert.Equal("mml:rakennus/9001", shop.MmlBuilding);
        Assert.Equal(0.92, shop.MmlOverlap);

        var school = file.Items[1];
        Assert.Null(school.Lidar);
        Assert.Equal(LidarRejectReason.TreeCover, school.LidarRejected);
        Assert.Equal(BuildingCategory.Education, school.Category);
        Assert.Equal(BuildingCategorySource.OsmPoiArea, school.CategorySource);
        Assert.Equal(FeatureNames.None, school.Names);
        Assert.Null(school.NameSource);
        Assert.Equal(Address.None, school.Address);
        Assert.Null(school.AddressSource);
        Assert.Equal(["osm:way/600"], school.AreaPoiIds);
        Assert.Null(school.MmlBuilding);
        Assert.Null(school.MmlOverlap);

        var unknown = file.Items[2];
        Assert.Equal(BuildingCategory.Unknown, unknown.Category);
        Assert.Null(unknown.CategorySource);
        Assert.Null(unknown.Lidar);
        Assert.Null(unknown.LidarRejected);
        Assert.Equal(2, unknown.CollisionNodes.Count);
        Assert.Equal(new Address(null, "3-5"), unknown.Address);
        Assert.All(file.Items, building => Assert.Empty(building.Overrides));
    }

    [Fact]
    public void Parse_EmptyList_IsValid()
    {
        Assert.Empty(BuildingsReader.Parse(EmptyBuildings).Items);
    }

    [Fact]
    public void Parse_Version1_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(
            () => BuildingsReader.Parse(Version1Buildings, "res://maps/somero/buildings.json"));
        Assert.Contains("res://maps/somero/buildings.json", ex.Message);
        Assert.Contains("schema_version 1 is not supported; this game reads version 2", ex.Message);
    }

    [Fact]
    public void Parse_OtherVersion_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(Modify(root => root["schema_version"] = 3)));
        Assert.Contains("schema_version 3 is not supported", ex.Message);
    }

    [Fact]
    public void Parse_NotJson_IsRejected()
    {
        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse("{", "buildings.json"));
        Assert.Contains("buildings.json: not valid JSON", ex.Message);
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("map_id")]
    [InlineData("buildings")]
    public void Parse_MissingTopLevelField_IsRejected(string field)
    {
        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(Modify(root => root.AsObject().Remove(field))));
        Assert.Contains($"'{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tile")]
    [InlineData("node")]
    [InlineData("collision_nodes")]
    [InlineData("height_m")]
    [InlineData("height_source")]
    [InlineData("base_z")]
    [InlineData("base_source")]
    [InlineData("roof_shape")]
    [InlineData("roof_shape_source")]
    [InlineData("category")]
    [InlineData("category_source")]
    [InlineData("category_raw")]
    [InlineData("name")]
    [InlineData("name_sv")]
    [InlineData("ref")]
    [InlineData("name_source")]
    [InlineData("address")]
    [InlineData("address_source")]
    [InlineData("poi_ids")]
    [InlineData("area_poi_ids")]
    [InlineData("mml_building")]
    [InlineData("mml_overlap")]
    [InlineData("overrides")]
    public void Parse_MissingBuildingField_IsRejected(string field)
    {
        var json = ModifyBuilding(1, building => building.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[1].{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("osm_building")]
    [InlineData("osm_poi")]
    [InlineData("osm_poi_area")]
    [InlineData("mml_feature_class")]
    [InlineData("mml_use_class")]
    [InlineData("osm_landuse")]
    [InlineData("override")]
    public void Parse_MissingCategoryRawField_IsRejected(string field)
    {
        var json = ModifyBuilding(2, building => building["category_raw"]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[2].category_raw.{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("street")]
    [InlineData("housenumber")]
    public void Parse_MissingAddressField_IsRejected(string field)
    {
        var json = ModifyBuilding(1, building => building["address"]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[1].address.{field}' is missing", ex.Message);
    }

    [Theory]
    [InlineData("", "colour")]
    [InlineData("buildings[0]", "colour")]
    [InlineData("buildings[0].category_raw", "colour")]
    [InlineData("buildings[0].address", "postcode")]
    public void Parse_UnknownField_IsRejected(string path, string field)
    {
        var json = Modify(root =>
        {
            var target = path switch
            {
                "" => root,
                "buildings[0]" => root["buildings"]![0]!,
                "buildings[0].category_raw" => root["buildings"]![0]!["category_raw"]!,
                _ => root["buildings"]![0]!["address"]!,
            };
            target[field] = "x";
        });

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'{(path.Length == 0 ? field : $"{path}.{field}")}' is not a known field", ex.Message);
    }

    [Theory]
    [InlineData("category", "shed")]
    [InlineData("category_source", "mml_3d")]
    [InlineData("name_source", "poi")]
    [InlineData("address_source", "building")]
    [InlineData("height_source", "lidar")]
    [InlineData("base_source", "lidar")]
    [InlineData("roof_shape", "gabled")]
    [InlineData("roof_shape_source", "osm")]
    [InlineData("lidar_returns", "last")]
    public void Parse_UnknownValue_IsRejected(string field, string value)
    {
        var json = ModifyBuilding(0, building => building[field] = value);

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[0].{field}' '{value}' is not a known value", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOverrideCategoryAndRejectReason_AreRejected()
    {
        var overrideJson = ModifyBuilding(0, building => building["category_raw"]!["override"] = "castle");
        Assert.Contains(
            "'buildings[0].category_raw.override' 'castle' is not a known value",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(overrideJson)).Message);

        var rejectedJson = ModifyBuilding(1, building => building["lidar_rejected"] = "fog");
        Assert.Contains(
            "'buildings[1].lidar_rejected' 'fog' is not a known value",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(rejectedJson)).Message);
    }

    [Fact]
    public void Parse_DuplicateId_IsRejected()
    {
        var json = ModifyBuilding(1, building => building["id"] = "osm:way/100");

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains("'buildings[1].id' building id 'osm:way/100' appears more than once", ex.Message);
    }

    [Fact]
    public void Parse_SharedNodeName_IsRejected()
    {
        var json = ModifyBuilding(2, building => building["collision_nodes"]![1] = "bld_osm_way_200_c0-colonly");

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains("glTF node 'bld_osm_way_200_c0-colonly' of building 'osm:relation/300' is already used by building 'osm:way/200'", ex.Message);
    }

    [Theory]
    [InlineData("\"height_m\": 7.5", "\"height_m\": 1e400", "buildings[0].height_m")]
    [InlineData("\"base_z\": 101.25", "\"base_z\": -1e400", "buildings[0].base_z")]
    [InlineData("\"roof_z_p90\": 109.1", "\"roof_z_p90\": 1e999", "buildings[0].roof_z_p90")]
    [InlineData("\"lidar_buffer_m\": 0.5", "\"lidar_buffer_m\": 1e400", "buildings[0].lidar_buffer_m")]
    [InlineData("\"mml_overlap\": 0.92", "\"mml_overlap\": 1e400", "buildings[0].mml_overlap")]
    public void Parse_NonFiniteNumber_IsRejected(string original, string replacement, string field)
    {
        Assert.Contains(original, ValidBuildings);
        var json = ValidBuildings.Replace(original, replacement, StringComparison.Ordinal);

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'{field}' must be a finite number", ex.Message);
    }

    [Theory]
    [InlineData(0, "osm_building", "category_raw.osm_building has no value")]
    [InlineData(0, "override", "category_raw.override has no value")]
    [InlineData(1, "osm_poi", "category_raw.osm_poi has no value")]
    [InlineData(0, "osm_poi_area", "category_raw.osm_poi_area has no value")]
    [InlineData(1, "mml_use_class", "category_raw.mml_feature_class has no value")]
    [InlineData(1, "osm_landuse", "category_raw.osm_landuse has no value")]
    [InlineData(0, null, "is null, but the category isn't 'unknown'")]
    public void Parse_CategorySourceWithoutRawValue_IsRejected(int index, string? categorySource, string message)
    {
        var json = ModifyBuilding(
            index,
            building =>
            {
                building["category_source"] = categorySource;
                building["category_raw"]!["osm_building"] = null;
            });

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[{index}].category_source'", ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Parse_OverrideCategoryWithRawValue_IsValid()
    {
        var json = ModifyBuilding(
            2,
            building =>
            {
                building["category"] = "garage_or_shed";
                building["category_source"] = "override";
                building["category_raw"]!["override"] = "garage_or_shed";
                building["overrides"] = JsonNode.Parse(
                    """[{"id": "mml:rakennus/77", "source": "playtest 2026-11-02", "licence": "own work", "fields": ["category"]}]""");
            });

        var building = BuildingsReader.Parse(json).Items[2];
        Assert.Equal(BuildingCategory.GarageOrShed, building.Category);
        Assert.Equal(BuildingCategorySource.Override, building.CategorySource);
        Assert.Equal(BuildingCategory.GarageOrShed, building.CategoryRaw.Override);
    }

    [Fact]
    public void Parse_NameSourceDisagreeingWithNames_IsRejected()
    {
        var noSource = ModifyBuilding(0, building => building["name_source"] = null);
        Assert.Contains(
            "'buildings[0].name_source' is null, but the building has a name field",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noSource)).Message);

        var noNames = ModifyBuilding(1, building => building["name_source"] = "osm");
        Assert.Contains(
            "'buildings[1].name_source' names a source, but all name fields are null",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noNames)).Message);
    }

    [Fact]
    public void Parse_NameOverrideOfAllNulls_IsValid()
    {
        var json = ModifyBuilding(
            1,
            building =>
            {
                building["name_source"] = "override";
                building["overrides"] = JsonNode.Parse(
                    """[{"id": "osm:way/200", "source": "own survey", "licence": "own work", "fields": ["name"]}]""");
            });

        Assert.Equal(BuildingNameSource.Override, BuildingsReader.Parse(json).Items[1].NameSource);
    }

    [Fact]
    public void Parse_AddressSourceDisagreeingWithAddress_IsRejected()
    {
        var noSource = ModifyBuilding(0, building => building["address_source"] = null);
        Assert.Contains(
            "'buildings[0].address_source' must be 'osm' exactly when the address has a value",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noSource)).Message);

        var noAddress = ModifyBuilding(1, building => building["address_source"] = "osm");
        Assert.Contains(
            "'buildings[1].address_source' must be 'osm' exactly when the address has a value",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noAddress)).Message);
    }

    [Fact]
    public void Parse_BadMmlFields_AreRejected()
    {
        var onlyOverlap = ModifyBuilding(1, building => building["mml_overlap"] = 0.5);
        Assert.Contains(
            "'buildings[1].mml_overlap' must be null exactly when mml_building is null",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(onlyOverlap)).Message);

        var overOne = ModifyBuilding(0, building => building["mml_overlap"] = 1.2);
        Assert.Contains(
            "'buildings[0].mml_overlap' must be between 0 and 1",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(overOne)).Message);

        foreach (var bad in new[] { "9001", "mml:rakennus/", "MML:rakennus/1", "mml:rakennus/12a", "mml/1", "mml:rakennus/1/2" })
        {
            var badId = ModifyBuilding(0, building => building["mml_building"] = bad);
            Assert.Contains(
                "'buildings[0].mml_building' must be a building id '<source>:<table>/<number>' or null",
                Assert.Throws<MapPackException>(() => BuildingsReader.Parse(badId)).Message);
        }
    }

    [Theory]
    [InlineData("mml:rakennus/9001")]
    [InlineData("other_agency:building_table/7")]
    [InlineData("osm:way/123")]
    public void Parse_MmlBuilding_AcceptsAnySourceAndTable(string id)
    {
        var json = ModifyBuilding(0, building => building["mml_building"] = id);
        Assert.Equal(id, BuildingsReader.Parse(json).Items[0].MmlBuilding);
    }

    [Fact]
    public void Parse_BadLidarFields_AreRejected()
    {
        var incomplete = ModifyBuilding(0, building => building.AsObject().Remove("roof_z_p90"));
        Assert.Contains(
            "'buildings[0].roof_z_p90' is missing; the lidar fields come together",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(incomplete)).Message);

        var noLidar = ModifyBuilding(
            0,
            building =>
            {
                foreach (var name in new[] { "lidar_points", "lidar_buffer_m", "lidar_returns", "roof_z_median", "roof_z_p90" })
                {
                    building.AsObject().Remove(name);
                }
            });
        Assert.Contains(
            "height_source 'mml_lidar' needs the lidar fields",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noLidar)).Message);

        var notLidarHeight = ModifyBuilding(0, building => building["height_source"] = "osm_height");
        Assert.Contains(
            "'buildings[0].lidar_points' is only allowed when height_source is 'mml_lidar'",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(notLidarHeight)).Message);

        var negative = ModifyBuilding(0, building => building["lidar_buffer_m"] = -0.5);
        Assert.Contains(
            "'buildings[0].lidar_buffer_m' must not be negative",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(negative)).Message);

        var both = ModifyBuilding(0, building => building["lidar_rejected"] = "too_low");
        Assert.Contains(
            "must not be given together with lidar_rejected",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(both)).Message);
    }

    [Fact]
    public void Parse_BadListsAndValues_AreRejected()
    {
        var repeated = ModifyBuilding(0, building => building["poi_ids"] = new JsonArray("osm:node/500", "osm:node/500"));
        Assert.Contains(
            "'buildings[0].poi_ids[1]' 'osm:node/500' appears more than once",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(repeated)).Message);

        var noCollision = ModifyBuilding(0, building => building["collision_nodes"] = new JsonArray());
        Assert.Contains(
            "'buildings[0].collision_nodes' must not be empty",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noCollision)).Message);

        var flat = ModifyBuilding(0, building => building["height_m"] = 0);
        Assert.Contains(
            "'buildings[0].height_m' must be greater than 0",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(flat)).Message);

        var badClass = ModifyBuilding(0, building => building["category_raw"]!["mml_feature_class"] = "42221");
        Assert.Contains(
            "'buildings[0].category_raw.mml_feature_class' must be a whole number or null",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(badClass)).Message);

        var badMapId = Modify(root => root["map_id"] = "Somero");
        Assert.Contains(
            "'map_id' 'Somero' is not a lowercase slug",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(badMapId)).Message);
    }

    /// <summary>Building 3 with its category from an MML-key override and its name from an OSM-key override.</summary>
    private static string WithOverrides(Action<JsonNode>? change = null) =>
        ModifyBuilding(
            2,
            building =>
            {
                building["category"] = "garage_or_shed";
                building["category_source"] = "override";
                building["category_raw"]!["override"] = "garage_or_shed";
                building["name"] = "Sauna";
                building["name_source"] = "override";
                building["overrides"] = JsonNode.Parse("""
                    [
                      {"id": "mml:rakennus/77", "source": "playtest 2026-11-02", "licence": "own work", "fields": ["category"]},
                      {"id": "osm:relation/300", "source": "Example survey", "licence": "CC BY-SA 4.0", "fields": ["category", "name"]}
                    ]
                    """);
                change?.Invoke(building);
            });

    [Fact]
    public void Parse_Overrides_AreRead()
    {
        var building = BuildingsReader.Parse(WithOverrides()).Items[2];

        Assert.Equal(BuildingCategorySource.Override, building.CategorySource);
        Assert.Equal(BuildingNameSource.Override, building.NameSource);
        Assert.Equal(2, building.Overrides.Count);
        Assert.Equal("mml:rakennus/77", building.Overrides[0].Id);
        Assert.Equal("playtest 2026-11-02", building.Overrides[0].Source);
        Assert.Equal("own work", building.Overrides[0].Licence);
        Assert.Equal([BuildingOverrideField.Category], building.Overrides[0].Fields);
        Assert.Equal("osm:relation/300", building.Overrides[1].Id);
        Assert.Equal("Example survey", building.Overrides[1].Source);
        Assert.Equal("CC BY-SA 4.0", building.Overrides[1].Licence);
        Assert.Equal([BuildingOverrideField.Category, BuildingOverrideField.Name], building.Overrides[1].Fields);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("source")]
    [InlineData("licence")]
    [InlineData("fields")]
    public void Parse_MissingOverrideField_IsRejected(string field)
    {
        var json = WithOverrides(building => building["overrides"]![1]!.AsObject().Remove(field));

        var ex = Assert.Throws<MapPackException>(() => BuildingsReader.Parse(json));
        Assert.Contains($"'buildings[2].overrides[1].{field}' is missing", ex.Message);
    }

    [Fact]
    public void Parse_BadOverrideEntries_AreRejected()
    {
        var unknownKey = WithOverrides(building => building["overrides"]![0]!["attribution"] = "x");
        Assert.Contains(
            "'buildings[2].overrides[0].attribution' is not a known field",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(unknownKey)).Message);

        var unknownField = WithOverrides(building => building["overrides"]![0]!["fields"] = new JsonArray("height"));
        Assert.Contains(
            "'buildings[2].overrides[0].fields[0]' 'height' is not a known value",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(unknownField)).Message);

        var noFields = WithOverrides(building => building["overrides"]![0]!["fields"] = new JsonArray());
        Assert.Contains(
            "'buildings[2].overrides[0].fields' must not be empty",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noFields)).Message);

        var repeatedField = WithOverrides(building => building["overrides"]![1]!["fields"] = new JsonArray("name", "name"));
        Assert.Contains(
            "'buildings[2].overrides[1].fields[1]' 'name' appears more than once",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(repeatedField)).Message);

        var repeatedId = WithOverrides(building => building["overrides"]![1]!["id"] = "mml:rakennus/77");
        Assert.Contains(
            "'buildings[2].overrides[1].id' override 'mml:rakennus/77' appears more than once",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(repeatedId)).Message);

        var emptySource = WithOverrides(building => building["overrides"]![0]!["source"] = "");
        Assert.Contains(
            "'buildings[2].overrides[0].source' must not be empty",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(emptySource)).Message);

        var emptyLicence = WithOverrides(building => building["overrides"]![0]!["licence"] = "");
        Assert.Contains(
            "'buildings[2].overrides[0].licence' must not be empty",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(emptyLicence)).Message);
    }

    [Fact]
    public void Parse_OverrideSourceWithoutEntry_IsRejected()
    {
        var noCategory = WithOverrides(building =>
        {
            building["overrides"]!.AsArray().RemoveAt(0);
            building["overrides"]![0]!["fields"] = new JsonArray("name");
        });
        Assert.Contains(
            "'buildings[2].category_source' is 'override', but no overrides entry has 'category' in its fields",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noCategory)).Message);

        var noName = WithOverrides(building => building["overrides"]![1]!["fields"] = new JsonArray("category"));
        Assert.Contains(
            "'buildings[2].name_source' is 'override', but no overrides entry has 'name' in its fields",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(noName)).Message);
    }

    [Fact]
    public void Parse_OverrideEntryWithoutSource_IsRejected()
    {
        // An entry that set the category on a building whose category came from OSM.
        var category = ModifyBuilding(
            0,
            building => building["overrides"] = JsonNode.Parse(
                """[{"id": "osm:way/100", "source": "s", "licence": "own work", "fields": ["category"]}]"""));
        Assert.Contains(
            "'buildings[0].overrides' entry 'osm:way/100' set 'category', but category_source isn't 'override'",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(category)).Message);

        var name = WithOverrides(building => building["name_source"] = null);
        Assert.Contains(
            "'buildings[2].name_source' is null, but the building has a name field",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(name)).Message);

        var nameNull = WithOverrides(building =>
        {
            building["name"] = null;
            building["name_source"] = null;
        });
        Assert.Contains(
            "entry 'osm:relation/300' set 'name', but name_source isn't 'override'",
            Assert.Throws<MapPackException>(() => BuildingsReader.Parse(nameNull)).Message);
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(ValidBuildings)!;
        change(root);
        return root.ToJsonString();
    }

    private static string ModifyBuilding(int index, Action<JsonNode> change) =>
        Modify(root => change(root["buildings"]![index]!));
}
