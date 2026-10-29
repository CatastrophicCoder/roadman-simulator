using System.Globalization;
using System.Text;

namespace Roadman.Core.Maps;

/// <summary>
/// The debug inspector's panel text for a building or point of interest (<c>docs/specs/place-information.md</c>,
/// "Game"). It shows the pack's raw values with their file spellings (<c>garage_or_shed</c>, <c>osm_poi_area</c>),
/// including real business names: the inspector is a debug tool and never ships. Numbers use the invariant culture.
/// </summary>
public static class PlaceText
{
    /// <summary>Shown when the ray hits neither a building nor anything near a point of interest.</summary>
    public const string NothingHere = "No building or point of interest here";

    /// <summary>Shown when the pack has no readable <c>buildings.json</c> and <c>pois.json</c>.</summary>
    public static string NoPlacesMessage(string mapId) => $"No place data in this pack; run /regen-map {mapId}";

    /// <summary>
    /// Every field of <paramref name="building"/> the inspector shows: id, category with its source and the
    /// overrides that set it, every raw category value, the MML match, name fields with their source and overrides,
    /// address, height, and the POIs on or in it and the POI areas it is inside (from <paramref name="places"/>).
    /// </summary>
    public static string Describe(Building building, PlaceIndex places)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(places);
        var text = new StringBuilder();
        text.AppendLine($"Building {building.Id}");
        text.AppendLine($"category: {WireName(building.Category)} (category_source: {Or(building.CategorySource, WireName)})");
        AppendOverrides(text, building, BuildingOverrideField.Category);

        var raw = building.CategoryRaw;
        text.AppendLine("category_raw:");
        text.AppendLine($"  osm_building: {Quote(raw.OsmBuilding)}");
        text.AppendLine($"  osm_poi: {List(raw.OsmPoi)}");
        text.AppendLine($"  osm_poi_area: {List(raw.OsmPoiArea)}");
        text.AppendLine($"  mml_feature_class: {Or(raw.MmlFeatureClass, Number)}");
        text.AppendLine($"  mml_use_class: {Or(raw.MmlUseClass, Number)}");
        text.AppendLine($"  osm_landuse: {Quote(raw.OsmLanduse)}");
        text.AppendLine($"  override: {Or(raw.Override, WireName)}");

        text.AppendLine($"mml_building: {Quote(building.MmlBuilding)}, mml_overlap: {Or(building.MmlOverlap, Number)}");
        text.AppendLine($"{Names(building.Names)} (name_source: {Or(building.NameSource, WireName)})");
        AppendOverrides(text, building, BuildingOverrideField.Name);
        if (building.Overrides.Count == 0)
        {
            text.AppendLine("overrides: none");
        }

        text.AppendLine($"address: {AddressText(building.Address)} (address_source: {Or(building.AddressSource, WireName)})");
        text.AppendLine($"height: {Number(building.HeightM)} m (height_source: {WireName(building.HeightSource)})");

        var pois = places.PoisOnOrIn(building);
        text.AppendLine(pois.Count == 0 ? "POIs on or in it: none" : "POIs on or in it:");
        foreach (var poi in pois)
        {
            text.AppendLine($"  {Summary(poi)}");
        }

