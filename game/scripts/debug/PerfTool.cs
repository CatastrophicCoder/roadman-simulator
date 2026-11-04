using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Tools;
using Roadman.Scenes;
using Directory = System.IO.Directory;
using File = System.IO.File;
using IOException = System.IO.IOException;
using Path = System.IO.Path;

namespace Roadman.Debug;

/// <summary>
/// Measures frame times for the world edge's performance budget (<c>docs/specs/world-edge-game.md</c>, "Performance";
/// criterion 27) and quits:
/// <c>godot --path game [--headless] -- --perf &lt;spawn|roadblock&gt; &lt;out.json&gt; [--map &lt;map-id&gt;] [--seed &lt;n&gt;] [--no-world-edge] [--no-space-sky] [--warmup &lt;frames&gt;] [--frames &lt;n&gt;]</c>.
/// <c>World</c> adds it in debug builds only (<see cref="PerfArgs"/>; <c>.claude/scripts/perf-world-edge.sh</c> runs
/// every combination).
/// <para>
/// After the map loads and the player spawns (and the barriers are built, unless the world edge is off), the player stands
/// still with its input off: at its spawn point (<c>spawn</c>), or on the ground <see cref="RoadblockInset"/> inward of
/// the span midpoint of <see cref="PerfArgs.RoadblockId"/>, facing along the ring with its camera behind it
/// (<c>roadblock</c>). Both spots need that roadblock in <c>boundary.json</c>, so on any other pack (the test area) the run
/// prints <c>N/A: not the full pack (...)</c> and exits <see cref="PerfExitCode.NotApplicable"/>. Then it waits
/// <see cref="PerfArgs.WarmupFrames"/> frames and records <see cref="PerfArgs.Frames"/> frames: the real time between
/// frames (<see cref="Time.GetTicksUsec"/>), the physics steps taken, and in windowed runs the main viewport's measured
/// render time (<see cref="RenderingServer.ViewportSetMeasureRenderTime"/>: CPU and GPU, from the start of the warm-up).
/// The frame cap is off (<see cref="Engine.MaxFps"/> 0) in both modes, and windowed runs turn vsync off.
/// </para>
/// <para>
/// Headless runs must be started with <c>--fixed-fps &lt;physics ticks per second&gt;</c>: every frame then advances the
/// game clock by exactly one physics step and nothing sleeps, so a frame's real time is its process and physics work.
/// The tool checks that the measured frames took exactly one physics step each and fails otherwise. The headless budget
/// figure is <c>mean_frame_ms</c>; the windowed one is <c>mean_render_ms</c> (CPU + GPU), with <c>mean_frame_ms</c>
/// information only. The <see cref="Performance.Monitor.TimeProcess"/> and
/// <see cref="Performance.Monitor.TimePhysicsProcess"/> monitors are recorded as <c>peak_process_ms</c> and
/// <c>peak_physics_ms</c>, information only: Godot updates them about once a second with the peak of that second, so
/// their mean isn't a per-frame cost (<c>peak_monitor_updates</c> counts the measured frames in which
/// <see cref="Performance.Monitor.TimeProcess"/> changed).
/// </para>
/// </summary>
public partial class PerfTool : Node
{
    /// <summary>How far inward of the roadblock's span midpoint the player stands, m.</summary>
    [Export]
    public float RoadblockInset { get; set; } = 4f;

    /// <summary>Physics frames to wait for the player beyond the world's own spawn wait.</summary>
    [Export]
    public int PlayerSpawnMarginFrames { get; set; } = 10;

    /// <summary>Physics frames to wait for the barriers to be built.</summary>
    [Export]
    public int EdgeWaitFrames { get; set; } = 120;

    /// <summary>Physics frames to wait for the placed player to be on the floor.</summary>
    [Export]
    public int PlayerFloorWaitFrames { get; set; } = 120;

    /// <summary>Whether <see cref="Finish"/> quits the tree (the command-line run); tests set it false and read <see cref="ExitCode"/>.</summary>
    [Export]
    public bool QuitOnFinish { get; set; } = true;

    /// <summary>The parsed command line; set before adding the node.</summary>
    public PerfArgs? Request { get; set; }

