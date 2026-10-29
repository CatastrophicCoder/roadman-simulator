using System.Text.Json.Nodes;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class PlaceTextTests
{
    [Fact]
    public void Describe_Building_ShowsEveryInspectorField()
    {
        var places = Places();

        var text = PlaceText.Describe(places.Building("osm:way/100"), places);

        Assert.Equal(
            """
            Building osm:way/100
            category: retail (category_source: osm_poi)
            category_raw:
              osm_building: "yes"
              osm_poi: [shop=convenience]
              osm_poi_area: []
              mml_feature_class: 42221
              mml_use_class: 2
              osm_landuse: "retail"
              override: null
            mml_building: "mml:rakennus/9001", mml_overlap: 0.92
            name: "Kulmakauppa", name_sv: null, ref: null (name_source: osm_poi)
            overrides: none
            address: street "Härkälänkatu", housenumber "12a" (address_source: osm)
            height: 7.5 m (height_source: mml_lidar)
            POIs on or in it:
              osm:node/500 shop=convenience, name: "Kulmakauppa", name_sv: null, ref: null
            POI areas it is inside: none
            """.ReplaceLineEndings(),
            text.ReplaceLineEndings());
    }

    [Fact]
    public void Describe_Building_ListsPoiAreasAndNullSources()
    {
        var places = Places();

        var text = PlaceText.Describe(places.Building("osm:way/200"), places);

        Assert.Contains("category: education (category_source: osm_poi_area)", text);
        Assert.Contains("mml_building: null, mml_overlap: null", text);
        Assert.Contains("name: null, name_sv: null, ref: null (name_source: null)", text);
        Assert.Contains("address: street null, housenumber null (address_source: null)", text);
        Assert.Contains("height: 6 m (height_source: default)", text);
        Assert.Contains("POIs on or in it: none", text);
        Assert.EndsWith(
            "POI areas it is inside:" + Environment.NewLine
            + "  osm:way/600 amenity=school, name: \"Koulu\", name_sv: \"Skola\", ref: null",
            text);
    }

    [Fact]
    public void Describe_Building_ShowsEachOverrideUnderTheFieldItSet()
    {
        var buildings = Apply(BuildingsReaderTests.ValidBuildings, root =>
        {
            var building = root["buildings"]![0]!;
            building["category"] = "commercial";
            building["category_source"] = "override";
            building["category_raw"]!["override"] = "commercial";
            building["name"] = "Korjattu";
            building["name_source"] = "override";
            building["overrides"] = JsonNode.Parse(
                """
                [
                  {"id": "mml:rakennus/9001", "source": "playtest 2026-11-02", "licence": "own work", "fields": ["category"]},
                  {"id": "osm:way/100", "source": "Mapillary image 1", "licence": "CC BY-SA 4.0", "fields": ["name"]}
                ]
                """);
        });
        var places = PlaceIndex.Create(BuildingsReader.Parse(buildings), PoisReader.Parse(PoisReaderTests.ValidPois));

        var lines = PlaceText.Describe(places.Building("osm:way/100"), places).ReplaceLineEndings("\n").Split('\n');

        var category = Array.IndexOf(lines, "category: commercial (category_source: override)");
        Assert.True(category >= 0);
        Assert.Equal(
            "  override mml:rakennus/9001: source \"playtest 2026-11-02\", licence \"own work\", fields [category]",
            lines[category + 1]);
        var name = Array.IndexOf(lines, "name: \"Korjattu\", name_sv: null, ref: null (name_source: override)");
        Assert.True(name >= 0);
        Assert.Equal(
            "  override osm:way/100: source \"Mapillary image 1\", licence \"CC BY-SA 4.0\", fields [name]",
            lines[name + 1]);
        Assert.DoesNotContain("overrides: none", lines);
    }

    [Fact]
    public void Describe_PointPoi_ShowsEveryField()
    {
        var places = Places();

        var text = PlaceText.Describe(places.Poi("osm:node/900"));

        Assert.Equal(
            """
            POI osm:node/900
            type: amenity, subtype: "fuel", other_tags: [shop=convenience]
            name: "Asema", name_sv: null, ref: "7"
            geometry: point, position: [301, 90, 300]
            outline: null
            address: street null, housenumber null (address_source: null)
            building_id: null
            contains_building_ids: []
            """.ReplaceLineEndings(),
            text.ReplaceLineEndings());
    }

    [Fact]
    public void Describe_AreaPoi_SummarisesTheOutline()
    {
        var places = Places();

        var school = PlaceText.Describe(places.Poi("osm:way/600"));
        var park = PlaceText.Describe(places.Poi("osm:way/700"));

        Assert.Contains("outline: 1 polygon(s), 2 ring(s), area 9600 m²", school);
        Assert.Contains("address: street \"Koulukatu\", housenumber null (address_source: osm)", school);
        Assert.Contains("contains_building_ids: [osm:way/200]", school);
        Assert.Contains("outline: 2 polygon(s), 2 ring(s), area 200 m²", park);
    }

    [Fact]
    public void Messages()
    {
        Assert.Equal("No place data in this pack; run /regen-map somero", PlaceText.NoPlacesMessage("somero"));
        Assert.Equal("No building or point of interest here", PlaceText.NothingHere);
    }

    [Fact]
    public void WireNames_CoverEveryEnumValueWithDistinctNames()
    {
        AssertDistinct<BuildingCategory>(PlaceText.WireName);
        AssertDistinct<BuildingCategorySource>(PlaceText.WireName);
        AssertDistinct<BuildingNameSource>(PlaceText.WireName);
        AssertDistinct<AddressSource>(PlaceText.WireName);
        AssertDistinct<BuildingHeightSource>(PlaceText.WireName);
        AssertDistinct<BuildingOverrideField>(PlaceText.WireName);
        AssertDistinct<PoiType>(PlaceText.WireName);
        AssertDistinct<PoiGeometry>(PlaceText.WireName);
    }

    [Theory]
    [InlineData("garage_or_shed")]
    [InlineData("agricultural")]
    [InlineData("public")]
    public void WireName_RoundTripsTheCategoriesTheReaderAccepts(string category)
    {
        var json = Apply(BuildingsReaderTests.ValidBuildings, root =>
        {
            root["buildings"]![1]!["category"] = category;
        });

        var building = BuildingsReader.Parse(json).Items[1];

        Assert.Equal(category, PlaceText.WireName(building.Category));
    }

    [Theory]
    [InlineData("mml_3d")]
    [InlineData("osm_height")]
    [InlineData("osm_levels")]
    public void WireName_RoundTripsTheHeightSourcesTheReaderAccepts(string source)
    {
        var json = Apply(BuildingsReaderTests.ValidBuildings, root =>
        {
            root["buildings"]![1]!["height_source"] = source;
        });

        var building = BuildingsReader.Parse(json).Items[1];

        Assert.Equal(source, PlaceText.WireName(building.HeightSource));
    }

    private static void AssertDistinct<T>(Func<T, string> wireName)
        where T : struct, Enum
    {
        var names = Enum.GetValues<T>().Select(wireName).ToList();
        Assert.All(names, name => Assert.Matches("^[a-z0-9_]+$", name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static PlaceIndex Places() =>
        PlaceIndex.Create(
            BuildingsReader.Parse(BuildingsReaderTests.ValidBuildings),
            PoisReader.Parse(PoisReaderTests.ValidPois));

    private static string Apply(string json, Action<JsonNode> change)
    {
        var root = JsonNode.Parse(json)!;
        change(root);
        return root.ToJsonString();
    }
}