        var areas = places.PoiAreasContaining(building);
        text.Append(areas.Count == 0 ? "POI areas it is inside: none" : "POI areas it is inside:");
        foreach (var area in areas)
        {
            text.AppendLine();
            text.Append($"  {Summary(area)}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Every field of <paramref name="poi"/>. The outline is summarised (polygon and ring counts, area), as its points
    /// don't fit on a screen.
    /// </summary>
    public static string Describe(Poi poi)
    {
        ArgumentNullException.ThrowIfNull(poi);
        var text = new StringBuilder();
        text.AppendLine($"POI {poi.Id}");
        text.AppendLine($"type: {WireName(poi.Type)}, subtype: {Quote(poi.Subtype)}, other_tags: {List(poi.OtherTags)}");
        text.AppendLine(Names(poi.Names));
        text.AppendLine($"geometry: {WireName(poi.Geometry)}, position: {Point(poi.Position)}");
        text.AppendLine($"outline: {OutlineText(poi)}");
        text.AppendLine($"address: {AddressText(poi.Address)} (address_source: {Or(poi.AddressSource, WireName)})");
        text.AppendLine($"building_id: {Quote(poi.BuildingId)}");
        text.Append($"contains_building_ids: {List(poi.ContainsBuildingIds)}");
        return text.ToString();
    }

    /// <summary>One line for a POI in a building's lists: id, type, subtype and name fields.</summary>
    public static string Summary(Poi poi)
    {
        ArgumentNullException.ThrowIfNull(poi);
        return $"{poi.Id} {WireName(poi.Type)}={poi.Subtype}, {Names(poi.Names)}";
    }

    /// <summary>One override entry: id, source, licence and the fields it set.</summary>
    public static string Summary(BuildingOverride entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var fields = string.Join(", ", entry.Fields.Select(WireName));
        return $"override {entry.Id}: source {Quote(entry.Source)}, licence {Quote(entry.Licence)}, fields [{fields}]";
    }

    public static string WireName(BuildingCategory value) =>
        value switch
        {
            BuildingCategory.Residential => "residential",
            BuildingCategory.Commercial => "commercial",
            BuildingCategory.Retail => "retail",
            BuildingCategory.Industrial => "industrial",
            BuildingCategory.Public => "public",
            BuildingCategory.Religious => "religious",
            BuildingCategory.Education => "education",
            BuildingCategory.Agricultural => "agricultural",
            BuildingCategory.GarageOrShed => "garage_or_shed",
            BuildingCategory.Other => "other",
            BuildingCategory.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(BuildingCategorySource value) =>
        value switch
        {
            BuildingCategorySource.Override => "override",
            BuildingCategorySource.OsmBuilding => "osm_building",
            BuildingCategorySource.OsmPoi => "osm_poi",
            BuildingCategorySource.OsmPoiArea => "osm_poi_area",
            BuildingCategorySource.MmlUseClass => "mml_use_class",
            BuildingCategorySource.OsmLanduse => "osm_landuse",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(BuildingNameSource value) =>
        value switch
        {
            BuildingNameSource.Override => "override",
            BuildingNameSource.Osm => "osm",
            BuildingNameSource.OsmPoi => "osm_poi",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(AddressSource value) =>
        value switch
        {
            AddressSource.Osm => "osm",
            AddressSource.Building => "building",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(BuildingHeightSource value) =>
        value switch
        {
            BuildingHeightSource.MmlLidar => "mml_lidar",
            BuildingHeightSource.Mml3d => "mml_3d",
            BuildingHeightSource.OsmHeight => "osm_height",
            BuildingHeightSource.OsmLevels => "osm_levels",
            BuildingHeightSource.Default => "default",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(BuildingOverrideField value) =>
        value switch
        {
            BuildingOverrideField.Category => "category",
            BuildingOverrideField.Name => "name",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(PoiType value) =>
        value switch
        {
            PoiType.Shop => "shop",
            PoiType.Amenity => "amenity",
            PoiType.Office => "office",
            PoiType.Tourism => "tourism",
            PoiType.Leisure => "leisure",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    public static string WireName(PoiGeometry value) =>
        value switch
        {
            PoiGeometry.Point => "point",
            PoiGeometry.Area => "area",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    /// <summary>The overrides that set <paramref name="field"/>, one indented line each, under that field's line.</summary>
    private static void AppendOverrides(StringBuilder text, Building building, BuildingOverrideField field)
    {
        foreach (var entry in building.Overrides.Where(entry => entry.Fields.Contains(field)))
        {
            text.AppendLine($"  {Summary(entry)}");
        }
    }

    private static string Names(FeatureNames names) =>
        $"name: {Quote(names.Name)}, name_sv: {Quote(names.NameSv)}, ref: {Quote(names.Ref)}";

    private static string AddressText(Address address) =>
        $"street {Quote(address.Street)}, housenumber {Quote(address.HouseNumber)}";

    private static string OutlineText(Poi poi)
    {
        if (poi.Outline is not { } outline)
        {
            return "null";
        }

        var rings = outline.Sum(polygon => 1 + polygon.Holes.Count);
        return $"{outline.Count} polygon(s), {rings} ring(s), area {Number(Math.Round(poi.Area, 1))} m²";
    }

    private static string Point(LocalPoint point) =>
        $"[{Number(point.X)}, {Number(point.Y)}, {Number(point.Z)}]";

    private static string List(IReadOnlyList<string> values) =>
        $"[{string.Join(", ", values)}]";

    private static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Or<T>(T? value, Func<T, string> format)
        where T : struct => value is { } present ? format(present) : "null";
}