    /// <summary>The loader of the map measured; set before adding the node and before it loads.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>The world whose player is placed; set before adding the node.</summary>
    public World? World { get; set; }

    /// <summary>The exit code the run finished with (<see cref="PerfExitCode"/>); null while it runs.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Whether the measurement is running (after the warm-up, until the JSON is written).</summary>
    public bool Measuring => _phase == Phase.Measuring;

    /// <summary>The figures of the finished measurement (the JSON's content); null before.</summary>
    public IReadOnlyDictionary<string, object>? Result { get; private set; }

    private enum Phase
    {
        Waiting,
        WarmingUp,
        Measuring,
        Done,
    }

    private Phase _phase = Phase.Waiting;
    private int _frameCount;
    private double _processSum;
    private double _physicsSum;
    private double _lastProcessMonitor = double.NaN;
    private int _monitorUpdates;
    private ulong _physicsFramesAtStart;
    private ulong _physicsSteps;
    private double _frameSum;
    private double _renderCpuSum;
    private double _renderGpuSum;
    private bool _measureRender;
    private ulong _lastTicks;
    private string _outputPath = "";
    private Vector3 _playerPosition;
    private bool _finished;

    public override void _Ready()
    {
        if (Request is null || Loader is null || World is null)
        {
            Finish(PerfExitCode.Error, "PerfTool needs Request, Loader and World set before it is added");
            return;
        }

        if (!DebugBuild.IsDebug)
        {
            Finish(PerfExitCode.Error, $"{PerfArgs.PerfFlag} needs a debug build");
            return;
        }

        try
        {
            _outputPath = OutputPath.Resolve(Request.OutputPath, OS.GetEnvironment("PWD"));
        }
        catch (ArgumentException ex)
        {
            Finish(PerfExitCode.Error, ex.Message);
            return;
        }

        Loader.MapLoaded += OnMapLoaded;
        Loader.MapLoadFailed += OnMapLoadFailed;
    }

    public override void _ExitTree()
    {
        if (Loader is not null && IsInstanceValid(Loader))
        {
            Loader.MapLoaded -= OnMapLoaded;
            Loader.MapLoadFailed -= OnMapLoadFailed;
        }
    }

    public override void _Process(double delta)
    {
        switch (_phase)
        {
            case Phase.WarmingUp:
                if (++_frameCount < Request!.WarmupFrames)
                {
                    return;
                }

                StartMeasuring();
                return;
            case Phase.Measuring:
                var now = Time.GetTicksUsec();
                _frameSum += (now - _lastTicks) / 1000.0;
                _lastTicks = now;
                var processMonitor = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                if (!processMonitor.Equals(_lastProcessMonitor))
                {
                    _monitorUpdates++;
                    _lastProcessMonitor = processMonitor;
                }

                _processSum += processMonitor;
                _physicsSum += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
                if (_measureRender)
                {
                    var viewport = GetViewport().GetViewportRid();
                    _renderCpuSum += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
                    _renderGpuSum += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                }
                if (++_frameCount >= Request!.Frames)
                {
                    _phase = Phase.Done;
                    _physicsSteps = Engine.GetPhysicsFrames() - _physicsFramesAtStart;
                    WriteResult();
                }

                return;
            default:
                return;
        }
    }

    private void OnMapLoadFailed(string message) => Finish(PerfExitCode.Error, $"map didn't load: {message}");

    private void OnMapLoaded(int tileCount)
    {
        var manifestTiles = Loader!.Manifest?.Tiles.Count;
        if (ScreenshotExitCode.AfterMapLoad(tileCount, manifestTiles) is not null)
        {
            Finish(
                PerfExitCode.Error,
                $"map didn't load completely: {tileCount} of {manifestTiles?.ToString() ?? "?"} tiles from {Loader.PackPath}; {MapPack.RegenHint(Loader.ResolvedMapId)}");
            return;
        }

        PrepareAsync();
    }

