using System.Text.Json;
using System.Text.RegularExpressions;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a pack's <c>buildings.json</c> (schema version 2, <c>docs/specs/place-information.md</c>).
/// Any problem throws a <see cref="MapPackException"/> that names the file and the field, like
/// <see cref="RoadGraphReader"/>: another schema version (version 1 packs must be rebuilt), a missing, mistyped or
/// unknown field (nullable keys must still be present; the lidar keys are the only optional ones), an unknown
/// category, source or other value, a duplicate building id or glTF node name, a <c>category_source</c> without its
/// raw value, a null <c>category_source</c> on a category other than <c>unknown</c>, a <c>name_source</c> or
/// <c>address_source</c> that doesn't agree with its values, <c>mml_building</c> and <c>mml_overlap</c> not null
/// together, an overlap outside 0 to 1, lidar keys that are incomplete or don't agree with <c>height_source</c>,
/// a height not above 0, a negative lidar point count or buffer, an <c>overrides</c> entry with a repeated id, empty or unknown <c>fields</c>, a <c>category_source</c> or <c>name_source</c> of <c>override</c> without an entry that set it (or such an entry without that source), and non-finite numbers. Links to <c>pois.json</c> are checked by <see cref="PlaceIndex.Create"/>.
/// </summary>
public static class BuildingsReader
{
    public const int SupportedSchemaVersion = 2;

    /// <summary>True when <paramref name="id"/> matches the pipeline's building id pattern <c>&lt;source&gt;:&lt;table&gt;/&lt;number&gt;</c>.</summary>
    public static bool IsBuildingId(string id) => BuildingIdPattern.IsMatch(id);

    /// <summary>
    /// The pipeline's building id pattern (<c>BuildingId</c> in <c>tools/mapgen/src/mapgen/build/places/model.py</c>):
    /// <c>&lt;source&gt;:&lt;table&gt;/&lt;number&gt;</c>, e.g. <c>osm:way/123</c>. Map-agnostic (ADR 0002).
    /// </summary>
    private static readonly Regex BuildingIdPattern =
        new("^[a-z][a-z0-9_]*:[a-z][a-z0-9_]*/[0-9]+$", RegexOptions.CultureInvariant);

    private static readonly string[] RootFields = ["schema_version", "map_id", "buildings"];

    private static readonly string[] LidarFields =
        ["lidar_points", "lidar_buffer_m", "lidar_returns", "roof_z_median", "roof_z_p90"];

    private static readonly string[] BuildingFields =
    [
        "id", "tile", "node", "collision_nodes",
        "height_m", "height_source", "base_z", "base_source", "roof_shape", "roof_shape_source",
        "lidar_points", "lidar_buffer_m", "lidar_returns", "roof_z_median", "roof_z_p90", "lidar_rejected",
        "category", "category_source", "category_raw",
        "name", "name_sv", "ref", "name_source",
        "address", "address_source",
        "poi_ids", "area_poi_ids",
        "mml_building", "mml_overlap",
        "overrides",
    ];

    private static readonly string[] OverrideFields = ["id", "source", "licence", "fields"];

    private static readonly Dictionary<string, BuildingOverrideField> OverrideFieldNames = new(StringComparer.Ordinal)
    {
        ["category"] = BuildingOverrideField.Category,
        ["name"] = BuildingOverrideField.Name,
    };

    private static readonly string[] CategoryRawFields =
        ["osm_building", "osm_poi", "osm_poi_area", "mml_feature_class", "mml_use_class", "osm_landuse", "override"];

    internal static readonly Dictionary<string, BuildingCategory> Categories = new(StringComparer.Ordinal)
    {
        ["residential"] = BuildingCategory.Residential,
        ["commercial"] = BuildingCategory.Commercial,
        ["retail"] = BuildingCategory.Retail,
        ["industrial"] = BuildingCategory.Industrial,
        ["public"] = BuildingCategory.Public,
        ["religious"] = BuildingCategory.Religious,
        ["education"] = BuildingCategory.Education,
        ["agricultural"] = BuildingCategory.Agricultural,
        ["garage_or_shed"] = BuildingCategory.GarageOrShed,
        ["other"] = BuildingCategory.Other,
        ["unknown"] = BuildingCategory.Unknown,
    };

