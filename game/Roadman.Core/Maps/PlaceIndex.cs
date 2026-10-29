namespace Roadman.Core.Maps;

/// <summary>
/// The pack's buildings and points of interest joined by their links, with the lookups the HUD and the debug
/// inspector need. Create it with <see cref="Create"/>, which checks that every link agrees in both directions.
/// </summary>
public sealed class PlaceIndex
{
    private readonly Dictionary<string, Building> _buildings;
    private readonly Dictionary<string, Building> _buildingsByNode;
    private readonly Dictionary<string, Poi> _pois;
    private readonly List<Poi> _areasBySize;
    private readonly List<Poi> _points;

    private PlaceIndex(
        Buildings buildings,
        Pois pois,
        Dictionary<string, Building> byId,
        Dictionary<string, Building> byNode,
        Dictionary<string, Poi> poisById)
    {
        Buildings = buildings;
        Pois = pois;
        _buildings = byId;
        _buildingsByNode = byNode;
        _pois = poisById;
        _areasBySize = pois.Items
            .Where(poi => poi.Geometry == PoiGeometry.Area)
            .OrderBy(poi => poi.Area)
            .ThenBy(poi => poi.Id, StringComparer.Ordinal)
            .ToList();
        _points = pois.Items.Where(poi => poi.Geometry == PoiGeometry.Point).ToList();
    }

    /// <summary>The buildings, as read.</summary>
    public Buildings Buildings { get; }

    /// <summary>The points of interest, as read.</summary>
    public Pois Pois { get; }

    /// <summary>
    /// Joins <paramref name="buildings"/> and <paramref name="pois"/> (from the same pack). Throws a
    /// <see cref="MapPackException"/> when their map ids differ, a building's <c>poi_ids</c> or <c>area_poi_ids</c>
    /// or a POI's <c>building_id</c> or <c>contains_building_ids</c> names a missing id or one that doesn't link
    /// back, an <c>area_poi_ids</c> entry is a point, a <c>building</c> address that differs from the building's, or
    /// two buildings share a glTF node name.
    /// </summary>
    public static PlaceIndex Create(Buildings buildings, Pois pois)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        ArgumentNullException.ThrowIfNull(pois);
        const string BuildingsSource = MapPack.BuildingsFile;
        const string PoisSource = MapPack.PoisFile;

        if (buildings.MapId != pois.MapId)
        {
            throw new MapPackException(
                $"{BuildingsSource} is for map '{buildings.MapId}', but {PoisSource} is for map '{pois.MapId}'");
        }

