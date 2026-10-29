namespace Roadman.Core.Maps;

/// <summary>What a direct child of an imported tile scene is, for <see cref="BuildingBodies.Resolve"/>.</summary>
public enum TileChildKind
{
    /// <summary>A mesh node (building visual meshes are <c>bld_&lt;id&gt;</c>).</summary>
    Mesh,

    /// <summary>A collision body (from a <c>-colonly</c> glTF node).</summary>
    Body,

    /// <summary>Any other node.</summary>
    Other,
}

/// <summary>A direct child of an imported tile scene, in node order.</summary>
public readonly record struct TileChild(TileChildKind Kind, string Name);

/// <summary>
/// One collision body of a tile and the glTF collision node name it was resolved to.
/// </summary>
/// <param name="ChildIndex">Index of the body among the tile's children.</param>
/// <param name="OriginalName">The body's name as imported.</param>
/// <param name="CollisionNode">The resolved glTF node name (<c>bld_…-colonly</c>), or null when it couldn't be resolved.</param>
/// <param name="FromOrder">True when the name was inherited from the preceding visual mesh (the body was renamed by Godot).</param>
public readonly record struct ResolvedBody(int ChildIndex, string OriginalName, string? CollisionNode, bool FromOrder);

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
/// The importer turns <c>bld_&lt;id&gt;-colonly</c> into a body named <c>bld_&lt;id&gt;</c>, which clashes with the
/// visual mesh of the same name, so Godot renames the body (<c>@StaticBody3D@…</c>). The pipeline writes each body
/// right after its visual mesh, so such a body takes the name of the <c>bld_</c> mesh immediately before it. Multi-part
/// bodies (<c>bld_&lt;id&gt;_c&lt;k&gt;-colonly</c>) don't clash and keep their name.
/// </summary>
public static class BuildingBodies
{
    /// <summary>Prefix of the building visual mesh and collision node names (ADR 0006).</summary>
    public const string NodePrefix = "bld_";

    /// <summary>Godot's import suffix for collision-only nodes.</summary>
    public const string CollisionOnlySuffix = "-colonly";

    /// <summary>
    /// Resolves every <see cref="TileChildKind.Body"/> child, in order. A body named <c>bld_…</c> resolves to its name
    /// plus <see cref="CollisionOnlySuffix"/>. Any other body inherits the name of the <c>bld_</c> mesh immediately
    /// before it; any body and any non-building node in between clear that, so a mesh gives its name to one body at
    /// most, and never across a multi-part building's bodies. When <paramref name="collisionNodes"/> is given, a
    /// resolved name must be one of them, else the body is unresolved.
    /// </summary>
    public static IReadOnlyList<ResolvedBody> Resolve(
        IReadOnlyList<TileChild> children, IReadOnlySet<string>? collisionNodes)
    {
        var bodies = new List<ResolvedBody>();
        string? previousMesh = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Kind != TileChildKind.Body)
            {
                previousMesh = child.Kind == TileChildKind.Mesh && IsBuildingName(child.Name) ? child.Name : null;
                continue;
            }

            string? stem;
            bool fromOrder;
            if (IsBuildingName(child.Name))
            {
                stem = child.Name;
                fromOrder = false;
            }
            else
            {
                stem = previousMesh;
                fromOrder = true;
            }

            previousMesh = null;
            var node = stem is null ? null : stem + CollisionOnlySuffix;
            if (node is not null && collisionNodes is not null && !collisionNodes.Contains(node))
            {
                node = null;
            }

            bodies.Add(new ResolvedBody(i, child.Name, node, fromOrder));
        }

        return bodies;
    }

    /// <summary>
    /// Checks that <paramref name="bodies"/> (one tile's, from <see cref="Resolve"/>) and <paramref name="expected"/>
    /// (the collision nodes of the buildings whose <c>tile</c> is <paramref name="tile"/>) are a bijection. On a
    /// match every body is accepted. Otherwise unresolved, extra and duplicate bodies are rejected, and so is every
    /// body named from node order, since the order can't be trusted then; only importer-named bodies with a valid,
    /// unique name of this tile are kept.
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
        var bijection = unresolved.Count == 0 && extra.Count == 0 && duplicates.Count == 0 && missing.Count == 0;

        var accepted = bodies
            .Select(body => bijection || (body.CollisionNode is { } node && !body.FromOrder
                                          && expectedSet.Contains(node) && counts[node] == 1))
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

    private static bool IsBuildingName(string name) => name.StartsWith(NodePrefix, StringComparison.Ordinal);
}
