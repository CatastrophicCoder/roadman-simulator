using System.Globalization;
using Roadman.Core.Maps;

namespace Roadman.Core.Tools;

/// <summary>Where the performance measurement stands the player.</summary>
public enum PerfSpot
{
    /// <summary><c>spawn</c>: where the player spawned.</summary>
    Spawn,

    /// <summary><c>roadblock</c>: beside road 52's north roadblock (<see cref="PerfArgs.RoadblockId"/>), looking along the fence.</summary>
    Roadblock,
}

/// <summary>
/// The performance tool's user arguments (after <c>--</c> on the Godot command line; <c>docs/specs/world-edge-game.md</c>,
/// "Performance"):
/// <c>--perf &lt;spawn|roadblock&gt; &lt;out.json&gt; [--map &lt;map-id&gt;] [--seed &lt;n&gt;] [--no-world-edge] [--no-space-sky] [--no-minimap] [--warmup &lt;frames&gt;] [--frames &lt;n&gt;]</c>.
/// <c>--seed</c> belongs to <see cref="SeedArg"/>; here its value is only checked and skipped.
/// </summary>
/// <param name="Spot">Where the player stands.</param>
/// <param name="OutputPath">JSON file to write, as given; resolve it with <see cref="OutputPath.Resolve"/>.</param>
/// <param name="MapId">Optional map pack instead of the project setting's; a slug.</param>
/// <param name="WorldEdge">False with <c>--no-world-edge</c>: no barriers, void trigger or edge geometry, and the plain sky.</param>
/// <param name="SpaceSky">False with <c>--no-world-edge</c> or <c>--no-space-sky</c>: the plain procedural day sky.</param>
/// <param name="WarmupFrames">Frames before the measurement starts.</param>
/// <param name="Frames">Frames measured.</param>
/// <param name="Minimap">False with <c>--no-minimap</c>: no minimap is added (<c>docs/specs/minimap.md</c>, "Godot").</param>
public sealed record PerfArgs(
    PerfSpot Spot,
    string OutputPath,
    string? MapId = null,
    bool WorldEdge = true,
    bool SpaceSky = true,
    int WarmupFrames = PerfArgs.DefaultWarmupFrames,
    int Frames = PerfArgs.DefaultFrames,
    bool Minimap = true)
{
    public const string PerfFlag = "--perf";
    public const string MapFlag = "--map";
    public const string NoWorldEdgeFlag = "--no-world-edge";
    public const string NoSpaceSkyFlag = "--no-space-sky";
    public const string NoMinimapFlag = "--no-minimap";
    public const string WarmupFlag = "--warmup";
    public const string FramesFlag = "--frames";
    public const string SpawnValue = "spawn";
    public const string RoadblockValue = "roadblock";

    /// <summary>Frames of warm-up by default.</summary>
    public const int DefaultWarmupFrames = 120;

    /// <summary>Frames measured by default (the spec's 600).</summary>
    public const int DefaultFrames = 600;

    /// <summary>The roadblock of the <c>roadblock</c> spot: road 52's north roadblock, the <c>world-edge</c> preset's.</summary>
    public const string RoadblockId = "roadblock:cut:way/1445392542:0";

    private const string Usage =
        $"{PerfFlag} <{SpawnValue}|{RoadblockValue}> <out.json> [{MapFlag} <map-id>] [{SeedArg.Flag} <n>] [{NoWorldEdgeFlag}] [{NoSpaceSkyFlag}] [{NoMinimapFlag}] [{WarmupFlag} <frames>] [{FramesFlag} <n>]";

    /// <summary>Parses the user arguments.</summary>
    /// <returns>Null when <c>--perf</c> isn't given; other user arguments are then left alone.</returns>
    /// <exception cref="ArgumentException">
    /// With <c>--perf</c>: a value is missing, an argument is repeated or unknown, the spot isn't <c>spawn</c> or
    /// <c>roadblock</c>, the output isn't a <c>.json</c>, the map id isn't a slug, a frame count isn't a whole number
    /// (at least 0 for <c>--warmup</c>, at least 1 for <c>--frames</c>), or the <c>--seed</c> value isn't an integer.
    /// </exception>
    public static PerfArgs? Parse(IReadOnlyList<string> userArgs)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        if (!userArgs.Contains(PerfFlag))
        {
            return null;
        }

        PerfSpot? spot = null;
        string? output = null;
        string? mapId = null;
        var noWorldEdge = false;
        var noSpaceSky = false;
        var noMinimap = false;
        int? warmup = null;
        int? frames = null;
        var seenSeed = false;

        for (var i = 0; i < userArgs.Count; i++)
        {
            switch (userArgs[i])
            {
                case PerfFlag:
                    if (spot is not null)
                    {
                        throw new ArgumentException($"{PerfFlag} is given more than once");
                    }

                    var spotText = Value(userArgs, ++i, $"{PerfFlag} needs <{SpawnValue}|{RoadblockValue}> <out.json>");
                    spot = spotText switch
                    {
                        SpawnValue => PerfSpot.Spawn,
                        RoadblockValue => PerfSpot.Roadblock,
                        _ => throw new ArgumentException($"{PerfFlag} needs {SpawnValue} or {RoadblockValue}, got '{spotText}'"),
                    };
                    output = Value(userArgs, ++i, $"{PerfFlag} needs <{SpawnValue}|{RoadblockValue}> <out.json>, the output path is missing");
                    break;
                case MapFlag:
                    if (mapId is not null)
                    {
                        throw new ArgumentException($"{MapFlag} is given more than once");
                    }

                    mapId = Value(userArgs, ++i, $"{MapFlag} needs a map id");
                    if (!MapPack.IsValidMapId(mapId))
                    {
                        throw new ArgumentException(
                            $"the map id '{mapId}' must be a lowercase slug (letters and digits joined by single '-' or '_')");
                    }

                    break;
                case NoWorldEdgeFlag:
                    if (noWorldEdge)
                    {
                        throw new ArgumentException($"{NoWorldEdgeFlag} is given more than once");
                    }

                    noWorldEdge = true;
                    break;
                case NoSpaceSkyFlag:
                    if (noSpaceSky)
                    {
                        throw new ArgumentException($"{NoSpaceSkyFlag} is given more than once");
                    }

                    noSpaceSky = true;
                    break;
                case NoMinimapFlag:
                    if (noMinimap)
                    {
                        throw new ArgumentException($"{NoMinimapFlag} is given more than once");
                    }

                    noMinimap = true;
                    break;
                case WarmupFlag:
                    if (warmup is not null)
                    {
                        throw new ArgumentException($"{WarmupFlag} is given more than once");
                    }

                    warmup = Count(userArgs, ++i, WarmupFlag, 0);
                    break;
                case FramesFlag:
                    if (frames is not null)
                    {
                        throw new ArgumentException($"{FramesFlag} is given more than once");
                    }

                    frames = Count(userArgs, ++i, FramesFlag, 1);
                    break;
                case SeedArg.Flag:
                    if (seenSeed)
                    {
                        throw new ArgumentException($"{SeedArg.Flag} is given more than once");
                    }

                    seenSeed = true;
                    SeedArg.ParseValue(userArgs, ++i);
                    break;
                default:
                    throw new ArgumentException($"unexpected argument '{userArgs[i]}'; usage: {Usage}");
            }
        }

        if (!output!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"the output path '{output}' must end in .json");
        }

        return new PerfArgs(
            spot!.Value,
            output,
            mapId,
            WorldEdge: !noWorldEdge,
            SpaceSky: !noWorldEdge && !noSpaceSky,
            WarmupFrames: warmup ?? DefaultWarmupFrames,
            Frames: frames ?? DefaultFrames,
            Minimap: !noMinimap);
    }

    private static int Count(IReadOnlyList<string> args, int index, string flag, int minimum)
    {
        var text = Value(args, index, $"{flag} needs a whole number");
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"{flag} needs a whole number of at least {minimum}, got '{text}'"));
        }

        return value;
    }

    private static string Value(IReadOnlyList<string> args, int index, string error)
    {
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(error);
        }

        return args[index];
    }
}

/// <summary>Process exit codes of the performance tool.</summary>
public static class PerfExitCode
{
    /// <summary>The JSON was written.</summary>
    public const int Written = 0;

    /// <summary>Any failure: bad arguments, not a debug build, a map that didn't load, no player, no ground.</summary>
    public const int Error = 1;

    /// <summary>Not the full pack: the boundary has no <see cref="PerfArgs.RoadblockId"/>. Reported as N/A.</summary>
    public const int NotApplicable = 2;
}
