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
/// Breaking (spec "Breaking"; only vehicles): each physics frame, before the physics step, every breaker (a
/// <see cref="PhysicsBody3D"/> implementing <see cref="IBarrierBreaker"/> in the group <see cref="BreakerGroup"/>) at
/// <see cref="BreakParameters.BreakSpeed"/> or faster sweeps its shapes along its motion over the step
/// (<see cref="SweepBreaker"/>), and every item it touches breaks (<see cref="Break"/>): the static body goes, the batch
/// instances are hidden, and one loose <see cref="RigidBody3D"/> per piece takes over under <c>LoosePieces</c> with the
/// breaker's velocity. <see cref="SignalName.ItemBroken"/> then fires; <c>World</c> hands the pieces to its safety net.
/// <see cref="RestoreAll"/> rebuilds every broken item and frees the loose pieces; <c>World</c> calls it on every
/// respawn. With no breaker in the tree the per-frame check costs one group lookup.
/// </para>
/// </summary>
public partial class Barriers : Node3D
{
    /// <summary>Folder of the placeholder barrier materials, until the art-direction palette replaces them.</summary>
    public const string MaterialsPath = "res://materials/placeholder";

    /// <summary>The group of the bodies that break barriers (<see cref="IBarrierBreaker"/>).</summary>
    public const string BreakerGroup = "barrier_breakers";

    /// <summary>
    /// The collision layers a loose piece collides with: the map, both barrier layers (unbroken items and other pieces),
    /// the player and vehicles.
    /// </summary>
    public const uint LoosePieceMask = CollisionLayers.Map | CollisionLayers.Barriers | CollisionLayers.Player | CollisionLayers.Vehicle;

    /// <summary>The smallest edge of a loose piece's collision shape, m (strands are 2 cm thick).</summary>
    public const float MinLooseShapeSize = 0.01f;

    private const int MaxSweepHits = 32;

    private readonly List<BarrierItem> _items = [];
    private readonly Dictionary<string, BarrierItem> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(MultiMeshInstance3D Batch, int Index)>> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<(BarrierKind Kind, PieceKind Piece), MultiMeshInstance3D> _batches = [];
    private readonly Dictionary<(MultiMeshInstance3D Batch, int Index), Transform3D> _restTransforms = [];
    private readonly Dictionary<(MultiMeshInstance3D Batch, int Index), Transform3D> _currentTransforms = [];
    private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RigidBody3D>> _loosePieces = new(StringComparer.Ordinal);
    private string? _lastBreakSettingsError;
    private IReadOnlyList<BarrierItemLayout> _layouts = [];

    /// <summary>Emitted once the items are built, with their number.</summary>
    [Signal]
    public delegate void BuiltEventHandler(int itemCount);

    /// <summary>
    /// Item <paramref name="itemId"/> broke into <paramref name="pieceCount"/> loose pieces
    /// (<see cref="LoosePiecesOf"/>), which are already under <see cref="LoosePieces"/>.
    /// </summary>
    [Signal]
    public delegate void ItemBrokenEventHandler(string itemId, int pieceCount);

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

    /// <summary>
    /// The number of loose pieces of all broken items that still exist: valid and not queued for deletion (as
    /// <see cref="LoosePiecesOf"/>); 0 after <see cref="RestoreAll"/> or a reload.
    /// </summary>
    public int LoosePieceCount => _loosePieces.Values.Sum(list => list.Count(IsLivePiece));

    public override void _Ready() => EnsureChildren();

    /// <summary>
    /// Before the physics step: each breaker at <see cref="BreakParameters.BreakSpeed"/> or faster breaks every item its
    /// sweep touches. Returns at once when no node is in <see cref="BreakerGroup"/>.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!IsBuilt || !GetTree().HasGroup(BreakerGroup))
        {
            return;
        }

