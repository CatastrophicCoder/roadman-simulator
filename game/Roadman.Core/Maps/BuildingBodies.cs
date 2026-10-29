namespace Roadman.Core.Maps;

/// <summary>
/// One building collision body of a tile and the glTF collision node name it was resolved to.
/// </summary>
/// <param name="Index">Index of the body in the list given to <see cref="BuildingBodies.Resolve"/>.</param>
/// <param name="OriginalName">The body's name as imported.</param>
/// <param name="CollisionNode">The resolved glTF node name (<c>bld_…_c&lt;k&gt;-colonly</c>), or null when it couldn't be resolved.</param>
public readonly record struct ResolvedBody(int Index, string OriginalName, string? CollisionNode);

/// <summary>
/// Result of <see cref="BuildingBodies.Check"/> for one tile: whether the resolved bodies and the tile's collision
/// nodes in <c>buildings.json</c> are a bijection, and which bodies may be used.
/// </summary>
/// <param name="Tile">The tile id.</param>
/// <param name="Accepted">Per body (same order as the input), true when the body may be put in the building group.</param>
/// <param name="Unresolved">Original names of bodies with no (valid) collision node name.</param>
/// <param name="Missing">Collision nodes of the tile's buildings that no body resolved to.</param>
/// <param name="Extra">Resolved names that aren't collision nodes of a building in this tile.</param>
/// <param name="Duplicates">Resolved names that more than one body resolved to.</param>
public sealed record TileBodyCheck(
    string Tile,
    IReadOnlyList<bool> Accepted,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<string> Duplicates)
{
    /// <summary>True when every collision node has exactly one body and every body exactly one collision node.</summary>
    public bool IsBijection => Unresolved.Count == 0 && Missing.Count == 0 && Extra.Count == 0 && Duplicates.Count == 0;

    /// <summary>A one-line description of the mismatch, with at most <paramref name="maxNames"/> names per kind.</summary>
    public string Describe(int maxNames = 5)
    {
        var parts = new List<string>();
        Add("unresolved bodies", Unresolved);
        Add("collision nodes without a body", Missing);
        Add("bodies not in this tile's buildings", Extra);
        Add("collision nodes with several bodies", Duplicates);
        return parts.Count == 0
            ? $"tile {Tile}: building bodies match buildings.json"
            : $"tile {Tile}: building bodies don't match buildings.json: {string.Join("; ", parts)}";

        void Add(string label, IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            var shown = string.Join(", ", names.Take(maxNames));
            var more = names.Count > maxNames ? $", … ({names.Count - maxNames} more)" : "";
            parts.Add($"{names.Count} {label} ({shown}{more})");
        }
    }
}

/// <summary>
/// Names building collision bodies after their glTF nodes and checks the result against <c>buildings.json</c>.
/// The pipeline names every building collision node <c>bld_&lt;id&gt;_c&lt;k&gt;-colonly</c> (ADR 0006), which never
/// equals a visual mesh name <c>bld_&lt;id&gt;</c> once the suffix is stripped, so Godot's import keeps each body's
/// name and only strips the suffix: the body is <c>bld_&lt;id&gt;_c&lt;k&gt;</c>. A body is resolved from its own name
/// alone, by re-appending <see cref="CollisionOnlySuffix"/>; node order is never used.
/// </summary>
public static class BuildingBodies
{
    /// <summary>Prefix of the building visual mesh and collision node names (ADR 0006).</summary>
    public const string NodePrefix = "bld_";

    /// <summary>Godot's import suffix for collision-only nodes.</summary>
    public const string CollisionOnlySuffix = "-colonly";

    /// <summary>
    /// The glTF collision node name of a body named <paramref name="bodyName"/> by the importer: the name plus
    /// <see cref="CollisionOnlySuffix"/> when it starts with <see cref="NodePrefix"/> and doesn't already end with the
    /// suffix (the importer strips it), else null. A body Godot renamed (<c>@StaticBody3D@…</c>) gives null.
    /// </summary>
    public static string? CollisionNodeOf(string bodyName) =>
        bodyName.StartsWith(NodePrefix, StringComparison.Ordinal)
        && !bodyName.EndsWith(CollisionOnlySuffix, StringComparison.Ordinal)
            ? bodyName + CollisionOnlySuffix
            : null;

    /// <summary>
    /// Resolves each body of <paramref name="bodyNames"/> (one tile's building collision bodies, as imported) with
    /// <see cref="CollisionNodeOf"/>. When <paramref name="collisionNodes"/> is given, a resolved name must be one of
    /// them, else the body is unresolved.
    /// </summary>
    public static IReadOnlyList<ResolvedBody> Resolve(IReadOnlyList<string> bodyNames, IReadOnlySet<string>? collisionNodes)
    {
        var bodies = new List<ResolvedBody>(bodyNames.Count);
        for (var i = 0; i < bodyNames.Count; i++)
        {
            var node = CollisionNodeOf(bodyNames[i]);
            if (node is not null && collisionNodes is not null && !collisionNodes.Contains(node))
            {
                node = null;
            }

            bodies.Add(new ResolvedBody(i, bodyNames[i], node));
        }

        return bodies;
    }

    /// <summary>
    /// Checks that <paramref name="bodies"/> (one tile's, from <see cref="Resolve"/>) and <paramref name="expected"/>
    /// (the collision nodes of the buildings whose <c>tile</c> is <paramref name="tile"/>) are a bijection. A body is
    /// accepted when its resolved name is one of <paramref name="expected"/> and no other body resolved to it, so on a
    /// match every body is accepted, and on a mismatch the unresolved, extra and duplicate bodies are rejected.
    /// </summary>
    public static TileBodyCheck Check(string tile, IReadOnlyList<ResolvedBody> bodies, IEnumerable<string> expected)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            if (body.CollisionNode is { } node)
            {
                counts[node] = counts.GetValueOrDefault(node) + 1;
            }
        }

        var unresolved = bodies.Where(body => body.CollisionNode is null).Select(body => body.OriginalName).ToList();
        var extra = counts.Keys.Where(node => !expectedSet.Contains(node)).Order(StringComparer.Ordinal).ToList();
        var duplicates = counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList();
        var missing = expectedSet.Where(node => !counts.ContainsKey(node)).Order(StringComparer.Ordinal).ToList();

        var accepted = bodies
            .Select(body => body.CollisionNode is { } node && expectedSet.Contains(node) && counts[node] == 1)
            .ToList();
        return new TileBodyCheck(tile, accepted, unresolved, missing, extra, duplicates);
    }

    /// <summary>The collision node names of <paramref name="buildings"/>, grouped by their <c>tile</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CollisionNodesByTile(Buildings buildings)
    {
        var byTile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var building in buildings.Items)
        {
            if (!byTile.TryGetValue(building.Tile, out var nodes))
            {
                nodes = [];
                byTile[building.Tile] = nodes;
            }

            nodes.AddRange(building.CollisionNodes);
        }

        return byTile.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }
}