        var byId = new Dictionary<string, Building>(buildings.Items.Count, StringComparer.Ordinal);
        var byNode = new Dictionary<string, Building>(StringComparer.Ordinal);
        foreach (var building in buildings.Items)
        {
            if (!byId.TryAdd(building.Id, building))
            {
                throw new MapPackException($"{BuildingsSource}: building id '{building.Id}' appears more than once");
            }

            foreach (var node in building.CollisionNodes.Prepend(building.Node))
            {
                if (!byNode.TryAdd(node, building))
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: glTF node '{node}' is used by '{byNode[node].Id}' and '{building.Id}'");
                }
            }
        }

        var poisById = new Dictionary<string, Poi>(pois.Items.Count, StringComparer.Ordinal);
        foreach (var poi in pois.Items)
        {
            if (!poisById.TryAdd(poi.Id, poi))
            {
                throw new MapPackException($"{PoisSource}: POI id '{poi.Id}' appears more than once");
            }
        }

        foreach (var building in buildings.Items)
        {
            foreach (var poiId in building.PoiIds)
            {
                if (!poisById.TryGetValue(poiId, out var poi))
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: building '{building.Id}' lists POI '{poiId}' in poi_ids, which isn't in {PoisSource}");
                }

                if (poi.BuildingId != building.Id)
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: building '{building.Id}' lists POI '{poiId}' in poi_ids, "
                        + $"but its building_id is {Quote(poi.BuildingId)}");
                }
            }

            foreach (var poiId in building.AreaPoiIds)
            {
                if (!poisById.TryGetValue(poiId, out var poi))
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: building '{building.Id}' lists POI '{poiId}' in area_poi_ids, which isn't in {PoisSource}");
                }

                if (poi.Geometry != PoiGeometry.Area)
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: building '{building.Id}' lists POI '{poiId}' in area_poi_ids, but it is a point");
                }

                if (!poi.ContainsBuildingIds.Contains(building.Id, StringComparer.Ordinal))
                {
                    throw new MapPackException(
                        $"{BuildingsSource}: building '{building.Id}' lists POI '{poiId}' in area_poi_ids, "
                        + "but the POI's contains_building_ids doesn't list the building");
                }
            }
        }

        foreach (var poi in pois.Items)
        {
            if (poi.BuildingId is not null)
            {
                if (!byId.TryGetValue(poi.BuildingId, out var building))
                {
                    throw new MapPackException(
                        $"{PoisSource}: POI '{poi.Id}' has building_id '{poi.BuildingId}', which isn't in {BuildingsSource}");
                }

                if (!building.PoiIds.Contains(poi.Id, StringComparer.Ordinal))
                {
                    throw new MapPackException(
                        $"{PoisSource}: POI '{poi.Id}' has building_id '{poi.BuildingId}', "
                        + "but the building's poi_ids doesn't list the POI");
                }

                if (poi.AddressSource == AddressSource.Building && poi.Address != building.Address)
                {
                    throw new MapPackException(
                        $"{PoisSource}: POI '{poi.Id}' has address_source 'building', but its address differs from "
                        + $"building '{building.Id}'");
                }
            }

            foreach (var buildingId in poi.ContainsBuildingIds)
            {
                if (!byId.TryGetValue(buildingId, out var building))
                {
                    throw new MapPackException(
                        $"{PoisSource}: POI '{poi.Id}' lists building '{buildingId}' in contains_building_ids, "
                        + $"which isn't in {BuildingsSource}");
                }

                if (!building.AreaPoiIds.Contains(poi.Id, StringComparer.Ordinal))
                {
                    throw new MapPackException(
                        $"{PoisSource}: POI '{poi.Id}' lists building '{buildingId}' in contains_building_ids, "
                        + "but the building's area_poi_ids doesn't list the POI");
                }
            }
        }

        return new PlaceIndex(buildings, pois, byId, byNode, poisById);
    }

    /// <exception cref="KeyNotFoundException">No building has <paramref name="id"/>.</exception>
    public Building Building(string id) =>
        _buildings.TryGetValue(id, out var building) ? building : throw new KeyNotFoundException($"no building '{id}'");

    public bool TryGetBuilding(string id, out Building building) => _buildings.TryGetValue(id, out building!);

    /// <summary>The building whose visual node or one of whose collision nodes is named <paramref name="nodeName"/>.</summary>
    public bool TryGetBuildingByNode(string nodeName, out Building building) =>
        _buildingsByNode.TryGetValue(nodeName, out building!);

    /// <exception cref="KeyNotFoundException">No POI has <paramref name="id"/>.</exception>
    public Poi Poi(string id) =>
        _pois.TryGetValue(id, out var poi) ? poi : throw new KeyNotFoundException($"no point of interest '{id}'");

    public bool TryGetPoi(string id, out Poi poi) => _pois.TryGetValue(id, out poi!);

    /// <summary>The POIs on or in <paramref name="building"/> (its <c>poi_ids</c>), in that order.</summary>
    public IReadOnlyList<Poi> PoisOnOrIn(Building building)
    {
        ArgumentNullException.ThrowIfNull(building);
        return building.PoiIds.Select(Poi).ToList();
    }

    /// <summary>The POI areas <paramref name="building"/> is inside (its <c>area_poi_ids</c>), in that order.</summary>
    public IReadOnlyList<Poi> PoiAreasContaining(Building building)
    {
        ArgumentNullException.ThrowIfNull(building);
        return building.AreaPoiIds.Select(Poi).ToList();
    }

    /// <summary>
    /// Every POI area whose outline contains (<paramref name="x"/>, <paramref name="z"/>), holes excluded, smallest
    /// area first (equal areas by id).
    /// </summary>
    public IReadOnlyList<Poi> PoiAreasAt(double x, double z) => _areasBySize.Where(poi => poi.Contains(x, z)).ToList();

    /// <summary>The smallest POI area whose outline contains (<paramref name="x"/>, <paramref name="z"/>), or null.</summary>
    public Poi? PoiAreaAt(double x, double z) => _areasBySize.FirstOrDefault(poi => poi.Contains(x, z));

    /// <summary>
    /// The POI point (not area) nearest to <paramref name="position"/> in 3D, if it is at most
    /// <paramref name="radius"/> metres away; on a tie, the lower id (ordinal). Null when none is that close.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or NaN.</exception>
    public Poi? NearestPoiPoint(LocalPoint position, double radius)
    {
        if (double.IsNaN(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be 0 or more");
        }

        Poi? best = null;
        var bestDistance = radius * radius;
        foreach (var poi in _points)
        {
            var dx = poi.Position.X - position.X;
            var dy = poi.Position.Y - position.Y;
            var dz = poi.Position.Z - position.Z;
            var distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < bestDistance
                || (distance == bestDistance && (best is null || string.CompareOrdinal(poi.Id, best.Id) < 0)))
            {
                best = poi;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static string Quote(string? id) => id is null ? "null" : $"'{id}'";
}
