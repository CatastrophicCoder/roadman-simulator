using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Scenes.Edge;

/// <summary>
/// The world-edge barriers (<c>docs/specs/world-edge-game.md</c>, "Barriers") at <c>MapLoader/Edge/Barriers</c>: one
/// <see cref="BarrierItem"/> (<see cref="Roadblock"/>, <see cref="LandFence"/> or <see cref="WaterFence"/>) per
/// <c>boundary.json</c> roadblock and fence, from <see cref="BarrierLayout.Build"/>, and the visuals batched in one
/// <see cref="MultiMeshInstance3D"/> per barrier type and piece kind for the whole map (under <c>Batches</c>).
/// <para>
/// <see cref="MapLoader"/> creates it after it reads the boundary and calls <see cref="BuildWhenCollisionReadyAsync"/>:
/// the roadblock stands stand on <see cref="MapLoader.GroundOrRoadHitAt"/>, so the build waits until the terrain
/// collision is queryable. A map reload frees this node and makes a new one.
/// </para>
/// <para>
/// Nothing breaks yet (vehicles come in Part B); <see cref="RestoreAll"/> already rebuilds every item marked broken and
/// frees the loose pieces under <c>LoosePieces</c>, and runs on every respawn.
/// </para>
/// </summary>
public partial class Barriers : Node3D
{
    /// <summary>Folder of the placeholder barrier materials, until the art-direction palette replaces them.</summary>
    public const string MaterialsPath = "res://materials/placeholder";

    private readonly List<BarrierItem> _items = [];
    private readonly Dictionary<string, BarrierItem> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(MultiMeshInstance3D Batch, int Index)>> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<(BarrierKind Kind, PieceKind Piece), MultiMeshInstance3D> _batches = [];
    private readonly Dictionary<(MultiMeshInstance3D Batch, int Index), Transform3D> _restTransforms = [];
    private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
    private IReadOnlyList<BarrierItemLayout> _layouts = [];

    /// <summary>Emitted once the items are built, with their number.</summary>
    [Signal]
    public delegate void BuiltEventHandler(int itemCount);

    /// <summary>
    /// The tuned barrier sizes. <see cref="MapLoader"/> sets its <see cref="MapLoader.BarrierSettings"/> here before the
    /// build, so they last across reloads; read at each <see cref="Build"/>.
    /// </summary>
    [Export]
    public BarrierSettings Settings { get; set; } = new();

    /// <summary>Whether the items are built.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>The number of items built.</summary>
    public int ItemCount => _items.Count;

    /// <summary>The item bodies, in ring order.</summary>
    public IReadOnlyList<BarrierItem> Items => _items;

    /// <summary>The layouts the items were built from, in ring order.</summary>
    public IReadOnlyList<BarrierItemLayout> Layouts => _layouts;

    /// <summary>The number of visual pieces (batched instances) over all items.</summary>
    public int PieceCount { get; private set; }

    /// <summary>The batches, one per barrier type and piece kind that occurs.</summary>
    public IReadOnlyDictionary<(BarrierKind Kind, PieceKind Piece), MultiMeshInstance3D> Batches => _batches;

    /// <summary>Parent of the batches.</summary>
    public Node3D BatchRoot { get; private set; } = null!;

    /// <summary>Parent of the loose pieces of broken items (empty until vehicles can break barriers).</summary>
    public Node3D LoosePieces { get; private set; } = null!;

    /// <summary>The ids of the items that are broken now.</summary>
    public IReadOnlyCollection<string> BrokenItemIds => _broken;

    public override void _Ready() => EnsureChildren();

    /// <summary>The item with <paramref name="itemId"/> (its <c>boundary.json</c> id), or null.</summary>
    public BarrierItem? ItemById(string itemId) => _itemsById.GetValueOrDefault(itemId);

    /// <summary>The batch instances that draw item <paramref name="itemId"/>'s pieces (empty for an unknown id).</summary>
    public IReadOnlyList<(MultiMeshInstance3D Batch, int Index)> InstancesOf(string itemId) =>
        _instances.TryGetValue(itemId, out var list) ? list : [];

    /// <summary>The layout parameters from <see cref="Settings"/>.</summary>
    /// <exception cref="ArgumentException">A setting is out of its range (<see cref="BarrierParameters"/>).</exception>
    public BarrierParameters Parameters() => (Settings ?? new BarrierSettings()).ToParameters();