        BreakTouchedItems(delta);
    }

    /// <summary>
    /// One breaking pass (what <see cref="_PhysicsProcess"/> does): for every <see cref="PhysicsBody3D"/> in
    /// <see cref="BreakerGroup"/> that implements <see cref="IBarrierBreaker"/>, is inside the tree and moves at
    /// <see cref="BreakParameters.BreakSpeed"/> or faster, breaks every item of this node that
    /// <see cref="SweepBreaker"/> finds over <paramref name="delta"/> seconds. A breaking setting out of range pushes one
    /// error (until it changes) and breaks nothing.
    /// </summary>
    /// <returns>The ids of the items broken, in the order they broke.</returns>
    public IReadOnlyList<string> BreakTouchedItems(double delta)
    {
        if (!IsBuilt || !IsInsideTree())
        {
            return [];
        }

        BreakParameters parameters;
        try
        {
            parameters = (Settings ?? new BarrierSettings()).ToBreakParameters();
            _lastBreakSettingsError = null;
        }
        catch (ArgumentException ex)
        {
            if (_lastBreakSettingsError != ex.Message)
            {
                _lastBreakSettingsError = ex.Message;
                GD.PushError($"Barriers: a breaking setting is out of range ({ex.Message}), so nothing breaks; fix MapLoader.BarrierSettings");
            }

            return [];
        }

        List<string>? broken = null;
        foreach (var node in GetTree().GetNodesInGroup(BreakerGroup))
        {
            if (node is not PhysicsBody3D body || node is not IBarrierBreaker breaker || !IsInstanceValid(body) || !body.IsInsideTree())
            {
                continue;
            }

            var velocity = breaker.BreakerVelocity;
            if (!parameters.Breaks(velocity.Length()))
            {
                continue;
            }

            foreach (var itemId in SweepBreaker(body, velocity, delta))
            {
                if (Break(itemId, velocity))
                {
                    (broken ??= []).Add(itemId);
                }
            }
        }

        return broken is null ? [] : broken;
    }

    /// <summary>
    /// The ids of this node's unbroken items that <paramref name="body"/>'s enabled <see cref="CollisionShape3D"/>
    /// children touch while moving by <paramref name="velocity"/> × <paramref name="delta"/>: each shape (with
    /// <see cref="BarrierSettings.BreakingSweepMargin"/> (the default when a breaking setting is out of range), against <see cref="CollisionLayers.BarrierStatic"/>, the body itself excluded) is tested
    /// where it is, where the cast along the motion first hits, and at the end of the motion. Sorted by id, no repeats.
    /// Doesn't look at the speed.
    /// </summary>
    public IReadOnlyList<string> SweepBreaker(PhysicsBody3D body, Vector3 velocity, double delta)
    {
        if (!IsBuilt || !IsInsideTree() || !IsInstanceValid(body) || !body.IsInsideTree())
        {
            return [];
        }

        var space = GetWorld3D().DirectSpaceState;
        var motion = velocity * (float)delta;
        float margin;
        try
        {
            margin = (float)(Settings ?? new BarrierSettings()).ToBreakParameters().SweepMargin;
        }
        catch (ArgumentException)
        {
            margin = (float)new BreakParameters().SweepMargin;
        }

        var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var child in body.GetChildren())
        {
            if (child is not CollisionShape3D { Disabled: false, Shape: { } shape } collisionShape)
            {
                continue;
            }

            var start = collisionShape.GlobalTransform;
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = shape,
                Transform = start,
                Motion = motion,
                Margin = margin,
                CollisionMask = CollisionLayers.BarrierStatic,
                CollideWithBodies = true,
                CollideWithAreas = false,
                Exclude = exclude,
            };
            CollectItems(space.IntersectShape(query, MaxSweepHits), hits);
            if (motion.LengthSquared() <= 0f)
            {
                continue;
            }

            var fractions = space.CastMotion(query);
            if (fractions.Length == 2 && fractions[1] < 1f)
            {
                query.Transform = start.Translated(motion * fractions[1]);
                CollectItems(space.IntersectShape(query, MaxSweepHits), hits);
            }

            query.Transform = start.Translated(motion);
            CollectItems(space.IntersectShape(query, MaxSweepHits), hits);
        }

        return [.. hits];
    }

    /// <summary>
    /// Breaks item <paramref name="itemId"/>: <see cref="MarkBroken"/>, then one loose <see cref="RigidBody3D"/> per
    /// piece of its layout under <see cref="LoosePieces"/> (named <c>&lt;item node name&gt;_&lt;i&gt;</c>), at the piece's
    /// rest place, with the piece's batch mesh, a collision shape sized from the piece (a box, a cylinder for posts, a
    /// sphere for round floats), on <see cref="CollisionLayers.Barrier"/> with mask <see cref="LoosePieceMask"/>, a mass
    /// from <see cref="BreakParameters.PieceMass"/> and <paramref name="velocity"/> as its linear velocity. Emits
    /// <see cref="SignalName.ItemBroken"/>. A breaking setting out of range uses the defaults for the masses.
    /// </summary>
    /// <returns>False for an unknown or already broken item (nothing changes).</returns>
    public bool Break(string itemId, Vector3 velocity)
    {
        if (!_itemsById.TryGetValue(itemId, out var item) || item.Layout is not { } layout || !MarkBroken(itemId))
        {
            return false;
        }

        BreakParameters parameters;
        try
        {
            parameters = (Settings ?? new BarrierSettings()).ToBreakParameters();
        }
        catch (ArgumentException)
        {
            parameters = new BreakParameters();
        }

        EnsureChildren();
        var toPieces = LoosePieces.Transform.AffineInverse() * BatchRoot.Transform;
        var pieces = new List<RigidBody3D>(layout.Pieces.Count);
        var name = item.Name.ToString();
        for (var i = 0; i < layout.Pieces.Count; i++)
        {
            var piece = layout.Pieces[i];
            if (CreateLoosePiece($"{name}_{i}", layout.Kind, piece, toPieces, parameters, velocity) is { } body)
            {
                LoosePieces.AddChild(body);
                pieces.Add(body);
            }
        }

        _loosePieces[itemId] = pieces;
        EmitSignal(SignalName.ItemBroken, itemId, pieces.Count);
        return true;
    }

    /// <summary>
    /// The loose pieces item <paramref name="itemId"/> broke into that still exist: valid and not queued for deletion
    /// (empty when it isn't broken).
    /// </summary>
    public IReadOnlyList<RigidBody3D> LoosePiecesOf(string itemId) =>
        _loosePieces.TryGetValue(itemId, out var list) ? list.Where(IsLivePiece).ToList() : [];

    private static bool IsLivePiece(RigidBody3D piece) => IsInstanceValid(piece) && !piece.IsQueuedForDeletion();

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
                _restTransforms[(batch, i)] = list[i].Transform;
                SetInstance(batch, i, list[i].Transform);
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
        _loosePieces.Clear();
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
                SetInstance(batch, index, _restTransforms[(batch, index)]);
            }
        }

        _broken.Clear();
    }

    /// <summary>
    /// Removes item <paramref name="itemId"/>'s static body from the tree and hides its batch instances (the first half
    /// of <see cref="Break"/>, which adds the loose pieces). Returns false for an unknown or already broken item.
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
            SetInstance(batch, index, HiddenTransform);
        }

        return true;
    }

    /// <summary>The transform a hidden batch instance gets (zero scale, so it draws nothing).</summary>
    public static readonly Transform3D HiddenTransform = new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);

    /// <summary>
    /// The transform this node last gave batch instance <paramref name="index"/> of <paramref name="batch"/> (its rest
    /// transform, or <see cref="HiddenTransform"/> while its item is broken), or null for an unknown instance. Read from
    /// this node's own record, because the headless rendering server keeps no multimesh instance transforms.
    /// </summary>
    public Transform3D? InstanceTransformOf(MultiMeshInstance3D batch, int index) =>
        _currentTransforms.TryGetValue((batch, index), out var transform) ? transform : null;

    /// <summary>The rest (unbroken) transform of a batch instance, or null for an unknown instance.</summary>
    public Transform3D? RestTransformOf(MultiMeshInstance3D batch, int index) =>
        _restTransforms.TryGetValue((batch, index), out var transform) ? transform : null;

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

    private void SetInstance(MultiMeshInstance3D batch, int index, Transform3D transform)
    {
        batch.Multimesh.SetInstanceTransform(index, transform);
        _currentTransforms[(batch, index)] = transform;
    }

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
        _currentTransforms.Clear();
        _broken.Clear();
        _loosePieces.Clear();
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

    private void CollectItems(Godot.Collections.Array<Godot.Collections.Dictionary> results, SortedSet<string> hits)
    {
        foreach (var result in results)
        {
            if (result["collider"].AsGodotObject() is BarrierItem item && item.GetParent() == this
                && _itemsById.TryGetValue(item.ItemId, out var known) && known == item && !_broken.Contains(item.ItemId))
            {
                hits.Add(item.ItemId);
            }
        }
    }

    /// <summary>
    /// A loose body for <paramref name="piece"/>: an orthonormal frame on the piece's box (along its x axis, which may
    /// slope, and its horizontal z axis), the batch mesh under it with the rest of the box's transform (so it looks
    /// exactly as the batch instance did), and a shape of the box's size in that frame. Null for a piece without
    /// extent.
    /// </summary>
    private RigidBody3D? CreateLoosePiece(
        string name, BarrierKind kind, BarrierPiece piece, Transform3D toPieces, BreakParameters parameters, Vector3 velocity)
    {
        var full = toPieces * ToTransform(piece.Box);
        var axisX = full.Basis.Column0;
        var axisY = full.Basis.Column1;
        var axisZ = full.Basis.Column2;
        if (axisX.LengthSquared() < 1e-12f || axisZ.LengthSquared() < 1e-12f || axisY.LengthSquared() < 1e-12f)
        {
            return null;
        }

        var x = axisX.Normalized();
        var y = axisZ.Normalized().Cross(x).Normalized();
        var z = x.Cross(y);
        var frame = new Basis(x, y, z);
        var sizeX = axisX.Length();
        var sizeY = Mathf.Abs(axisY.Dot(y));
        var sizeZ = axisZ.Length();

        var body = new RigidBody3D
        {
            Name = name,
            Transform = new Transform3D(frame, full.Origin),
            CollisionLayer = CollisionLayers.Barrier,
            CollisionMask = LoosePieceMask,
            Mass = (float)parameters.PieceMass(piece.Kind, sizeX, sizeY, sizeZ),
            LinearVelocity = velocity,
        };
        var mesh = _batches.TryGetValue((kind, piece.Kind), out var batch) ? batch.Multimesh.Mesh : MeshFor(piece.Kind);
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh",
            Mesh = mesh,
            Transform = new Transform3D(frame.Inverse() * full.Basis, Vector3.Zero),
        });
        body.AddChild(new CollisionShape3D { Name = "Collision", Shape = LooseShape(piece.Kind, sizeX, sizeY, sizeZ) });
        return body;
    }

    /// <summary>A loose piece's shape: a cylinder for posts, a sphere for round floats, else a box.</summary>
    private static Shape3D LooseShape(PieceKind kind, float sizeX, float sizeY, float sizeZ)
    {
        var size = new Vector3(
            Mathf.Max(sizeX, MinLooseShapeSize), Mathf.Max(sizeY, MinLooseShapeSize), Mathf.Max(sizeZ, MinLooseShapeSize));
        if (kind == PieceKind.Post)
        {
            return new CylinderShape3D { Radius = Mathf.Max(size.X, size.Z) / 2f, Height = size.Y };
        }

        var smallest = Mathf.Min(size.X, Mathf.Min(size.Y, size.Z));
        var largest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
        if (kind == PieceKind.Float && largest <= smallest * 1.2f)
        {
            return new SphereShape3D { Radius = (size.X + size.Y + size.Z) / 6f };
        }

        return new BoxShape3D { Size = size };
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