    private static readonly Dictionary<string, BuildingCategorySource> CategorySources = new(StringComparer.Ordinal)
    {
        ["override"] = BuildingCategorySource.Override,
        ["osm_building"] = BuildingCategorySource.OsmBuilding,
        ["osm_poi"] = BuildingCategorySource.OsmPoi,
        ["osm_poi_area"] = BuildingCategorySource.OsmPoiArea,
        ["mml_use_class"] = BuildingCategorySource.MmlUseClass,
        ["osm_landuse"] = BuildingCategorySource.OsmLanduse,
    };

    private static readonly Dictionary<string, BuildingNameSource> NameSources = new(StringComparer.Ordinal)
    {
        ["override"] = BuildingNameSource.Override,
        ["osm"] = BuildingNameSource.Osm,
        ["osm_poi"] = BuildingNameSource.OsmPoi,
    };

    private static readonly Dictionary<string, AddressSource> AddressSources = new(StringComparer.Ordinal)
    {
        ["osm"] = AddressSource.Osm,
    };

    private static readonly Dictionary<string, BuildingHeightSource> HeightSources = new(StringComparer.Ordinal)
    {
        ["mml_lidar"] = BuildingHeightSource.MmlLidar,
        ["mml_3d"] = BuildingHeightSource.Mml3d,
        ["osm_height"] = BuildingHeightSource.OsmHeight,
        ["osm_levels"] = BuildingHeightSource.OsmLevels,
        ["default"] = BuildingHeightSource.Default,
    };

    private static readonly Dictionary<string, BuildingBaseSource> BaseSources = new(StringComparer.Ordinal)
    {
        ["dem"] = BuildingBaseSource.Dem,
    };

    private static readonly Dictionary<string, RoofShape> RoofShapes = new(StringComparer.Ordinal)
    {
        ["flat"] = RoofShape.Flat,
    };

    private static readonly Dictionary<string, RoofShapeSource> RoofShapeSources = new(StringComparer.Ordinal)
    {
        ["default"] = RoofShapeSource.Default,
    };

    private static readonly Dictionary<string, LidarReturns> Returns = new(StringComparer.Ordinal)
    {
        ["all"] = LidarReturns.All,
        ["single"] = LidarReturns.Single,
    };

    private static readonly Dictionary<string, LidarRejectReason> RejectReasons = new(StringComparer.Ordinal)
    {
        ["few_points"] = LidarRejectReason.FewPoints,
        ["tree_cover"] = LidarRejectReason.TreeCover,
        ["too_low"] = LidarRejectReason.TooLow,
        ["too_high"] = LidarRejectReason.TooHigh,
    };

    /// <param name="json">The file's contents.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static Buildings Parse(string json, string source = MapPack.BuildingsFile)
    {
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);

        // The version comes first, so a version 1 file says "rebuild" rather than naming its missing fields.
        var version = PackJson.Int(root, "schema_version", source, "");
        if (version != SupportedSchemaVersion)
        {
            throw new MapPackException(
                $"{source}: schema_version {version} is not supported; this game reads version {SupportedSchemaVersion}");
        }

        PackJson.RejectUnknown(root, source, "", RootFields);
        var mapId = PackJson.String(root, "map_id", source, "");
        if (!MapPack.IsValidMapId(mapId))
        {
            throw PackJson.Error(source, "map_id", $"'{mapId}' is not a lowercase slug");
        }