    private async void PrepareAsync()
    {
        var loader = Loader!;
        var world = World!;
        var request = Request!;
        if (loader.Boundary is not { } boundary)
        {
            Finish(PerfExitCode.Error, $"no readable {loader.BoundaryPath}; {MapPack.RegenHint(loader.ResolvedMapId)}");
            return;
        }

        var roadblock = boundary.Roadblocks.FirstOrDefault(r => r.Id == PerfArgs.RoadblockId);
        if (roadblock is null)
        {
            GD.Print($"N/A: not the full pack (no {PerfArgs.RoadblockId} in {loader.BoundaryPath})");
            Finish(PerfExitCode.NotApplicable, null);
            return;
        }

        var tree = GetTree();
        var maxSpawnFrames = world.SpawnPhysicsWaitFrames + PlayerSpawnMarginFrames;
        for (var frame = 0; !HasPlayer(world) && world.LastSpawnFailure is null && frame < maxSpawnFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        if (!HasPlayer(world))
        {
            var reason = world.LastSpawnFailure ?? FormattableString.Invariant($"no spawn within {maxSpawnFrames} physics frames");
            Finish(PerfExitCode.Error, $"the player didn't spawn ({reason}); {MapPack.RegenHint(loader.ResolvedMapId)}");
            return;
        }

        if (request.WorldEdge && !await loader.WaitForWorldEdgeAsync(EdgeWaitFrames))
        {
            if (!_finished)
            {
                Finish(PerfExitCode.Error, $"the barriers weren't built within {EdgeWaitFrames} physics frames");
            }

            return;
        }

        if (_finished || !IsInsideTree() || !HasPlayer(world))
        {
            if (!_finished)
            {
                Finish(PerfExitCode.Error, "the player was freed before the measurement");
            }

            return;
        }

        var player = world.Player!;
        var camera = world.PlayerCamera!;
        player.InputEnabled = false;
        camera.InputEnabled = false;
        if (request.Spot == PerfSpot.Roadblock && !PlaceAtRoadblock(roadblock, player, camera))
        {
            return;
        }

        for (var frame = 0; frame < PlayerFloorWaitFrames && !player.IsOnFloor(); frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return;
            }

            if (!IsInstanceValid(player))
            {
                Finish(PerfExitCode.Error, "the player was freed before the measurement");
                return;
            }
        }

        if (!player.IsOnFloor())
        {
            Finish(PerfExitCode.Error, $"the player isn't on the floor after {PlayerFloorWaitFrames} physics frames");
            return;
        }

        _playerPosition = player.GlobalPosition;
        Engine.MaxFps = 0;
        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            _measureRender = true;
        }

