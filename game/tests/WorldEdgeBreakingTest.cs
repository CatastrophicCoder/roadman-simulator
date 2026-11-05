using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.Debug;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Scenes.Edge;
using static GdUnit4.Assertions;
using static Roadman.Tests.WorldEdgeTest;

namespace Roadman.Tests;

/// <summary>
/// The world edge in Godot, Part B of <c>docs/specs/world-edge-game.md</c> (<c>Ac</c> numbers are its acceptance
/// criteria): a car-sized <see cref="TestBarrierBreaker"/> breaking a roadblock (12), the restore on a death respawn and
/// on a map reload (17), the safety net tracking loose pieces, and dismissing the death screen with a key or gamepad
/// button press (16; Part B plan amendment 1). Shares <see cref="WorldEdgeTest"/>'s helpers and rules: every position
/// is derived from the loaded pack (roadblocks from <see cref="Barriers.Layouts"/> and
/// <see cref="Roadman.Core.Maps.Boundary.Roadblocks"/>, heights from ray hits), and motion and timing are scripted per
/// physics frame, never wall time.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class WorldEdgeBreakingTest
{
    /// <summary>Criterion 12: the breaking drive speed, m/s.</summary>
    private const float BreakingDriveSpeed = 15f;

    /// <summary>Criterion 12: the drive speed under <c>BreakSpeed</c>, m/s.</summary>
    private const float SlowDriveSpeed = 2f;

    /// <summary>Criterion 12: the spec's time limit from the start of the drive to the void trigger, s.</summary>
    private const float EnterVoidWithinSeconds = 3f;

    /// <summary>
    /// Criterion 12: the least distance from the breaker's sides to the roadblock's ends, m, so it hits only the
    /// roadblock (a search margin, not a tolerance).
    /// </summary>
    private const float SideClearance = 1f;

    /// <summary>Criterion 16: physical key used for the dismiss presses (bound to no action).</summary>
    private const Key DismissKey = Key.Enter;

    /// <summary>
    /// Criterion 16: gamepad button used for the dismiss presses: the south face button. Not the start button, which
    /// is the <c>pause</c> action and never dismisses the death screen (<c>docs/specs/menu-settings-save.md</c>,
    /// criterion 12). The player's input is off until the respawn, so its <c>jump</c> binding doesn't matter.
    /// </summary>
    private const JoyButton DismissButton = GamepadButtons.South;

    private const int PadDevice = 0;

    [AfterTest]
    public void CleanUp()
    {
        Inject(new InputEventKey { PhysicalKeycode = DismissKey, Pressed = false });
        Inject(new InputEventJoypadButton { Device = PadDevice, ButtonIndex = DismissButton, Pressed = false });
        Inject(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        DebugBuild.Override = null;
    }

    // ----- Criterion 12: a vehicle breaks a roadblock -----

    /// <summary>
    /// The <see cref="TestBarrierBreaker"/> (1.8 × 1.5 × 4.5 m, 1,200 kg) on the most level drivable roadblock route of
    /// the pack, <see cref="WorldEdgeTest.RunStartDistance"/> m inward of the barrier and facing outward, driven at
    /// 15 m/s: the roadblock breaks (<see cref="Barriers.ItemBroken"/> with its piece count, in
    /// <see cref="Barriers.BrokenItemIds"/>, the static body out of the tree and no longer blocking, loose pieces on
    /// <see cref="CollisionLayers.Barrier"/> with the breaker's velocity, tracked by the safety net), the body passes
    /// the boundary and the void trigger reports it within 3 s, and every loose piece that falls into the void is freed
    /// within <see cref="World.MaxFallTime"/>. No error is pushed.
    /// </summary>
    [TestCase]
    public async Task Ac12_CarSizedBreakerAt15MetresPerSecond_BreaksTheRoadblock_EntersTheVoidWithin3s_AndItsLoosePiecesInTheVoidAreFreed()
    {
        AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
            var world = await StartWorld(runner);
            var barriers = world.MapLoader.Barriers!;
            var geometry = world.MapLoader.EdgeGeometry!;
            var trigger = world.MapLoader.VoidTrigger!;
            if (FindBreakerRoute(world) is not { } route)
            {
                return;
            }

            var id = route.Item.Layout.Id;
            var instances = barriers.InstancesOf(id).Count;
            AssertInt(instances).IsGreater(0);
            var item = barriers.ItemById(id)!;
            AssertBool(BarrierBetween(world, route, id)).OverrideFailureMessage("no roadblock collision across the route before the drive").IsTrue();

            var frame = 0;
            var broken = new List<BreakRecord>();
            TestBarrierBreaker? breaker = null;
            void OnBroken(string itemId, int pieceCount)
            {
                var pieces = barriers.LoosePiecesOf(itemId).ToList();
                var velocity = breaker!.BreakerVelocity; // the velocity the sweep used: set before this step
                broken.Add(new BreakRecord(
                    itemId,
                    pieceCount,
                    frame,
                    pieces,
                    barriers.ItemById(itemId) is { } body && body.IsInsideTree(),
                    pieces.All(p => p.CollisionLayer == CollisionLayers.Barrier && p.CollisionMask == Barriers.LoosePieceMask),
                    pieces.Count == 0 ? 0f : pieces.Max(p => p.LinearVelocity.DistanceTo(velocity)),
                    pieces.All(p => world.TrackedVoidBodies.Contains(p)),
                    pieces.All(p => p.GetParent() == barriers.LoosePieces)));
            }

            var reportedAt = -1;
            void OnEntered(Node3D body, Vector3 outward)
            {
                if (body is TestBarrierBreaker && reportedAt < 0)
                {
                    reportedAt = frame;
                }
            }

            barriers.ItemBroken += OnBroken;
            trigger.BodyEnteredVoid += OnEntered;
            breaker = new TestBarrierBreaker { Transform = route.StartTransform };
            world.AddChild(breaker);

            var ticks = Engine.PhysicsTicksPerSecond;
            var maxFallFrames = FramesFor(world.MaxFallTime, ticks);
            var limit = FramesFor(EnterVoidWithinSeconds, ticks) + maxFallFrames + ticks;
            var passedAt = -1;
            var speedAfterBreak = float.NaN;
            var fallingFrom = new Dictionary<RigidBody3D, int>();
            var freedAt = new Dictionary<RigidBody3D, int>();
            try
            {
                await Drive(world, f =>
                {
                    frame = f;
                    if (!IsFreed(breaker))
                    {
                        // SceneTree.PhysicsFrame fires before the nodes' _PhysicsProcess, so a break recorded with
                        // frame f happened in iteration f before its physics step: here, at f + 1 and before the velocity
                        // is set again, the breaker's velocity is the result of exactly one step after the break.
                        if (float.IsNaN(speedAfterBreak) && broken.FirstOrDefault(b => b.Id == id) is { } first && f == first.Frame + 1)
                        {
                            var v = breaker.LinearVelocity;
                            speedAfterBreak = new Vector3(v.X, 0f, v.Z).Dot(route.Item.Across);
                        }

                        if (reportedAt < 0 && !world.VoidFall.IsFalling(breaker))
                        {
                            // Driven at 15 m/s: friction and bumps can't slow it before the void takes over.
                            breaker.LinearVelocity = (route.Item.Across * BreakingDriveSpeed) + (Vector3.Up * breaker.LinearVelocity.Y);
                        }

                        var p = breaker.GlobalPosition;
                        if (passedAt < 0 && !geometry.IsInsideRing(p.X, p.Z))
                        {
                            passedAt = f;
                        }
                    }

                    foreach (var piece in broken.SelectMany(b => b.Pieces))
                    {
                        if (IsFreed(piece))
                        {
                            freedAt.TryAdd(piece, f);
                        }
                        else if (world.VoidFall.IsFalling(piece))
                        {
                            fallingFrom.TryAdd(piece, f);
                        }
                    }

                    var allPieces = broken.SelectMany(b => b.Pieces).ToList();
                    return broken.Count > 0 && allPieces.All(freedAt.ContainsKey);
                }, limit);
            }
            finally
            {
                barriers.ItemBroken -= OnBroken;
                trigger.BodyEnteredVoid -= OnEntered;
            }

            var record = broken.FirstOrDefault(b => b.Id == id);
            var fell = fallingFrom.Keys.ToList();
            var lateFrees = fell
                .Where(p => !freedAt.TryGetValue(p, out var at) || at - fallingFrom[p] > maxFallFrames + 1)
                .Select(p => Invariant($"{p.Name}: falling from frame {fallingFrom[p]}, freed {(freedAt.TryGetValue(p, out var at) ? at.ToString(System.Globalization.CultureInfo.InvariantCulture) : "never")}"))
                .ToList();
            var allLoose = broken.SelectMany(b => b.Pieces).ToList();
            GD.Print(Invariant(
                $"WorldEdgeBreakingTest: Ac12 {id} ({route.Item.Length:0.##} m, route slope up to {route.MaxSlopeDegrees:0.##}°): broke at frame {record?.Frame}, speed along the route one step later {speedAfterBreak:0.####} m/s, items broken {string.Join(",", broken.Select(b => b.Id))}; {record?.PieceCount} pieces, worst piece velocity off the breaker's {record?.WorstVelocityError:0.####} m/s; breaker centre past the ring at frame {passedAt}, reported by the trigger at frame {reportedAt} (limit {FramesFor(EnterVoidWithinSeconds, ticks)}); {fell.Count} of {allLoose.Count} loose pieces fell into the void, {freedAt.Count} freed (MaxFallTime {world.MaxFallTime} s = {maxFallFrames} frames)"));

            AssertBool(record is not null).OverrideFailureMessage($"the roadblock {id} didn't break").IsTrue();
            AssertInt(record!.PieceCount).IsEqual(route.Item.Layout.Pieces.Count);
            AssertInt(record.Pieces.Count).IsEqual(record.PieceCount);
            AssertBool(record.ItemInTree).OverrideFailureMessage("the roadblock's static body is still in the tree").IsFalse();
            AssertBool(GodotObject.IsInstanceValid(item) && !item.IsInsideTree()).IsTrue();
            AssertBool(BarrierBetween(world, route, id)).OverrideFailureMessage("the broken roadblock still blocks").IsFalse();
            AssertInt(barriers.InstancesOf(id).Count).IsEqual(instances);
            AssertBool(AllInstances(barriers, id, hidden: true)).OverrideFailureMessage("a batch instance of the broken roadblock is still visible").IsTrue();
            AssertBool(record.PiecesOnBarrierLayer).OverrideFailureMessage("a loose piece isn't on the Barrier layer with LoosePieceMask").IsTrue();
            AssertBool(record.PiecesUnderLoosePieces).IsTrue();
            AssertFloat(record.WorstVelocityError).OverrideFailureMessage("a loose piece didn't get the breaker's velocity").IsLessEqual(1e-4f);
            AssertBool(record.PiecesTracked).OverrideFailureMessage("the loose pieces weren't handed to the safety net").IsTrue();

            // Broken before contact (Part B plan amendment 5: the sweep runs before the physics step): one step after
            // the break the breaker still moves at nearly the drive speed. Hitting the static body first would stop it
            // (a 1,200 kg body against a static wall loses nearly all its speed into the barrier in one step). What it
            // may lose is ground friction for one step, at most mu * g * dt = 1.0 * 9.8 m/s² / 60 ≈ 0.163 m/s (about 1.1 %
            // of 15 m/s; Godot's default friction 1.0 on both bodies), plus the slope of the route. Measured on the full
            // Somero pack (roadblock:cut:way/34070814:0, route slope up to 9.5°): 14.837 m/s, a loss of 0.163 m/s, exactly
            // that friction. 5 % (0.75 m/s) leaves room for other routes and packs and is still far from a stop.
            AssertFloat(speedAfterBreak)
                .OverrideFailureMessage(Invariant($"one physics step after the break the breaker moved at {speedAfterBreak:0.###} m/s along the route, under 95 % of {BreakingDriveSpeed} m/s: it hit the roadblock before it broke"))
                .IsGreaterEqual(0.95f * BreakingDriveSpeed);
            AssertBool(barriers.BrokenItemIds.Contains(id)).IsTrue();
            AssertBool(world.Death.State == DeathState.Alive).IsTrue();

            AssertInt(passedAt).OverrideFailureMessage("the breaker never got past the boundary").IsGreaterEqual(0);
            AssertInt(reportedAt).OverrideFailureMessage("the void trigger never reported the breaker").IsGreaterEqual(0);
            AssertInt(reportedAt).OverrideFailureMessage("the breaker reached the void trigger later than 3 s").IsLessEqual(FramesFor(EnterVoidWithinSeconds, ticks));
            AssertInt(passedAt).OverrideFailureMessage("the breaker passed the boundary later than 3 s").IsLessEqual(FramesFor(EnterVoidWithinSeconds, ticks));

            AssertInt(fell.Count).OverrideFailureMessage("no loose piece reached the void, so the test can't tell").IsGreater(0);
            AssertThat(string.Join("; ", lateFrees.Take(5))).OverrideFailureMessage($"loose pieces not freed within MaxFallTime ({lateFrees.Count})").IsEqual("");
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    /// <summary>
    /// The same breaker on the same route at 2 m/s (under <c>BreakSpeed</c>), driven for the run to the barrier and
    /// <see cref="WorldEdgeTest.StopWithinSeconds"/> more: no item breaks, no loose piece appears, it never enters the
    /// void, and it stops at the barrier (its front reaches the collision and stays inside it, steady over the last half
    /// second).
    /// </summary>
    [TestCase]
    public async Task Ac12_CarSizedBreakerAt2MetresPerSecond_BreaksNothingAndStopsAtTheRoadblock()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var barriers = world.MapLoader.Barriers!;
        var trigger = world.MapLoader.VoidTrigger!;
        var settings = barriers.Settings;
        AssertFloat(SlowDriveSpeed).OverrideFailureMessage("the slow speed isn't under BreakSpeed").IsLess(settings.BreakingSpeed);
        if (FindBreakerRoute(world) is not { } route)
        {
            return;
        }

        var id = route.Item.Layout.Id;
        var halfThickness = settings.CollisionThickness / 2f;
        var halfLength = TestBarrierBreaker.Size.Z / 2f;

        var brokenIds = new List<string>();
        void OnBroken(string itemId, int _) => brokenIds.Add(itemId);
        var reported = 0;
        void OnEntered(Node3D body, Vector3 _)
        {
            if (body is TestBarrierBreaker)
            {
                reported++;
            }
        }

        barriers.ItemBroken += OnBroken;
        trigger.BodyEnteredVoid += OnEntered;
        var breaker = new TestBarrierBreaker { Transform = route.StartTransform };
        world.AddChild(breaker);

        var ticks = Engine.PhysicsTicksPerSecond;
        var frames = Mathf.CeilToInt(((RunStartDistance / SlowDriveSpeed) + StopWithinSeconds) * ticks);
        var fronts = new List<float>();
        try
        {
            await Drive(world, f =>
            {
                breaker.LinearVelocity = (route.Item.Across * SlowDriveSpeed) + (Vector3.Up * breaker.LinearVelocity.Y);
                var front = Horizontal(breaker.GlobalPosition - route.Mid).Dot(route.Item.Across) + halfLength;
                fronts.Add(front);
                return false;
            }, frames + 1);
        }
        finally
        {
            barriers.ItemBroken -= OnBroken;
            trigger.BodyEnteredVoid -= OnEntered;
        }

        var maxSpeed = fronts.Zip(fronts.Skip(1), (a, b) => (b - a) * ticks).Max();
        var tail = fronts.Skip(fronts.Count - (ticks / 2)).ToList();
        var drift = tail.Max() - tail.Min();
        var furthest = fronts.Max();
        GD.Print(Invariant(
            $"WorldEdgeBreakingTest: Ac12 slow {SlowDriveSpeed} m/s at {id}: top outward speed {maxSpeed:0.###} m/s, front furthest {furthest:0.###} m from the line (collision from {-halfThickness:0.###} to {halfThickness:0.###}), drift over the last 0.5 s {drift:0.#####} m, broken {brokenIds.Count}, trigger reports {reported}"));
        AssertFloat(maxSpeed).OverrideFailureMessage("the breaker never moved at the slow speed").IsGreaterEqual(SlowDriveSpeed * 0.9f);
        AssertInt(brokenIds.Count).OverrideFailureMessage($"items broke: {string.Join(",", brokenIds)}").IsEqual(0);
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(0);
        AssertInt(barriers.LoosePieceCount).IsEqual(0);
        AssertInt(barriers.LoosePieces.GetChildCount()).IsEqual(0);
        AssertBool(barriers.ItemById(id)!.IsInsideTree()).IsTrue();
        AssertInt(reported).OverrideFailureMessage("the slow breaker entered the void").IsEqual(0);
        AssertBool(world.VoidFall.IsFalling(breaker)).IsFalse();
        AssertFloat(furthest).OverrideFailureMessage("the breaker never reached the roadblock").IsGreaterEqual(-halfThickness - 0.05f);
        AssertFloat(furthest).OverrideFailureMessage("the breaker's front got through the roadblock's collision").IsLessEqual(halfThickness);
        AssertFloat(drift).OverrideFailureMessage("the breaker was still moving at the end").IsLessEqual(StoppedDrift);
    }

    /// <summary>
    /// The player on foot is never a breaker (spec "Breaking": only vehicles); criterion 9's runs into a fence, a water
    /// fence and a roadblock check that nothing breaks.
    /// </summary>
    [TestCase]
    public async Task Ac9_ThePlayerIsNeverABarrierBreaker()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var player = world.Player!;
        AssertBool(player is IBarrierBreaker).IsFalse();
        AssertBool(player.IsInGroup(Barriers.BreakerGroup)).IsFalse();
        AssertInt(world.GetTree().GetNodesInGroup(Barriers.BreakerGroup).Count).IsEqual(0);
    }

    // ----- Criterion 17: broken barriers are restored -----

    /// <summary>
    /// After the breaker broke a roadblock (criterion 12), a player death (placed on the ledge, as in criterion 13) and
    /// its respawn rebuild it: the same static body back under <c>Barriers</c> on
    /// <see cref="CollisionLayers.BarrierStatic"/> and blocking again, the same number of batch instances, no loose
    /// piece left (all freed, none tracked by the safety net), <see cref="Barriers.BrokenItemIds"/> empty, and every
    /// batch instance of the item hidden while it is broken and back at its rest transform after the respawn. The
    /// transforms are read with <see cref="Barriers.InstanceTransformOf"/> (against <see cref="Barriers.HiddenTransform"/>
    /// and <see cref="Barriers.RestTransformOf"/>), which reads <see cref="Barriers"/>' own record of what it set: the
    /// headless test run's dummy rendering server keeps no multimesh instance transforms
    /// (<see cref="MultiMesh.GetInstanceTransform"/> always gives identity there).
    /// </summary>
    [TestCase]
    public async Task Ac17_AfterABreak_ADeathAndRespawn_RestoresTheRoadblockAndFreesTheLoosePieces()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var barriers = world.MapLoader.Barriers!;
        if (FindBreakerRoute(world) is not { } route)
        {
            return;
        }

        var id = route.Item.Layout.Id;
        var item = barriers.ItemById(id)!;
        var instances = barriers.InstancesOf(id).Count;
        var (_, pieces) = await BreakWithBreaker(world, route);

        AssertBool(item.IsInsideTree()).IsFalse();
        AssertBool(BarrierBetween(world, route, id)).OverrideFailureMessage("the broken roadblock still blocks").IsFalse();
        AssertBool(AllInstances(barriers, id, hidden: true)).OverrideFailureMessage("a batch instance of the broken roadblock is still visible").IsTrue();

        var entry = FindLedgeEntry(world);
        var respawned = 0;
        world.PlayerRespawned += _ => respawned++;
        AssertEntered(await PlaceOnLedgeAndWaitForEntry(world, entry));
        var stillBrokenWhileDead = barriers.BrokenItemIds.Contains(id);
        AssertBool(await WaitUntil(world, () => world.Death.State is DeathState.Alive or DeathState.Failed,
            Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20)).IsTrue();
        await PhysicsFrames(world, 1);

        GD.Print(Invariant(
            $"WorldEdgeBreakingTest: Ac17 respawn after breaking {id} into {pieces.Count} pieces: broken while dead {stillBrokenWhileDead}; now state {world.Death.State}, broken {barriers.BrokenItemIds.Count}, loose {barriers.LoosePieceCount}, body in tree {item.IsInsideTree()}"));
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertInt(respawned).IsEqual(1);
        AssertBool(stillBrokenWhileDead).OverrideFailureMessage("the roadblock was restored before the respawn").IsTrue();
        AssertRestored(world, barriers, route, instances, pieces);
        AssertThat(barriers.ItemById(id)).IsSame(item);
    }

    /// <summary>
    /// After the breaker broke a roadblock, a map reload gives a fresh, unbroken set: new <see cref="Barriers"/> with no
    /// broken item, the roadblock's body in the tree and blocking, the same number of batch instances, no loose piece
    /// anywhere under the map loader, and the old pieces freed and untracked. Instance transforms: see the test above.
    /// </summary>
    [TestCase]
    public async Task Ac17_AfterABreak_AMapReload_GivesAnUnbrokenSetWithNoLoosePieces()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var loader = world.MapLoader;
        var oldBarriers = loader.Barriers!;
        if (FindBreakerRoute(world) is not { } route)
        {
            return;
        }

        var id = route.Item.Layout.Id;
        var instances = oldBarriers.InstancesOf(id).Count;
        var (breaker, pieces) = await BreakWithBreaker(world, route);
        AssertBool(oldBarriers.BrokenItemIds.Contains(id)).IsTrue();

        // The breaker is still in the roadblock's line, moving at about 15 m/s; left there it would break the rebuilt
        // item too (a vehicle driving through). Vehicles on a reload are the vehicles spec's; here it is removed.
        world.RemoveChild(breaker);
        breaker.QueueFree();

        var spawned = 0;
        world.PlayerSpawned += _ => spawned++;
        loader.Load();
        AssertBool(await WaitUntil(world, () => spawned > 0, world.SpawnPhysicsWaitFrames + 30)).OverrideFailureMessage("no spawn after the reload").IsTrue();
        AssertBool(await loader.WaitForWorldEdgeAsync(EdgeWaitFrames)).OverrideFailureMessage("the barriers weren't rebuilt").IsTrue();
        await PhysicsFrames(world, 2);

        var barriers = loader.Barriers!;
        GD.Print(Invariant(
            $"WorldEdgeBreakingTest: Ac17 reload after breaking {id} into {pieces.Count} pieces: new barriers {!ReferenceEquals(barriers, oldBarriers)}, broken {barriers.BrokenItemIds.Count}, loose {barriers.LoosePieceCount}, rigid bodies under the loader {Descendants(loader).OfType<RigidBody3D>().Count()}"));
        AssertThat(barriers).IsNotSame(oldBarriers);
        AssertBool(GodotObject.IsInstanceValid(oldBarriers) && oldBarriers.IsInsideTree()).IsFalse();
        AssertRestored(world, barriers, route, instances, pieces);
        AssertInt(Descendants(loader).OfType<RigidBody3D>().Count()).IsEqual(0);
    }

    // ----- Safety net: loose pieces are tracked -----

    /// <summary>
    /// A break hands every loose piece to the safety net (<see cref="World.TrackVoidBody"/>, from
    /// <see cref="Barriers.ItemBroken"/>); one teleported just below the net inside the boundary is freed within one
    /// physics frame, the others stay. After a map reload the new <see cref="Barriers"/> is connected too.
    /// </summary>
    [TestCase]
    public async Task SafetyNet_LoosePiecesOfABreak_AreTracked_AndOneJustBelowTheNetIsFreed_AlsoAfterAReload()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var loader = world.MapLoader;

        await AssertPieceBelowNetFreed(world, loader.Barriers!, "first load");

        var spawned = 0;
        world.PlayerSpawned += _ => spawned++;
        loader.Load();
        AssertBool(await WaitUntil(world, () => spawned > 0, world.SpawnPhysicsWaitFrames + 30)).IsTrue();
        AssertBool(await loader.WaitForWorldEdgeAsync(EdgeWaitFrames)).IsTrue();
        AssertInt(world.TrackedVoidBodies.Count).OverrideFailureMessage("the reload didn't drop the tracked bodies").IsEqual(0);

        await AssertPieceBelowNetFreed(world, loader.Barriers!, "after a reload");
    }

    // ----- Criterion 16: dismissing the death screen -----

    /// <summary>
    /// <see cref="World.IsDismissPress"/>: a key press (not an echo), a gamepad button press and a mouse button press
    /// count; a key echo, a key, button or mouse button release, the mouse wheel, mouse motion and joypad motion (sticks
    /// and analog triggers) don't.
    /// </summary>
    [TestCase]
    public void Ac16_IsDismissPress_KeyGamepadButtonAndMouseButtonPressesOnly()
    {
        AssertBool(World.IsDismissPress(new InputEventKey { PhysicalKeycode = Key.Enter, Pressed = true })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventKey { PhysicalKeycode = Key.A, Pressed = true })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventJoypadButton { ButtonIndex = GamepadButtons.East, Pressed = true })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventJoypadButton { ButtonIndex = GamepadButtons.South, Pressed = true, Device = -1 })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true })).IsTrue();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true })).IsTrue();

        AssertBool(World.IsDismissPress(new InputEventKey { PhysicalKeycode = Key.Enter, Pressed = true, Echo = true })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventKey { PhysicalKeycode = Key.Enter, Pressed = false })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventJoypadButton { ButtonIndex = GamepadButtons.East, Pressed = false })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.WheelLeft, Pressed = true })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseButton { ButtonIndex = MouseButton.WheelRight, Pressed = true })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventMouseMotion { Relative = new Vector2(10f, 0f) })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1f })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 1f })).IsFalse();
        AssertBool(World.IsDismissPress(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = -1f })).IsFalse();
    }

    /// <summary>
    /// On the death screen, a key press in the last physics frame before <see cref="World.MinScreenTime"/> changes
    /// nothing and isn't kept: with no further input the screen stays until <see cref="World.ScreenTime"/> (the default
    /// 5 s, to the physics frame) and the respawn follows then.
    /// </summary>
    [TestCase]
    public async Task Ac16_KeyPressJustBeforeMinScreenTime_ChangesNothingAndIsNotKept()
    {
        var trace = await DeathScreenWithPress(PressKind.Key, PressAt.JustBeforeMinScreenTime);

        AssertInt(trace.PressedAtFrame).OverrideFailureMessage("no press was made").IsGreaterEqual(0);
        AssertFloat((float)trace.TimeAtPress).IsLess((float)trace.MinScreenTime);
        AssertThat(trace.StateAfterPress).IsEqual(DeathState.DeathScreen);
        AssertBool(trace.ScreenShownAfterMin).OverrideFailureMessage("the death screen didn't stay past MinScreenTime").IsTrue();
        AssertInt(trace.RespawnFrame - trace.ScreenFrame).OverrideFailureMessage("the respawn didn't come at ScreenTime").IsEqual(trace.ScreenFrames);
        AssertInt(trace.Respawns).IsEqual(1);
        AssertThat(trace.FinalState).IsEqual(DeathState.Alive);
    }

    /// <summary>
    /// On the death screen, a key press in the first physics frame at <see cref="World.MinScreenTime"/> moves the
    /// sequence to <see cref="DeathState.Respawning"/> at once, and the respawn runs in the next physics frame:
    /// <see cref="World.PlayerRespawned"/> fires once, the state is <see cref="DeathState.Alive"/>, and the player is
    /// back as after a timed respawn (on the floor within 120 physics frames, input on, its camera current, the death
    /// screen hidden).
    /// </summary>
    [TestCase]
    public async Task Ac16_KeyPressAtMinScreenTime_RespawnsInTheNextPhysicsFrame()
    {
        var trace = await DeathScreenWithPress(PressKind.Key, PressAt.MinScreenTime);
        AssertDismissed(trace);
    }

    /// <summary>A gamepad button press after <see cref="World.MinScreenTime"/> dismisses the death screen as a key does.</summary>
    [TestCase]
    public async Task Ac16_GamepadButtonPressAfterMinScreenTime_RespawnsInTheNextPhysicsFrame()
    {
        var trace = await DeathScreenWithPress(PressKind.GamepadButton, PressAt.AfterMinScreenTime);
        AssertDismissed(trace);
    }

    /// <summary>A mouse click after <see cref="World.MinScreenTime"/> dismisses the death screen as a key does.</summary>
    [TestCase]
    public async Task Ac16_MouseButtonPressAfterMinScreenTime_RespawnsInTheNextPhysicsFrame()
    {
        var trace = await DeathScreenWithPress(PressKind.MouseButton, PressAt.AfterMinScreenTime);
        AssertDismissed(trace);
    }

    /// <summary>
    /// A mouse click in the last physics frame before <see cref="World.MinScreenTime"/> changes nothing and isn't kept,
    /// as with a key: the screen stays until <see cref="World.ScreenTime"/> and the respawn follows then.
    /// </summary>
    [TestCase]
    public async Task Ac16_MouseButtonPressJustBeforeMinScreenTime_ChangesNothingAndIsNotKept()
    {
        var trace = await DeathScreenWithPress(PressKind.MouseButton, PressAt.JustBeforeMinScreenTime);

        AssertInt(trace.PressedAtFrame).OverrideFailureMessage("no press was made").IsGreaterEqual(0);
        AssertFloat((float)trace.TimeAtPress).IsLess((float)trace.MinScreenTime);
        AssertThat(trace.StateAfterPress).IsEqual(DeathState.DeathScreen);
        AssertBool(trace.ScreenShownAfterMin).OverrideFailureMessage("the death screen didn't stay past MinScreenTime").IsTrue();
        AssertInt(trace.RespawnFrame - trace.ScreenFrame).OverrideFailureMessage("the respawn didn't come at ScreenTime").IsEqual(trace.ScreenFrames);
        AssertInt(trace.Respawns).IsEqual(1);
        AssertThat(trace.FinalState).IsEqual(DeathState.Alive);
    }

    // ===== Helpers: breaking =====

    private sealed record BreakRecord(
        string Id,
        int PieceCount,
        int Frame,
        List<RigidBody3D> Pieces,
        bool ItemInTree,
        bool PiecesOnBarrierLayer,
        float WorstVelocityError,
        bool PiecesTracked,
        bool PiecesUnderLoosePieces);

    /// <summary>
    /// A breaker route: <paramref name="Item"/>, the roadblock; <paramref name="Mid"/>, its midpoint on the ground;
    /// <paramref name="StartTransform"/>, the breaker's start (centred on the route, its front
    /// <see cref="WorldEdgeTest.RunStartDistance"/> m inward of the line, long axis outward, its bottom
    /// <see cref="WorldEdgeTest.PlaceLift"/> above the highest ground under it).
    /// </summary>
    private readonly record struct BreakerRoute(ItemFrame Item, Vector3 Mid, Transform3D StartTransform, float MaxSlopeDegrees);

    /// <summary>
    /// Criterion 12's route: among the drivable roadblocks (<c>drivable</c> in <c>boundary.json</c>) at least the
    /// breaker's width + 2 × <see cref="SideClearance"/> long, the one whose ground is most level along the breaker's
    /// path. The path runs from the breaker's back at the start to the ledge's outer edge (<c>cliff.ledge_m</c>), along
    /// its centre and both sides; every <see cref="WorldEdgeTest.SampleStep"/> the first map hit from above must be
    /// terrain, road or the ledge, and no slope between samples (along or across) may exceed
    /// <see cref="WorldEdgeTest.MaxRouteSlopeDegrees"/>. The breaker's box at the start must overlap nothing.
    /// </summary>
    private static BreakerRoute? FindBreakerRoute(World world)
    {
        var loader = world.MapLoader;
        var boundary = loader.Boundary!;
        var barriers = loader.Barriers!;
        var space = world.GetWorld3D().DirectSpaceState;
        var size = TestBarrierBreaker.Size;
        var halfWidth = size.X / 2f;
        var halfLength = size.Z / 2f;
        var ledge = (float)boundary.Cliff.LedgeM;
        var drivable = boundary.Roadblocks.Where(r => r.Drivable).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = barriers.Layouts
            .Where(l => l.Kind == BarrierKind.Roadblock && drivable.Contains(l.Id))
            .Select(FrameOf)
            .Where(f => f.Length >= size.X + (2f * SideClearance))
            .ToList();
        if (candidates.Count == 0)
        {
            AssertNotFullPack(world, "Ac12", $"has no drivable roadblock at least {size.X + (2f * SideClearance)} m long");
            return null;
        }

        var back = -((2f * halfLength) + RunStartDistance);
        var front = ledge - SampleStep;
        var maxRise = Mathf.Tan(Mathf.DegToRad(MaxRouteSlopeDegrees));
        BreakerRoute? best = null;
        var rejected = new List<string>();
        foreach (var item in candidates)
        {
            var mid = item.At(0.5f);
            var stations = new List<float>();
            for (var d = back; d <= front + 1e-4f; d += SampleStep)
            {
                stations.Add(d);
            }

            var heights = new float[3, stations.Count];
            string? reason = null;
            for (var line = 0; line < 3 && reason is null; line++)
            {
                var offset = (line - 1) * halfWidth;
                for (var i = 0; i < stations.Count; i++)
                {
                    var point = mid + (item.Across * stations[i]) + (item.Along * offset);
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        new Vector3(point.X, 10_000f, point.Z), new Vector3(point.X, -10_000f, point.Z), CollisionLayers.Map));
                    if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node body
                        || !(IsGround(body) || body.IsInGroup(MapLoader.EdgeGroup)))
                    {
                        reason = Invariant($"no ground at {stations[i]:0.##} m");
                        break;
                    }

                    heights[line, i] = hit["position"].AsVector3().Y;
                }
            }

            var maxSlope = 0f;
            for (var i = 0; i < stations.Count && reason is null; i++)
            {
                for (var line = 0; line < 3; line++)
                {
                    if (i > 0)
                    {
                        maxSlope = Mathf.Max(maxSlope, Mathf.RadToDeg(Mathf.Atan(Mathf.Abs(heights[line, i] - heights[line, i - 1]) / SampleStep)));
                    }
                }

                var across = Mathf.Max(heights[0, i], Mathf.Max(heights[1, i], heights[2, i])) - Mathf.Min(heights[0, i], Mathf.Min(heights[1, i], heights[2, i]));
                maxSlope = Mathf.Max(maxSlope, Mathf.RadToDeg(Mathf.Atan(across / (2f * halfWidth))));
            }

            if (reason is null && maxSlope > MaxRouteSlopeDegrees)
            {
                reason = Invariant($"slope {maxSlope:0.#}°");
            }

            if (reason is not null)
            {
                rejected.Add($"{item.Layout.Id}: {reason}");
                continue;
            }

            // The breaker's bottom above the highest ground under its footprint at the start.
            var groundUnder = float.MinValue;
            for (var i = 0; i < stations.Count && stations[i] <= back + (2f * halfLength) + 1e-4f; i++)
            {
                groundUnder = Mathf.Max(groundUnder, Mathf.Max(heights[0, i], Mathf.Max(heights[1, i], heights[2, i])));
            }

            var centre = mid + (item.Across * (back + halfLength));
            centre.Y = groundUnder + (size.Y / 2f) + PlaceLift;
            var transform = new Transform3D(Basis.LookingAt(item.Across, Vector3.Up), centre);
            var overlap = space.IntersectShape(new PhysicsShapeQueryParameters3D
            {
                Shape = new BoxShape3D { Size = size },
                Transform = transform,
                CollisionMask = CollisionLayers.Map | CollisionLayers.Barriers | CollisionLayers.Player,
                CollideWithBodies = true,
            }, 4);
            if (overlap.Count > 0)
            {
                rejected.Add($"{item.Layout.Id}: start overlaps {(overlap[0]["collider"].AsGodotObject() as Node)?.Name}");
                continue;
            }

            var midGround = heights[1, stations.FindIndex(s => s >= 0f)];
            if (best is null || maxSlope < best.Value.MaxSlopeDegrees)
            {
                best = new BreakerRoute(item, mid with { Y = midGround }, transform, maxSlope);
            }
        }

        GD.Print(Invariant(
            $"WorldEdgeBreakingTest: {candidates.Count} drivable roadblocks long enough; chose {best?.Item.Layout.Id} (slope up to {best?.MaxSlopeDegrees:0.##}°); rejected {rejected.Count}: {string.Join("; ", rejected.Take(8))}"));
        if (best is null)
        {
            AssertNotFullPack(world, "Ac12", $"has no drivable roadblock with a level, clear route ({string.Join("; ", rejected)})");
        }

        return best;
    }

    /// <summary>
    /// Adds the breaker at <paramref name="route"/>'s start and drives it outward at 15 m/s until the roadblock breaks
    /// (then it rolls on by itself). Returns the breaker and the roadblock's loose pieces.
    /// </summary>
    private static async Task<(TestBarrierBreaker Breaker, List<RigidBody3D> Pieces)> BreakWithBreaker(World world, BreakerRoute route)
    {
        var barriers = world.MapLoader.Barriers!;
        var id = route.Item.Layout.Id;
        var breaker = new TestBarrierBreaker { Transform = route.StartTransform };
        world.AddChild(breaker);
        await Drive(world, _ =>
        {
            if (barriers.BrokenItemIds.Contains(id) || IsFreed(breaker))
            {
                return true;
            }

            breaker.LinearVelocity = (route.Item.Across * BreakingDriveSpeed) + (Vector3.Up * breaker.LinearVelocity.Y);
            return false;
        }, FramesFor(EnterVoidWithinSeconds, Engine.PhysicsTicksPerSecond));
        AssertBool(barriers.BrokenItemIds.Contains(id)).OverrideFailureMessage($"the breaker didn't break {id}").IsTrue();
        var pieces = barriers.LoosePiecesOf(id).ToList();
        AssertInt(pieces.Count).IsGreater(0);
        return (breaker, pieces);
    }

    /// <summary>
    /// Whether a ray on <see cref="CollisionLayers.BarrierStatic"/> straight across <paramref name="route"/>'s line, at
    /// half the roadblock height above the ground at its midpoint, hits item <paramref name="id"/>.
    /// </summary>
    private static bool BarrierBetween(World world, BreakerRoute route, string id)
    {
        var height = world.MapLoader.Barriers!.Settings.RoadblockHeight / 2f;
        var at = route.Mid + (Vector3.Up * height);
        var hit = world.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
            at - route.Item.Across, at + route.Item.Across, CollisionLayers.BarrierStatic));
        return hit.Count > 0 && hit["collider"].AsGodotObject() is BarrierItem item && item.ItemId == id;
    }

    /// <summary>Criterion 17's checks on <paramref name="barriers"/> after a restore or a reload.</summary>
    private static void AssertRestored(World world, Barriers barriers, BreakerRoute route, int instanceCount, List<RigidBody3D> oldPieces)
    {
        var id = route.Item.Layout.Id;
        var item = barriers.ItemById(id);
        AssertThat(item).IsNotNull();
        AssertBool(item!.IsInsideTree()).OverrideFailureMessage("the roadblock's static body isn't back in the tree").IsTrue();
        AssertThat(item.GetParent()).IsSame(barriers);
        AssertInt((int)item.CollisionLayer).IsEqual((int)CollisionLayers.BarrierStatic);
        AssertBool(BarrierBetween(world, route, id)).OverrideFailureMessage("the restored roadblock doesn't block").IsTrue();
        AssertInt(barriers.InstancesOf(id).Count).IsEqual(instanceCount);
        AssertBool(AllInstances(barriers, id, hidden: false)).OverrideFailureMessage("a batch instance of the restored roadblock isn't back at its rest transform").IsTrue();
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(0);
        AssertInt(barriers.LoosePieceCount).IsEqual(0);
        AssertInt(barriers.LoosePieces.GetChildCount()).IsEqual(0);
        AssertInt(oldPieces.Count(p => !IsFreed(p))).OverrideFailureMessage("loose pieces were left").IsEqual(0);
        AssertInt(oldPieces.Count(p => world.TrackedVoidBodies.Contains(p))).OverrideFailureMessage("freed pieces still tracked").IsEqual(0);
    }

    /// <summary>
    /// Breaks the first roadblock of <paramref name="barriers"/> with <see cref="Barriers.Break"/> (no velocity) and
    /// checks the safety net: every piece tracked; the first one teleported just below the net inside the boundary
    /// (at the player's x/z, not in a prism) freed within one physics frame, and the others not.
    /// </summary>
    private static async Task AssertPieceBelowNetFreed(World world, Barriers barriers, string when)
    {
        var (net, x, z) = SafetyNetPoint(world);
        var id = barriers.Layouts.First(l => l.Kind == BarrierKind.Roadblock).Id;
        AssertBool(barriers.Break(id, Vector3.Zero)).IsTrue();
        var pieces = barriers.LoosePiecesOf(id).ToList();
        var tracked = pieces.Count(p => world.TrackedVoidBodies.Contains(p));
        AssertInt(pieces.Count).IsGreater(1);

        var target = pieces[0];
        var freedAfterOne = false;
        var othersFreed = -1;
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                target.GlobalPosition = new Vector3(x, net - NetOffset, z);
                target.LinearVelocity = Vector3.Zero;
                return false;
            }

            freedAfterOne = IsFreed(target);
            othersFreed = pieces.Skip(1).Count(IsFreed);
            return true;
        }, 3);

        GD.Print(Invariant(
            $"WorldEdgeBreakingTest: safety net {when}: broke {id} into {pieces.Count} pieces, {tracked} tracked; the one below y {net - NetOffset:0.###} freed after one frame {freedAfterOne}, others freed {othersFreed}"));
        AssertInt(tracked).OverrideFailureMessage($"{when}: the loose pieces weren't tracked").IsEqual(pieces.Count);
        AssertBool(freedAfterOne).OverrideFailureMessage($"{when}: the piece below the net wasn't freed within one physics frame").IsTrue();
        AssertInt(othersFreed).IsEqual(0);
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
    }

    // ===== Helpers: dismissing the death screen =====

    private enum PressKind
    {
        Key,
        GamepadButton,
        MouseButton,
    }

    private enum PressAt
    {
        /// <summary>The last physics frame whose death-screen time is under <see cref="World.MinScreenTime"/>.</summary>
        JustBeforeMinScreenTime,

        /// <summary>The first physics frame whose death-screen time is at least <see cref="World.MinScreenTime"/>.</summary>
        MinScreenTime,

        /// <summary>Half a second after <see cref="World.MinScreenTime"/>, still before <see cref="World.ScreenTime"/>.</summary>
        AfterMinScreenTime,
    }

    private sealed record DismissTrace(
        int ScreenFrame,
        int PressedAtFrame,
        double TimeAtPress,
        DeathState StateAfterPress,
        int RespawnsAfterPress,
        bool ScreenShownAfterMin,
        DeathState StateNextFrame,
        int RespawnsNextFrame,
        int RespawnFrame,
        int ScreenFrames,
        double MinScreenTime,
        double ScreenTime,
        int Respawns,
        DeathState FinalState,
        Vector3? RespawnPosition,
        bool OnFloor,
        bool InputEnabled,
        bool PlayerCameraCurrent,
        bool ScreenHidden,
        bool InOpenGround);

    /// <summary>
    /// A death with the default <see cref="World.ScreenTime"/> and <see cref="World.MinScreenTime"/> (a short fall):
    /// on the death screen one press of <paramref name="kind"/> is injected (pressed, then released) at
    /// <paramref name="at"/>, at the start of a physics frame; returns what happened to the sequence then and up to the
    /// respawn and 120 frames after it.
    /// </summary>
    private static async Task<DismissTrace> DeathScreenWithPress(PressKind kind, PressAt at)
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, null), true);
        var world = await StartWorld(runner);
        var player = world.Player!;
        AssertFloat(world.ScreenTime).IsEqual(5f);
        AssertFloat(world.MinScreenTime).IsEqual(2f);
        var ticks = Engine.PhysicsTicksPerSecond;
        var dt = 1.0 / ticks;
        var min = (double)world.MinScreenTime;
        var entry = FindLedgeEntry(world);

        var frame = 0;
        var screenFrame = -1;
        var respawnFrame = -1;
        void OnChanged(DeathState from, DeathState to)
        {
            if (to == DeathState.DeathScreen)
            {
                screenFrame = frame;
            }
            else if (from == DeathState.DeathScreen && to == DeathState.Respawning)
            {
                respawnFrame = frame;
            }
        }

        var respawns = 0;
        Vector3? respawnPosition = null;
        void OnRespawned(Vector3 position)
        {
            respawns++;
            respawnPosition = position;
        }

        world.Death.StateChanged += OnChanged;
        world.PlayerRespawned += OnRespawned;
        var pressedAt = -1;
        var timeAtPress = double.NaN;
        var stateAfterPress = DeathState.Failed;
        var respawnsAfterPress = -1;
        var stateNextFrame = DeathState.Failed;
        var respawnsNextFrame = -1;
        var shownAfterMin = false;
        try
        {
            AssertEntered(await PlaceOnLedgeAndWaitForEntry(world, entry));
            await Drive(world, f =>
            {
                frame = f;
                if (pressedAt >= 0 && f == pressedAt + 1)
                {
                    stateNextFrame = world.Death.State;
                    respawnsNextFrame = respawns;
                }

                if (world.Death.State != DeathState.DeathScreen)
                {
                    return world.Death.State is DeathState.Alive or DeathState.Failed && pressedAt >= 0 && f > pressedAt + 1;
                }

                var time = world.Death.TimeInState;
                if (time >= min + dt && world.DeathScreen.IsShown)
                {
                    shownAfterMin = true;
                }

                if (pressedAt < 0 && ShouldPress(at, time, min, dt))
                {
                    pressedAt = f;
                    timeAtPress = time;
                    Press(kind, pressed: true);
                    stateAfterPress = world.Death.State;
                    respawnsAfterPress = respawns;
                    Press(kind, pressed: false);
                }

                return false;
            }, Mathf.CeilToInt((world.FallTime + world.ScreenTime) * ticks) + 30);

            var onFloor = await WaitUntil(world, () => player.IsOnFloor() && !world.DeathScreen.IsShown, RespawnSettleFrames);
            var boundary = world.MapLoader.Boundary!;
            var trace = new DismissTrace(
                screenFrame,
                pressedAt,
                timeAtPress,
                stateAfterPress,
                respawnsAfterPress,
                shownAfterMin,
                stateNextFrame,
                respawnsNextFrame,
                respawnFrame,
                FramesFor(world.ScreenTime, ticks),
                min,
                world.ScreenTime,
                respawns,
                world.Death.State,
                respawnPosition,
                onFloor,
                player.InputEnabled && world.PlayerCamera!.InputEnabled,
                world.PlayerCamera?.IsCurrent == true,
                !world.DeathScreen.IsShown,
                respawnPosition is { } r && Roadman.Core.Player.SpawnPicker.Contains(boundary.SpawnArea.OpenGround, r.X, r.Z));
            GD.Print(Invariant(
                $"WorldEdgeBreakingTest: Ac16 {kind} press {at}: death screen from frame {screenFrame}, pressed at frame {pressedAt} ({timeAtPress:0.####} s on the screen, MinScreenTime {min}) -> {stateAfterPress}, next frame {stateNextFrame} with {respawnsNextFrame} respawn(s); DeathScreen->Respawning at frame {respawnFrame} ({respawnFrame - screenFrame} frames, ScreenTime is {trace.ScreenFrames}); final {trace.FinalState}, respawns {respawns}"));
            return trace;
        }
        finally
        {
            world.Death.StateChanged -= OnChanged;
            world.PlayerRespawned -= OnRespawned;
            Press(kind, pressed: false);
        }
    }

    private static bool ShouldPress(PressAt at, double time, double min, double dt) => at switch
    {
        PressAt.JustBeforeMinScreenTime => time < min - DeathSequence.BoundaryTolerance && time + dt >= min - DeathSequence.BoundaryTolerance,
        PressAt.MinScreenTime => time >= min - DeathSequence.BoundaryTolerance,
        _ => time >= min + 0.5,
    };

    private static void AssertDismissed(DismissTrace trace)
    {
        AssertInt(trace.PressedAtFrame).OverrideFailureMessage("no press was made").IsGreaterEqual(0);
        AssertFloat((float)trace.TimeAtPress).IsGreaterEqual((float)trace.MinScreenTime - 1e-6f);
        AssertFloat((float)trace.TimeAtPress).IsLess((float)trace.ScreenTime);
        AssertThat(trace.StateAfterPress).OverrideFailureMessage("the press didn't end the death screen").IsEqual(DeathState.Respawning);
        AssertInt(trace.RespawnsAfterPress).OverrideFailureMessage("the respawn ran inside the input callback").IsEqual(0);
        AssertInt(trace.RespawnFrame).IsEqual(trace.PressedAtFrame);
        AssertThat(trace.StateNextFrame).OverrideFailureMessage("not Alive in the next physics frame").IsEqual(DeathState.Alive);
        AssertInt(trace.RespawnsNextFrame).IsEqual(1);
        AssertInt(trace.Respawns).OverrideFailureMessage("PlayerRespawned didn't fire exactly once").IsEqual(1);
        AssertThat(trace.FinalState).IsEqual(DeathState.Alive);
        AssertBool(trace.InOpenGround).OverrideFailureMessage($"respawn {trace.RespawnPosition} is outside spawn_area.open_ground").IsTrue();
        AssertBool(trace.OnFloor).OverrideFailureMessage($"not on the floor with the death screen hidden within {RespawnSettleFrames} physics frames").IsTrue();
        AssertBool(trace.InputEnabled).IsTrue();
        AssertBool(trace.PlayerCameraCurrent).IsTrue();
        AssertBool(trace.ScreenHidden).IsTrue();
    }

    private static void Press(PressKind kind, bool pressed)
    {
        switch (kind)
        {
            case PressKind.Key:
                Inject(new InputEventKey { PhysicalKeycode = DismissKey, Pressed = pressed });
                break;
            case PressKind.GamepadButton:
                Inject(new InputEventJoypadButton { Device = PadDevice, ButtonIndex = DismissButton, Pressed = pressed });
                break;
            default:
                Inject(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed });
                break;
        }
    }

    /// <summary>Feeds <paramref name="event"/> to <see cref="Input.ParseInputEvent"/> and flushes it, so it reaches the nodes' input callbacks now.</summary>
    private static void Inject(InputEvent @event)
    {
        Input.ParseInputEvent(@event);
        Input.FlushBufferedEvents();
    }

    /// <summary>
    /// Criteria 12 and 17: whether every batch instance of item <paramref name="id"/> is hidden
    /// (<see cref="Barriers.HiddenTransform"/>) or, with <paramref name="hidden"/> false, back at its rest transform.
    /// </summary>
    private static bool AllInstances(Barriers barriers, string id, bool hidden) =>
        barriers.InstancesOf(id).All(instance =>
            barriers.InstanceTransformOf(instance.Batch, instance.Index) is { } current
            && current.IsEqualApprox(hidden ? Barriers.HiddenTransform : barriers.RestTransformOf(instance.Batch, instance.Index)!.Value));
}