    /// <summary>
    /// Waits until <paramref name="loader"/>'s terrain collision is queryable (up to
    /// <paramref name="maxPhysicsFrames"/> physics frames), then builds the items of <paramref name="boundary"/> with
    /// the roadblock stands on <see cref="MapLoader.GroundOrRoadHitAt"/>. Does nothing when this node left the tree
    /// meanwhile (a map reload). A timeout, or a <see cref="Settings"/> value out of its range, pushes an error and
    /// builds nothing.
    /// </summary>
    public async void BuildWhenCollisionReadyAsync(MapLoader loader, Boundary boundary, int maxPhysicsFrames)
    {
        var ready = await loader.WaitForTerrainCollisionAsync(maxPhysicsFrames);
        if (!IsInstanceValid(this) || !IsInsideTree() || IsQueuedForDeletion() || !IsInstanceValid(loader) || loader.Boundary != boundary)
        {
            return;
        }

        if (!ready)
        {
            GD.PushError(FormattableString.Invariant(
                $"Barriers: no terrain collision was queryable after {maxPhysicsFrames} physics frames, so no barriers were built from {loader.BoundaryPath}; {MapPack.RegenHint(loader.ResolvedMapId)}"));
            return;
        }

        try
        {
            Build(boundary, (x, z) => loader.GroundOrRoadHitAt((float)x, (float)z)?.Position.Y);
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"Barriers: a barrier setting is out of range ({ex.Message}), so no barriers were built from {loader.BoundaryPath}; fix MapLoader.BarrierSettings");
        }
    }

    /// <summary>
    /// Builds the items and batches of <paramref name="boundary"/> now (replacing any built before), with
    /// <paramref name="groundAt"/> under the roadblock stands (<see cref="BarrierLayout.StandGround"/>: right under the
    /// stand, else <see cref="BarrierSettings.RoadblockStandGroundInset"/> inward), and emits
    /// <see cref="SignalName.Built"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A <see cref="Settings"/> value is out of its range; nothing is changed.</exception>
    public void Build(Boundary boundary, Func<double, double, double?> groundAt)
    {
        var parameters = Parameters();
        EnsureChildren();
        Clear();
        _layouts = BarrierLayout.Build(boundary, parameters, groundAt);

        var transforms = new Dictionary<(BarrierKind Kind, PieceKind Piece), List<(string ItemId, Transform3D Transform)>>();
        foreach (var layout in _layouts)
        {
            var item = BarrierItem.Create(layout);
            AddChild(item);
            _items.Add(item);
            _itemsById.TryAdd(layout.Id, item);
            foreach (var piece in layout.Pieces)
            {
                var key = (layout.Kind, piece.Kind);
                if (!transforms.TryGetValue(key, out var list))
                {
                    list = [];
                    transforms[key] = list;
                }

                list.Add((layout.Id, ToTransform(piece.Box)));
            }
        }

        foreach (var ((kind, pieceKind), list) in transforms.OrderBy(entry => entry.Key.Kind).ThenBy(entry => entry.Key.Piece))
        {
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = MeshFor(pieceKind),
                InstanceCount = list.Count,
            };
            var batch = new MultiMeshInstance3D { Name = $"{kind}_{pieceKind}", Multimesh = multimesh };
            for (var i = 0; i < list.Count; i++)
            {
                multimesh.SetInstanceTransform(i, list[i].Transform);
                _restTransforms[(batch, i)] = list[i].Transform;
                if (!_instances.TryGetValue(list[i].ItemId, out var instances))
                {
                    instances = [];
                    _instances[list[i].ItemId] = instances;
                }

                instances.Add((batch, i));
            }

            BatchRoot.AddChild(batch);
            _batches[(kind, pieceKind)] = batch;
            PieceCount += list.Count;
        }

        IsBuilt = true;
        GD.Print(
            $"Barriers: built {_items.Count} items ({Count(BarrierKind.Roadblock)} roadblocks, {Count(BarrierKind.LandFence)} land fences, {Count(BarrierKind.WaterFence)} water fences), {PieceCount} pieces in {_batches.Count} batches");
        EmitSignal(SignalName.Built, _items.Count);
    }

    /// <summary>
    /// Rebuilds every broken item as it was at load (its static body back and its instances visible) and frees every
    /// loose piece. <c>World</c> calls it on each death respawn; cheap when nothing is broken. A map reload doesn't call
    /// it: it frees this node and builds a new one.
    /// </summary>
    public void RestoreAll()
    {
        if (LoosePieces is not null && IsInstanceValid(LoosePieces))
        {
            foreach (var piece in LoosePieces.GetChildren())
            {
                LoosePieces.RemoveChild(piece);
                piece.QueueFree();
            }
        }

        foreach (var itemId in _broken)
        {
            if (_itemsById.TryGetValue(itemId, out var item) && IsInstanceValid(item) && item.GetParent() is null)
            {
                AddChild(item);
            }

            foreach (var (batch, index) in InstancesOf(itemId))
            {
                batch.Multimesh.SetInstanceTransform(index, _restTransforms[(batch, index)]);
            }
        }

        _broken.Clear();
    }

    /// <summary>
    /// Removes item <paramref name="itemId"/>'s static body from the tree and hides its batch instances (the first half
    /// of a break; Part B adds the loose pieces). Returns false for an unknown or already broken item.
    /// </summary>
    public bool MarkBroken(string itemId)
    {
        if (!_itemsById.TryGetValue(itemId, out var item) || !_broken.Add(itemId))
        {
            return false;
        }

        if (IsInstanceValid(item) && item.GetParent() == this)
        {
            RemoveChild(item);
        }

        foreach (var (batch, index) in InstancesOf(itemId))
        {
            batch.Multimesh.SetInstanceTransform(index, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero));
        }

        return true;
    }

    public override void _Notification(int what)
    {
        // Items removed from the tree by a break aren't freed with this node; free them here.
        if (what == NotificationPredelete)
        {
            foreach (var item in _items)
            {
                if (IsInstanceValid(item) && item.GetParent() is null)
                {
                    item.Free();
                }
            }
        }
    }

    /// <summary>The batch transform of a unit-cube mesh for <paramref name="box"/> (columns = the box's axes).</summary>
    public static Transform3D ToTransform(BoxTransform box) =>
        new(
            new Basis(BarrierItem.ToVector(box.AxisX), BarrierItem.ToVector(box.AxisY), BarrierItem.ToVector(box.AxisZ)),
            BarrierItem.ToVector(box.Origin));

    private int Count(BarrierKind kind) => _items.Count(item => item.Kind == kind);

    private void Clear()
    {
        foreach (var item in _items)
        {
            if (IsInstanceValid(item))
            {
                item.GetParent()?.RemoveChild(item);
                item.QueueFree();
            }
        }

        foreach (var batch in _batches.Values)
        {
            batch.GetParent()?.RemoveChild(batch);
            batch.QueueFree();
        }

        foreach (var piece in LoosePieces.GetChildren())
        {
            LoosePieces.RemoveChild(piece);
            piece.QueueFree();
        }

        _items.Clear();
        _itemsById.Clear();
        _instances.Clear();
        _batches.Clear();
        _restTransforms.Clear();
        _broken.Clear();
        _layouts = [];
        PieceCount = 0;
        IsBuilt = false;
    }

    private void EnsureChildren()
    {
        if (BatchRoot is null || !IsInstanceValid(BatchRoot))
        {
            BatchRoot = new Node3D { Name = "Batches" };
            AddChild(BatchRoot);
        }

        if (LoosePieces is null || !IsInstanceValid(LoosePieces))
        {
            LoosePieces = new Node3D { Name = "LoosePieces" };
            AddChild(LoosePieces);
        }
    }

    /// <summary>The unit mesh (fills [−0.5, 0.5]³) and placeholder material of a piece kind; low-poly.</summary>
    private static Mesh MeshFor(PieceKind piece)
    {
        var material = GD.Load<Material>($"{MaterialsPath}/{MaterialFile(piece)}");
        return piece switch
        {
            PieceKind.Post => new CylinderMesh
            {
                TopRadius = 0.5f,
                BottomRadius = 0.5f,
                Height = 1f,
                RadialSegments = 6,
                Rings = 0,
                Material = material,
            },
            PieceKind.Float => new SphereMesh
            {
                Radius = 0.5f,
                Height = 1f,
                RadialSegments = 8,
                Rings = 4,
                Material = material,
            },
            _ => new BoxMesh { Size = Vector3.One, Material = material },
        };
    }

    private static string MaterialFile(PieceKind piece) => piece switch
    {
        PieceKind.Post => "fence_post.tres",
        PieceKind.Strand => "fence_wire.tres",
        PieceKind.Stand => "fence_wire.tres",
        PieceKind.Float => "fence_float.tres",
        PieceKind.FloatLine => "fence_line.tres",
        PieceKind.BoardRed => "barrier_red.tres",
        _ => "barrier_white.tres",
    };
}
