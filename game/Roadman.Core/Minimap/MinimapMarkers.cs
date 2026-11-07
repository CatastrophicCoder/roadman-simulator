namespace Roadman.Core.Minimap;

/// <summary>The kind of a minimap marker (<c>docs/specs/minimap.md</c>, "Markers"); drawn as a generic shape.</summary>
public enum MinimapMarkerKind
{
    Generic,
}

/// <summary>
/// A marker on the minimap: an id, a ground position and a kind. The quest system will add them, resolving marker ids
/// from the pack to positions (missions never use raw coordinates).
/// </summary>
public sealed record MinimapMarker
{
    private readonly string _id = "";
    private readonly double _x;
    private readonly double _z;

    /// <param name="id">Non-blank.</param>
    /// <param name="x">Godot local x, finite.</param>
    /// <param name="z">Godot local z, finite.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="clampToRim">Whether it sits on the rim when outside the circle (else it isn't drawn then).</param>
    /// <exception cref="ArgumentException">A blank id or a non-finite position.</exception>
    public MinimapMarker(string id, double x, double z, MinimapMarkerKind kind = MinimapMarkerKind.Generic, bool clampToRim = false)
    {
        Id = id;
        X = x;
        Z = z;
        Kind = kind;
        ClampToRim = clampToRim;
    }

    /// <summary>Non-blank; unique within a <see cref="MinimapMarkerSet"/>.</summary>
    public string Id
    {
        get => _id;
        init => _id = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"a minimap marker id must not be blank, got '{value}'", nameof(Id))
            : value;
    }

    public double X
    {
        get => _x;
        init => _x = double.IsFinite(value) ? value : throw new ArgumentException($"marker x must be finite, got {value}", nameof(X));
    }

    public double Z
    {
        get => _z;
        init => _z = double.IsFinite(value) ? value : throw new ArgumentException($"marker z must be finite, got {value}", nameof(Z));
    }

    public MinimapMarkerKind Kind { get; init; }

    /// <summary>Sits on the rim when outside the circle.</summary>
    public bool ClampToRim { get; init; }
}

/// <summary>
/// The minimap's markers (<c>docs/specs/minimap.md</c>, "Markers"), keyed by id. <see cref="Changed"/> is raised once
/// per change and never for a no-op.
/// </summary>
public sealed class MinimapMarkerSet
{
    private readonly SortedDictionary<string, MinimapMarker> _markers = new(StringComparer.Ordinal);
    private IReadOnlyList<MinimapMarker>? _all;

    /// <summary>Raised once after every change.</summary>
    public event Action? Changed;

    /// <summary>Every marker, ordered by id (ordinal).</summary>
    public IReadOnlyList<MinimapMarker> All => _all ??= [.. _markers.Values];

    public int Count => _markers.Count;

    /// <summary>Adds <paramref name="marker"/>.</summary>
    /// <exception cref="ArgumentException">Its id is already in the set (the message names it); nothing changes.</exception>
    public void Add(MinimapMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (!_markers.TryAdd(marker.Id, marker))
        {
            throw new ArgumentException($"a minimap marker with id '{marker.Id}' is already in the set", nameof(marker));
        }

        OnChanged();
    }

    /// <summary>Removes the marker with <paramref name="id"/>; false (and no <see cref="Changed"/>) when there is none.</summary>
    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_markers.Remove(id))
        {
            return false;
        }

        OnChanged();
        return true;
    }

    /// <summary>Removes every marker; raises <see cref="Changed"/> only when there was one.</summary>
    public void Clear()
    {
        if (_markers.Count == 0)
        {
            return;
        }

        _markers.Clear();
        OnChanged();
    }

    public bool Contains(string id) => _markers.ContainsKey(id);

    public bool TryGet(string id, out MinimapMarker marker) => _markers.TryGetValue(id, out marker!);

    private void OnChanged()
    {
        _all = null;
        Changed?.Invoke();
    }
}
