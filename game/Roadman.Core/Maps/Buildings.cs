namespace Roadman.Core.Maps;

/// <summary>A building's resolved category (<c>category</c> in <c>buildings.json</c>).</summary>
public enum BuildingCategory
{
    /// <summary><c>residential</c></summary>
    Residential,

    /// <summary><c>commercial</c></summary>
    Commercial,

    /// <summary><c>retail</c></summary>
    Retail,

    /// <summary><c>industrial</c></summary>
    Industrial,

    /// <summary><c>public</c></summary>
    Public,

    /// <summary><c>religious</c></summary>
    Religious,

    /// <summary><c>education</c></summary>
    Education,

    /// <summary><c>agricultural</c></summary>
    Agricultural,

    /// <summary><c>garage_or_shed</c></summary>
    GarageOrShed,

    /// <summary><c>other</c>: a tag value the pipeline's tables don't list.</summary>
    Other,

    /// <summary><c>unknown</c>: no source gave a value.</summary>
    Unknown,
}

/// <summary>The step that decided a building's category (<c>category_source</c>; null for <c>unknown</c>).</summary>
public enum BuildingCategorySource
{
    /// <summary>The map's override file (<c>override</c>).</summary>
    Override,

    /// <summary>The building's <c>building</c> tag (<c>osm_building</c>).</summary>
    OsmBuilding,

    /// <summary>A POI tag on the building or on a POI on or in it (<c>osm_poi</c>).</summary>
    OsmPoi,

    /// <summary>The POI areas the building is inside (<c>osm_poi_area</c>).</summary>
    OsmPoiArea,

    /// <summary>The matched MML building's feature class (<c>mml_use_class</c>).</summary>
    MmlUseClass,

    /// <summary>The land use area under the footprint's centroid (<c>osm_landuse</c>).</summary>
    OsmLanduse,
}

/// <summary>Where a building's name fields came from (<c>name_source</c>; null when all are null).</summary>
public enum BuildingNameSource
{
    /// <summary>The map's override file (<c>override</c>).</summary>
    Override,

    /// <summary>The building's own tags (<c>osm</c>).</summary>
    Osm,

    /// <summary>The only named POI on or in the building (<c>osm_poi</c>).</summary>
    OsmPoi,
}

/// <summary>Where an address came from (<c>address_source</c>; null when it has no value).</summary>
public enum AddressSource
{
    /// <summary>The feature's own <c>addr:*</c> tags (<c>osm</c>).</summary>
    Osm,

    /// <summary>POIs only: the address of the building it is on or in (<c>building</c>).</summary>
    Building,
}

/// <summary>Where a building's height came from (<c>height_source</c>).</summary>
public enum BuildingHeightSource
{
    /// <summary>The lidar point cloud (<c>mml_lidar</c>).</summary>
    MmlLidar,

    /// <summary>An MML 3D building (<c>mml_3d</c>).</summary>
    Mml3d,

    /// <summary>The OSM <c>height</c> tag (<c>osm_height</c>).</summary>
    OsmHeight,

    /// <summary>The OSM <c>building:levels</c> tag (<c>osm_levels</c>).</summary>
    OsmLevels,

    /// <summary>The pipeline's default height (<c>default</c>).</summary>
    Default,
}

/// <summary>Where a building's base came from (<c>base_source</c>).</summary>
public enum BuildingBaseSource
{
    /// <summary>The terrain model (<c>dem</c>).</summary>
    Dem,
}

/// <summary>A building's roof shape (<c>roof_shape</c>).</summary>
public enum RoofShape
{
    /// <summary><c>flat</c></summary>
    Flat,
}

/// <summary>Where a building's roof shape came from (<c>roof_shape_source</c>).</summary>
public enum RoofShapeSource
{
    /// <summary>The pipeline's default (<c>default</c>).</summary>
    Default,
}

/// <summary>Which lidar returns the roof points were taken from (<c>lidar_returns</c>).</summary>
public enum LidarReturns
{
    /// <summary><c>all</c></summary>
    All,

    /// <summary><c>single</c>: only single returns, under tree cover.</summary>
    Single,
}

/// <summary>Why the lidar height of a building was rejected (<c>lidar_rejected</c>).</summary>
public enum LidarRejectReason
{
    /// <summary><c>few_points</c></summary>
    FewPoints,

    /// <summary><c>tree_cover</c></summary>
    TreeCover,

    /// <summary><c>too_low</c></summary>
    TooLow,

    /// <summary><c>too_high</c></summary>
    TooHigh,
}

/// <summary>
/// A street address (<c>address</c>), from <c>addr:street</c> and <c>addr:housenumber</c>. Each part is null when the
/// tag is missing; values follow the name field rules and house numbers such as <c>12a</c> or <c>3-5</c> are kept as tagged.
/// </summary>
public sealed record Address(string? Street, string? HouseNumber)
{
    public static Address None { get; } = new(null, null);

    /// <summary>True when either part has a value.</summary>
    public bool HasValue => Street is not null || HouseNumber is not null;
}