        GD.Print(FormattableString.Invariant(
            $"Perf: {SpotName(request.Spot)} at ({_playerPosition.X:0.###}, {_playerPosition.Y:0.###}, {_playerPosition.Z:0.###}), world edge {(request.WorldEdge ? "on" : "off")}, space sky {(request.SpaceSky ? "on" : "off")}; {request.WarmupFrames} warm-up and {request.Frames} measured frames"));
        _frameCount = 0;
        if (request.WarmupFrames > 0)
        {
            _phase = Phase.WarmingUp;
        }
        else
        {
            StartMeasuring();
        }
    }

    /// <summary>Resets the sums; the next <see cref="PerfArgs.Frames"/> process frames are measured from now.</summary>
    private void StartMeasuring()
    {
        _phase = Phase.Measuring;
        _frameCount = 0;
        _processSum = 0;
        _physicsSum = 0;
        _frameSum = 0;
        _renderCpuSum = 0;
        _renderGpuSum = 0;
        _monitorUpdates = 0;
        _lastProcessMonitor = double.NaN;
        _physicsFramesAtStart = Engine.GetPhysicsFrames();
        _lastTicks = Time.GetTicksUsec();
    }

    /// <summary>
    /// Puts the player on the ground <see cref="RoadblockInset"/> inward of the roadblock's span midpoint, facing along
    /// the span (the ring's tangent there), with its camera behind it.
    /// </summary>
    private bool PlaceAtRoadblock(Roadblock roadblock, OnFoot.Player player, OnFoot.PlayerCamera camera)
    {
        var outward = new Vector3((float)roadblock.Outward.X, 0f, (float)roadblock.Outward.Z);
        outward = outward.LengthSquared() > 0f ? outward.Normalized() : Vector3.Zero;
        var x = (float)roadblock.Position.X - (outward.X * RoadblockInset);
        var z = (float)roadblock.Position.Z - (outward.Z * RoadblockInset);
        if (Loader!.GroundOrRoadHitAt(x, z) is not { } ground)
        {
            Finish(
                PerfExitCode.Error,
                FormattableString.Invariant($"no terrain or road at x {x:0.###}, z {z:0.###} beside {PerfArgs.RoadblockId}"));
            return false;
        }

        var tangent = new Vector3((float)(roadblock.End.X - roadblock.Start.X), 0f, (float)(roadblock.End.Z - roadblock.Start.Z));
        // Godot's yaw 0 looks along −Z: forward = (−sin yaw, 0, −cos yaw).
        var facing = tangent.LengthSquared() > 1e-6f ? Mathf.Atan2(-tangent.X, -tangent.Z) : camera.Yaw;
        player.PlaceAt(ground.Position, facing);
        camera.PlaceBehind(facing);
        return true;
    }

    private void WriteResult()
    {
        var request = Request!;
        var loader = Loader!;
        var headless = DisplayServer.GetName() == "headless";
        if (headless && _physicsSteps != (ulong)_frameCount)
        {
            // Without --fixed-fps the frames aren't one physics step each, so the frame time isn't the per-step work.
            Finish(
                PerfExitCode.Error,
                FormattableString.Invariant(
                    $"headless runs need --fixed-fps {Engine.PhysicsTicksPerSecond} (one physics step per frame), but the {_frameCount} measured frames took {_physicsSteps} physics steps"));
            return;
        }

        var frames = Math.Max(1, _frameCount);
        var result = new Dictionary<string, object>
        {
            ["spot"] = SpotName(request.Spot),
            ["map_id"] = loader.ResolvedMapId,
            ["headless"] = headless,
            ["world_edge"] = request.WorldEdge,
            ["space_sky"] = request.SpaceSky,
            ["warmup_frames"] = request.WarmupFrames,
            ["frames"] = _frameCount,
            ["physics_steps"] = _physicsSteps,
            ["peak_process_ms"] = _processSum / frames,
            ["peak_physics_ms"] = _physicsSum / frames,
            ["peak_monitor_updates"] = _monitorUpdates,
            ["mean_frame_ms"] = _frameSum / frames,
            ["render_measured"] = _measureRender,
            ["mean_render_cpu_ms"] = _renderCpuSum / frames,
            ["mean_render_gpu_ms"] = _renderGpuSum / frames,
            ["mean_render_ms"] = (_renderCpuSum + _renderGpuSum) / frames,
            ["max_fps"] = Engine.MaxFps,
            ["physics_ticks_per_second"] = Engine.PhysicsTicksPerSecond,
            ["barrier_items"] = loader.Barriers?.ItemCount ?? 0,
            ["barrier_pieces"] = loader.Barriers?.PieceCount ?? 0,
            ["void_prisms"] = loader.VoidTrigger?.ShapeCount ?? 0,
            ["player_position"] = new[] { _playerPosition.X, _playerPosition.Y, _playerPosition.Z },
        };
        Result = result;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
            File.WriteAllText(_outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Finish(PerfExitCode.Error, $"can't write {_outputPath}: {ex.Message}");
            return;
        }

        GD.Print(FormattableString.Invariant(
            $"Perf: wrote {_outputPath}: frame {(double)result["mean_frame_ms"]:0.###} ms, {_physicsSteps} physics steps, render {(double)result["mean_render_ms"]:0.###} ms over {_frameCount} frames"));
        Finish(PerfExitCode.Written, null);
    }

    private static string SpotName(PerfSpot spot) => spot == PerfSpot.Roadblock ? PerfArgs.RoadblockValue : PerfArgs.SpawnValue;

    private static bool HasPlayer(World world) =>
        world.Player is { } player && IsInstanceValid(player)
        && world.PlayerCamera is { } camera && IsInstanceValid(camera);

    private void Finish(int exitCode, string? error)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _phase = Phase.Done;
        ExitCode = exitCode;
        if (error is not null)
        {
            GD.PushError($"Perf: {error}");
        }

        if (QuitOnFinish)
        {
            GetTree().Quit(exitCode);
        }
    }
}