        var buildings = new List<Building>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in PackJson.Array(root, "buildings", source, "").EnumerateArray())
        {
            var path = $"buildings[{index}]";
            PackJson.RequireObject(element, source, path);
            var building = ParseBuilding(element, source, path);
            if (!ids.Add(building.Id))
            {
                throw PackJson.Error(source, $"{path}.id", $"building id '{building.Id}' appears more than once");
            }

            foreach (var node in building.CollisionNodes.Prepend(building.Node))
            {
                if (!nodeNames.TryAdd(node, building.Id))
                {
                    throw PackJson.Error(
                        source,
                        path,
                        $"glTF node '{node}' of building '{building.Id}' is already used by building '{nodeNames[node]}'");
                }
            }

            buildings.Add(building);
            index++;
        }

        return new Buildings(version, mapId, buildings);
    }

    private static Building ParseBuilding(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, BuildingFields);
        var id = PackJson.NonEmptyString(element, "id", source, path);
        var tile = PackJson.NonEmptyString(element, "tile", source, path);
        var node = PackJson.NonEmptyString(element, "node", source, path);
        var collisionNodes = PackJson.Strings(element, "collision_nodes", source, path);
        if (collisionNodes.Count == 0)
        {
            throw PackJson.Error(source, $"{path}.collision_nodes", $"must not be empty (building '{id}')");
        }

        var height = PackJson.Number(element, "height_m", source, path);
        if (height <= 0)
        {
            throw PackJson.Error(source, $"{path}.height_m", $"must be greater than 0, not {height}");
        }

        var heightSource = PackJson.Choice(element, "height_source", HeightSources, source, path);
        var baseZ = PackJson.Number(element, "base_z", source, path);
        var baseSource = PackJson.Choice(element, "base_source", BaseSources, source, path);
        var roofShape = PackJson.Choice(element, "roof_shape", RoofShapes, source, path);
        var roofShapeSource = PackJson.Choice(element, "roof_shape_source", RoofShapeSources, source, path);
        var (lidar, rejected) = Lidar(element, heightSource, source, path);

        var category = PackJson.Choice(element, "category", Categories, source, path);
        var categorySource = PackJson.NullableChoice(element, "category_source", CategorySources, source, path);
        var raw = CategoryRaw(element, source, path);
        CheckCategorySource(category, categorySource, raw, source, path);

        var names = PackJson.Names(element, source, path);
        var nameSource = PackJson.NullableChoice(element, "name_source", NameSources, source, path);
        var hasName = names.Name is not null || names.NameSv is not null || names.Ref is not null;
        if (nameSource is null && hasName)
        {
            throw PackJson.Error(source, $"{path}.name_source", "is null, but the building has a name field");
        }

        if (nameSource is BuildingNameSource.Osm or BuildingNameSource.OsmPoi && !hasName)
        {
            throw PackJson.Error(source, $"{path}.name_source", "names a source, but all name fields are null");
        }

        var address = PackJson.Address(element, source, path);
        var addressSource = PackJson.NullableChoice(element, "address_source", AddressSources, source, path);
        if (address.HasValue != (addressSource is not null))
        {
            throw PackJson.Error(source, $"{path}.address_source", "must be 'osm' exactly when the address has a value");
        }

        var poiIds = PackJson.Strings(element, "poi_ids", source, path);
        var areaPoiIds = PackJson.Strings(element, "area_poi_ids", source, path);

        var mmlBuilding = PackJson.NullableString(element, "mml_building", source, path);
        if (mmlBuilding is not null && !IsBuildingId(mmlBuilding))
        {
            throw PackJson.Error(
                source, $"{path}.mml_building", $"must be a building id '<source>:<table>/<number>' or null, not '{mmlBuilding}'");
        }

        double? mmlOverlap = null;
        if (PackJson.RequiredNullable(element, "mml_overlap", source, path, out _))
        {
            mmlOverlap = PackJson.Number(element, "mml_overlap", source, path);
            if (mmlOverlap is < 0 or > 1)
            {
                throw PackJson.Error(source, $"{path}.mml_overlap", $"must be between 0 and 1, not {mmlOverlap}");
            }
        }

        if ((mmlBuilding is null) != (mmlOverlap is null))
        {
            throw PackJson.Error(source, $"{path}.mml_overlap", "must be null exactly when mml_building is null");
        }

        var overrides = Overrides(element, source, path);
        CheckOverrideSource(
            categorySource == BuildingCategorySource.Override, BuildingOverrideField.Category, "category_source", overrides, source, path);
        CheckOverrideSource(
            nameSource == BuildingNameSource.Override, BuildingOverrideField.Name, "name_source", overrides, source, path);

        return new Building(
            id, tile, node, collisionNodes,
            height, heightSource, baseZ, baseSource, roofShape, roofShapeSource,
            category, categorySource, raw,
            names, nameSource, address, addressSource,
            poiIds, areaPoiIds, mmlBuilding, mmlOverlap,
            lidar, rejected, overrides);
    }

    private static List<BuildingOverride> Overrides(JsonElement element, string source, string path)
    {
        var fieldPath = $"{path}.overrides";
        var result = new List<BuildingOverride>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var item in PackJson.Array(element, "overrides", source, path).EnumerateArray())
        {
            var itemPath = $"{fieldPath}[{i}]";
            PackJson.RequireObject(item, source, itemPath);
            PackJson.RejectUnknown(item, source, itemPath, OverrideFields);
            var id = PackJson.NonEmptyString(item, "id", source, itemPath);
            if (!ids.Add(id))
            {
                throw PackJson.Error(source, $"{itemPath}.id", $"override '{id}' appears more than once");
            }

            var entrySource = PackJson.NonEmptyString(item, "source", source, itemPath);
            var licence = PackJson.NonEmptyString(item, "licence", source, itemPath);
            var names = PackJson.Strings(item, "fields", source, itemPath);
            if (names.Count == 0)
            {
                throw PackJson.Error(source, $"{itemPath}.fields", $"must not be empty (override '{id}')");
            }

            var fields = new List<BuildingOverrideField>(names.Count);
            for (var j = 0; j < names.Count; j++)
            {
                if (!OverrideFieldNames.TryGetValue(names[j], out var field))
                {
                    throw PackJson.Error(
                        source,
                        $"{itemPath}.fields[{j}]",
                        $"'{names[j]}' is not a known value (known: {string.Join(", ", OverrideFieldNames.Keys)})");
                }

                fields.Add(field);
            }

            result.Add(new BuildingOverride(id, entrySource, licence, fields));
            i++;
        }

        return result;
    }

    /// <summary>A source of <c>override</c> needs an entry that set that field, and such an entry needs that source.</summary>
    private static void CheckOverrideSource(
        bool sourceIsOverride,
        BuildingOverrideField field,
        string sourceField,
        List<BuildingOverride> overrides,
        string source,
        string path)
    {
        var fieldName = OverrideFieldNames.First(pair => pair.Value == field).Key;
        var entry = overrides.FirstOrDefault(o => o.Fields.Contains(field));
        if (sourceIsOverride && entry is null)
        {
            throw PackJson.Error(
                source,
                $"{path}.{sourceField}",
                $"is 'override', but no overrides entry has '{fieldName}' in its fields");
        }

        if (!sourceIsOverride && entry is not null)
        {
            throw PackJson.Error(
                source,
                $"{path}.overrides",
                $"entry '{entry.Id}' set '{fieldName}', but {sourceField} isn't 'override'");
        }
    }

    private static BuildingCategoryRaw CategoryRaw(JsonElement element, string source, string path)
    {
        var raw = PackJson.Object(element, "category_raw", source, path);
        var rawPath = $"{path}.category_raw";
        PackJson.RejectUnknown(raw, source, rawPath, CategoryRawFields);
        return new BuildingCategoryRaw(
            PackJson.NullableString(raw, "osm_building", source, rawPath),
            PackJson.Strings(raw, "osm_poi", source, rawPath),
            PackJson.Strings(raw, "osm_poi_area", source, rawPath),
            PackJson.NullableLong(raw, "mml_feature_class", source, rawPath),
            PackJson.NullableLong(raw, "mml_use_class", source, rawPath),
            PackJson.NullableString(raw, "osm_landuse", source, rawPath),
            PackJson.NullableChoice(raw, "override", Categories, source, rawPath));
    }

    /// <summary>The winning step must have a raw value; no step means <c>unknown</c>.</summary>
    private static void CheckCategorySource(
        BuildingCategory category,
        BuildingCategorySource? categorySource,
        BuildingCategoryRaw raw,
        string source,
        string path)
    {
        var (hasRaw, rawName) = categorySource switch
        {
            null => (true, ""),
            BuildingCategorySource.Override => (raw.Override is not null, "override"),
            BuildingCategorySource.OsmBuilding => (raw.OsmBuilding is not null, "osm_building"),
            BuildingCategorySource.OsmPoi => (raw.OsmPoi.Count > 0, "osm_poi"),
            BuildingCategorySource.OsmPoiArea => (raw.OsmPoiArea.Count > 0, "osm_poi_area"),
            BuildingCategorySource.MmlUseClass => (raw.MmlFeatureClass is not null, "mml_feature_class"),
            BuildingCategorySource.OsmLanduse => (raw.OsmLanduse is not null, "osm_landuse"),
            _ => (false, "?"),
        };
        if (!hasRaw)
        {
            throw PackJson.Error(
                source,
                $"{path}.category_source",
                $"is '{CategorySources.First(pair => pair.Value == categorySource).Key}', but category_raw.{rawName} has no value");
        }

        if (categorySource is null && category != BuildingCategory.Unknown)
        {
            throw PackJson.Error(source, $"{path}.category_source", "is null, but the category isn't 'unknown'");
        }
    }

    private static (BuildingLidar? Lidar, LidarRejectReason? Rejected) Lidar(
        JsonElement element,
        BuildingHeightSource heightSource,
        string source,
        string path)
    {
        var present = LidarFields.Where(name => PackJson.TryGet(element, name, out _)).ToList();
        var hasRejected = PackJson.TryGet(element, "lidar_rejected", out _);
        if (hasRejected)
        {
            if (present.Count > 0)
            {
                throw PackJson.Error(source, $"{path}.{present[0]}", "must not be given together with lidar_rejected");
            }

            return (null, PackJson.Choice(element, "lidar_rejected", RejectReasons, source, path));
        }

        if (present.Count == 0)
        {
            if (heightSource == BuildingHeightSource.MmlLidar)
            {
                throw PackJson.Error(source, $"{path}.lidar_points", "is missing; height_source 'mml_lidar' needs the lidar fields");
            }

            return (null, null);
        }

        if (present.Count < LidarFields.Length)
        {
            var missing = LidarFields.First(name => !present.Contains(name));
            throw PackJson.Error(source, $"{path}.{missing}", "is missing; the lidar fields come together");
        }

        if (heightSource != BuildingHeightSource.MmlLidar)
        {
            throw PackJson.Error(source, $"{path}.lidar_points", "is only allowed when height_source is 'mml_lidar'");
        }

        var points = PackJson.Int(element, "lidar_points", source, path);
        if (points < 0)
        {
            throw PackJson.Error(source, $"{path}.lidar_points", $"must not be negative, not {points}");
        }

        var buffer = PackJson.Number(element, "lidar_buffer_m", source, path);
        if (buffer < 0)
        {
            throw PackJson.Error(source, $"{path}.lidar_buffer_m", $"must not be negative, not {buffer}");
        }
        var lidar = new BuildingLidar(
            points,
            buffer,
            PackJson.Choice(element, "lidar_returns", Returns, source, path),
            PackJson.Number(element, "roof_z_median", source, path),
            PackJson.Number(element, "roof_z_p90", source, path));
        return (lidar, null);
    }
}