/// <summary>
/// The raw values every category step saw (<c>category_raw</c>), kept whichever step won.
/// </summary>
/// <param name="OsmBuilding">The <c>building</c> tag.</param>
/// <param name="OsmPoi">The <c>key=value</c> POI tags considered in the POI step, sorted; possibly empty.</param>
/// <param name="OsmPoiArea">The <c>key=value</c> tags of the POI areas the building is inside, sorted; possibly empty.</param>
/// <param name="MmlFeatureClass">The matched MML building's <c>kohdeluokka</c>.</param>
/// <param name="MmlUseClass">The matched MML building's <c>kayttotarkoitus</c>.</param>
/// <param name="OsmLanduse">The <c>landuse</c> value under the footprint's centroid.</param>
/// <param name="Override">The category from the override file.</param>
public sealed record BuildingCategoryRaw(
    string? OsmBuilding,
    IReadOnlyList<string> OsmPoi,
    IReadOnlyList<string> OsmPoiArea,
    long? MmlFeatureClass,
    long? MmlUseClass,
    string? OsmLanduse,
    BuildingCategory? Override);

/// <summary>A building attribute an override can set (<c>fields</c> of an <c>overrides</c> entry).</summary>
public enum BuildingOverrideField
{
    /// <summary>The category (<c>category</c>).</summary>
    Category,

    /// <summary>All three name fields (<c>name</c>).</summary>
    Name,
}

/// <summary>
/// An override file entry that set at least one attribute of a building (one entry of <c>overrides</c>).
/// </summary>
/// <param name="Id">The override file key: a building id (<c>osm:way/&lt;id&gt;</c>) or an MML id (<c>mml:rakennus/&lt;mtk_id&gt;</c>).</param>
/// <param name="Source">Where the corrected value comes from, as recorded in the override file.</param>
/// <param name="Licence">The entry's licence (<c>own work</c> or a third-party licence).</param>
/// <param name="Fields">The attributes this entry actually set, at least one, each at most once.</param>
public sealed record BuildingOverride(string Id, string Source, string Licence, IReadOnlyList<BuildingOverrideField> Fields);

/// <summary>
/// The lidar points a building's height was measured from (<c>lidar_points</c>, <c>lidar_buffer_m</c>,
/// <c>lidar_returns</c>, <c>roof_z_median</c>, <c>roof_z_p90</c>); present exactly when the height source is lidar.
/// </summary>
/// <param name="RoofZMedian">Absolute elevation in metres.</param>
/// <param name="RoofZP90">Absolute elevation in metres, kept for pitched roofs.</param>
public sealed record BuildingLidar(int Points, double BufferM, LidarReturns Returns, double RoofZMedian, double RoofZP90);

/// <summary>One entry of <c>buildings.json</c>: provenance, category, name and address of a building.</summary>
/// <param name="Id">Stable id: <c>osm:way/&lt;id&gt;</c> or <c>osm:relation/&lt;id&gt;</c>.</param>
/// <param name="Tile">The tile id whose glTF holds the building.</param>
/// <param name="Node">The glTF node name of the visual mesh.</param>
/// <param name="CollisionNodes">The glTF node names of the collision meshes, one per footprint part.</param>
/// <param name="HeightM">Height above the base in metres, greater than 0.</param>
/// <param name="BaseZ">Base elevation in Godot local metres.</param>
/// <param name="Category">The resolved category.</param>
/// <param name="CategorySource">The step that decided it; null when no step had a value (then the category is <see cref="BuildingCategory.Unknown"/>).</param>
/// <param name="CategoryRaw">Raw values of every step.</param>
/// <param name="Names">Name fields.</param>
/// <param name="NameSource">Where <paramref name="Names"/> came from; null when all are null.</param>
/// <param name="Address">The building's own address.</param>
/// <param name="AddressSource"><see cref="Maps.AddressSource.Osm"/> when the address has a value, else null.</param>
/// <param name="PoiIds">The POIs on or in the building.</param>
/// <param name="AreaPoiIds">The POI areas the building is inside.</param>
/// <param name="MmlBuilding">The matched MML building (<c>mml:rakennus/&lt;mtk_id&gt;</c>).</param>
/// <param name="MmlOverlap">Share of the footprint the MML building covers, 0 to 1; null exactly when <paramref name="MmlBuilding"/> is.</param>
/// <param name="Lidar">The lidar measurement, when the height came from lidar.</param>
/// <param name="LidarRejected">Why lidar wasn't used, when it was tried and rejected.</param>
/// <param name="Overrides">The override entries that set something on this building, sorted by id; empty when none applies.</param>
public sealed record Building(
    string Id,
    string Tile,
    string Node,
    IReadOnlyList<string> CollisionNodes,
    double HeightM,
    BuildingHeightSource HeightSource,
    double BaseZ,
    BuildingBaseSource BaseSource,
    RoofShape RoofShape,
    RoofShapeSource RoofShapeSource,
    BuildingCategory Category,
    BuildingCategorySource? CategorySource,
    BuildingCategoryRaw CategoryRaw,
    FeatureNames Names,
    BuildingNameSource? NameSource,
    Address Address,
    AddressSource? AddressSource,
    IReadOnlyList<string> PoiIds,
    IReadOnlyList<string> AreaPoiIds,
    string? MmlBuilding,
    double? MmlOverlap,
    BuildingLidar? Lidar,
    LidarRejectReason? LidarRejected,
    IReadOnlyList<BuildingOverride> Overrides);

/// <summary>
/// The pack's <c>buildings.json</c>. Read it with <see cref="BuildingsReader"/>, which guarantees unique building ids
/// and glTF node names; join it with the POIs through <see cref="PlaceIndex.Create"/>.
/// </summary>
/// <param name="Items">Every building, in file order.</param>
public sealed record Buildings(int SchemaVersion, string MapId, IReadOnlyList<Building> Items);
