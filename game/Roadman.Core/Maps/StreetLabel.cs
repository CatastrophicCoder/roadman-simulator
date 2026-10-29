namespace Roadman.Core.Maps;

/// <summary>
/// The HUD street label text for a road graph edge (<c>docs/specs/place-information.md</c>, "Roadman.Core"). Finnish
/// names only; <c>name_sv</c> is never shown.
/// </summary>
public static class StreetLabel
{
    private static readonly Dictionary<string, string> ClassTexts = new(StringComparer.Ordinal)
    {
        ["primary"] = "Main road",
        ["secondary"] = "Main road",
        ["tertiary"] = "Road",
        ["unclassified"] = "Road",
        ["residential"] = "Residential street",
        ["living_street"] = "Living street",
        ["service"] = "Service road",
        ["driveway"] = "Driveway",
        ["parking_aisle"] = "Parking aisle",
        ["track"] = "Track",
        ["cycleway"] = "Cycle path",
        ["footway"] = "Footpath",
        ["path"] = "Path",
        ["pedestrian"] = "Pedestrian street",
    };

    /// <summary>
    /// <c>name (ref)</c> when the edge has both (<c>Jokioistentie (52)</c>), the name or the ref alone when it has
    /// one of them, and otherwise the class's display text: a fixed text for the listed classes, or the class value
    /// with <c>_</c> replaced by spaces and the first letter capitalised.
    /// </summary>
    public static string Format(RoadEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var name = edge.Names.Name;
        var reference = edge.Names.Ref;
        if (name is not null)
        {
            return reference is null ? name : $"{name} ({reference})";
        }

        if (reference is not null)
        {
            return reference;
        }

        return ClassText(edge.Class);
    }

    /// <summary>The display text of a road class, used when an edge has neither name nor ref.</summary>
    public static string ClassText(string roadClass)
    {
        ArgumentNullException.ThrowIfNull(roadClass);
        if (ClassTexts.TryGetValue(roadClass, out var text))
        {
            return text;
        }

        var spaced = roadClass.Replace('_', ' ');
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
