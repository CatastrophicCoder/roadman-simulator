using Roadman.Core.Maps;

namespace Roadman.Core.Tools;

/// <summary>
/// The screenshot tool's user arguments (after <c>--</c> on the Godot command line):
/// <c>--screenshot &lt;preset&gt; &lt;out.png&gt; [--presets &lt;camera_presets.json&gt;] [--map &lt;map-id&gt;] [--orthophoto] [--road-graph]</c>.
/// </summary>
/// <param name="Preset">Camera preset name.</param>
/// <param name="OutputPath">PNG to write, as given; resolve it with <see cref="OutputPath.Resolve"/>.</param>
/// <param name="PresetsPath">Optional presets file to use instead of the pack's <c>camera_presets.json</c>.</param>
/// <param name="MapId">
/// Optional map pack to render instead of the one in the project setting; its <c>camera_presets.json</c> is read
/// too (unless <paramref name="PresetsPath"/> is given). A slug, like every map id.
/// </param>
/// <param name="Orthophoto">
/// Render with the orthophoto debug view on, including its attribution overlay. The pack must have orthophoto images.
/// </param>
/// <param name="RoadGraph">
/// Render with the road graph debug view on. The pack must have a readable <c>road_graph.json</c>.
/// </param>
public sealed record ScreenshotArgs(
    string Preset,
    string OutputPath,
    string? PresetsPath,
    string? MapId = null,
    bool Orthophoto = false,
    bool RoadGraph = false)
{
    public const string ScreenshotFlag = "--screenshot";
    public const string PresetsFlag = "--presets";
    public const string MapFlag = "--map";
    public const string OrthophotoFlag = "--orthophoto";
    public const string RoadGraphFlag = "--road-graph";

    private const string Usage =
        $"{ScreenshotFlag} <preset> <out.png> [{PresetsFlag} <file>] [{MapFlag} <map-id>] [{OrthophotoFlag}] [{RoadGraphFlag}]";

    /// <summary>
    /// Parses the user arguments.
    /// </summary>
    /// <returns>
    /// Null when <c>--screenshot</c> isn't given, so the game runs normally. Other user arguments are then left
    /// alone; they may belong to something else (for example a test runner).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// With <c>--screenshot</c>: a value is missing, an argument is repeated or unknown, the output isn't a <c>.png</c>,
    /// or the map id isn't a slug.
    /// </exception>
    public static ScreenshotArgs? Parse(IReadOnlyList<string> userArgs)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        if (!userArgs.Contains(ScreenshotFlag))
        {
            return null;
        }

        string? preset = null;
        string? output = null;
        string? presetsPath = null;
        var seenScreenshot = false;
        string? mapId = null;
        var seenPresets = false;
        var seenMap = false;
        var orthophoto = false;
        var roadGraph = false;

        for (var i = 0; i < userArgs.Count; i++)
        {
            switch (userArgs[i])
            {
                case ScreenshotFlag:
                    if (seenScreenshot)
                    {
                        throw new ArgumentException($"{ScreenshotFlag} is given more than once");
                    }

                    seenScreenshot = true;
                    preset = Value(userArgs, ++i, $"{ScreenshotFlag} needs <preset> <out.png>");
                    output = Value(userArgs, ++i, $"{ScreenshotFlag} needs <preset> <out.png>, the output path is missing");
                    break;
                case PresetsFlag:
                    if (seenPresets)
                    {
                        throw new ArgumentException($"{PresetsFlag} is given more than once");
                    }

                    seenPresets = true;
                    presetsPath = Value(userArgs, ++i, $"{PresetsFlag} needs a file path");
                    break;
                case MapFlag:
                    if (seenMap)
                    {
                        throw new ArgumentException($"{MapFlag} is given more than once");
                    }

                    seenMap = true;
                    mapId = Value(userArgs, ++i, $"{MapFlag} needs a map id");
                    if (!MapPack.IsValidMapId(mapId))
                    {
                        throw new ArgumentException(
                            $"the map id '{mapId}' must be a lowercase slug (letters and digits joined by single '-' or '_')");
                    }

                    break;
                case OrthophotoFlag:
                    if (orthophoto)
                    {
                        throw new ArgumentException($"{OrthophotoFlag} is given more than once");
                    }

                    orthophoto = true;
                    break;
                case RoadGraphFlag:
                    if (roadGraph)
                    {
                        throw new ArgumentException($"{RoadGraphFlag} is given more than once");
                    }

                    roadGraph = true;
                    break;
                default:
                    throw new ArgumentException($"unexpected argument '{userArgs[i]}'; usage: {Usage}");
            }
        }

        if (!output!.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"the output path '{output}' must end in .png");
        }

        return new ScreenshotArgs(preset!, output, presetsPath, mapId, orthophoto, roadGraph);
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

/// <summary>Process exit codes of the screenshot tool, as read by <c>/verify</c>.</summary>
public static class ScreenshotExitCode
{
    /// <summary>The PNG was saved.</summary>
    public const int Saved = 0;

    /// <summary>
    /// Any failure: bad arguments, unknown preset, missing pack, a map that didn't load completely, headless
    /// display, flat-colour image, <c>--orthophoto</c> without orthophoto images in the pack, or
    /// <c>--road-graph</c> without a readable road graph or without the debug view (not a debug build).
    /// </summary>
    public const int Error = 1;

    /// <summary>
    /// A ground-relative preset has no terrain under it although every manifest tile loaded. Reported as N/A; no
    /// PNG is written.
    /// </summary>
    public const int OutsideBuiltArea = 2;

    /// <summary>
    /// Whether the screenshot can go on after the map loader finished. Missing terrain may only mean
    /// <see cref="OutsideBuiltArea"/> when the whole map is there; otherwise it could be a tile that didn't load.
    /// </summary>
    /// <param name="loadedTiles">Tiles the loader instanced.</param>
    /// <param name="manifestTiles">Tiles the manifest lists; null when the manifest wasn't read.</param>
    /// <returns>Null to go on, or <see cref="Error"/> when the load failed or is incomplete.</returns>
    public static int? AfterMapLoad(int loadedTiles, int? manifestTiles) =>
        manifestTiles is { } expected && expected > 0 && loadedTiles == expected ? null : Error;
}
