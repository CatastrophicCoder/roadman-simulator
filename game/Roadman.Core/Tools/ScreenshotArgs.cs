using Roadman.Core.Maps;

namespace Roadman.Core.Tools;

/// <summary>
/// The screenshot tool's user arguments (after <c>--</c> on the Godot command line):
/// <c>--screenshot &lt;preset&gt; &lt;out.png&gt; [--presets &lt;camera_presets.json&gt;] [--map &lt;map-id&gt;] [--orthophoto] [--road-graph] [--inspect] [--player] [--death &lt;fall|screen&gt;] [--menu &lt;main|pause|settings|slots&gt;]</c>.
/// <c>--seed &lt;n&gt;</c> may stand among them; it belongs to <see cref="SeedArg"/>, which reads it, and is skipped here
/// (its value is still checked and a repeat is rejected, so a bad seed is an argument error of the screenshot run too).
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
/// <param name="Inspect">
/// Render with the place inspector on, its centre ray through the preset's look-at point. Debug builds only; the pack
/// must have readable <c>buildings.json</c> and <c>pois.json</c>.
/// </param>
/// <param name="Player">
/// Render the player in the third-person view: placed on the ground at the preset's look-at x/z, facing away from the
/// preset's camera, seen through its player camera. Debug builds only; can't be combined with <paramref name="Inspect"/>.
/// </param>
/// <param name="Death">
/// Render a stage of the death sequence (<c>docs/specs/world-edge-game.md</c>, "Screenshots"): the player is put in the
/// void beside the ring point nearest the preset's look-at, and the image is the death camera's frame at the end of its
/// pull-back (<see cref="DeathShot.Fall"/>) or the faded-in death screen (<see cref="DeathShot.Screen"/>). Debug builds
/// only; can't be combined with <paramref name="Player"/> or <paramref name="Inspect"/>. Null: no death.
/// </param>
/// <param name="Menu">
/// Render a menu over the preset's view (<c>docs/specs/menu-settings-save.md</c>, "Screenshots"): the main menu, the
/// pause menu, the settings view's Display tab or the slot list in load mode with sample saves. Debug builds only;
/// can't be combined with <paramref name="Player"/>, <paramref name="Inspect"/>, <paramref name="Death"/>,
/// <paramref name="Orthophoto"/> or <paramref name="RoadGraph"/>. Null: no menu.
/// </param>
public sealed record ScreenshotArgs(
    string Preset,
    string OutputPath,
    string? PresetsPath,
    string? MapId = null,
    bool Orthophoto = false,
    bool RoadGraph = false,
    bool Inspect = false,
    bool Player = false,
    DeathShot? Death = null,
    MenuShot? Menu = null)
{
    public const string ScreenshotFlag = "--screenshot";
    public const string PresetsFlag = "--presets";
    public const string MapFlag = "--map";
    public const string OrthophotoFlag = "--orthophoto";
    public const string RoadGraphFlag = "--road-graph";
    public const string InspectFlag = "--inspect";
    public const string PlayerFlag = "--player";
    public const string DeathFlag = "--death";
    public const string DeathFallValue = "fall";
    public const string DeathScreenValue = "screen";
    public const string MenuFlag = "--menu";
    public const string MenuMainValue = "main";
    public const string MenuPauseValue = "pause";
    public const string MenuSettingsValue = "settings";
    public const string MenuSlotsValue = "slots";

    private const string Usage =
        $"{ScreenshotFlag} <preset> <out.png> [{PresetsFlag} <file>] [{MapFlag} <map-id>] [{OrthophotoFlag}] [{RoadGraphFlag}] [{InspectFlag}] [{PlayerFlag}] [{DeathFlag} <{DeathFallValue}|{DeathScreenValue}>] [{MenuFlag} <{MenuValues}>] [{SeedArg.Flag} <n>]";

    private const string MenuValues = $"{MenuMainValue}|{MenuPauseValue}|{MenuSettingsValue}|{MenuSlotsValue}";

    /// <summary>
    /// Parses the user arguments.
    /// </summary>
    /// <returns>
    /// Null when <c>--screenshot</c> isn't given, so the game runs normally. Other user arguments are then left
    /// alone; they may belong to something else (for example a test runner).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// With <c>--screenshot</c>: a value is missing, an argument is repeated or unknown, the output isn't a <c>.png</c>,
    /// the map id isn't a slug, the <c>--seed</c> value isn't an integer, the <c>--death</c> value isn't <c>fall</c> or
    /// <c>screen</c>, <c>--player</c> is given with <c>--inspect</c>, or <c>--death</c> with <c>--player</c> or
    /// <c>--inspect</c>, <c>--menu</c> isn't given one of <c>main</c>, <c>pause</c>, <c>settings</c> or <c>slots</c>, or
    /// <c>--menu</c> is given with <c>--player</c>, <c>--inspect</c>, <c>--death</c>, <c>--orthophoto</c> or
    /// <c>--road-graph</c>.
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
        var inspect = false;
        var player = false;
        DeathShot? death = null;
        MenuShot? menu = null;
        var seenSeed = false;

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
                case InspectFlag:
                    if (inspect)
                    {
                        throw new ArgumentException($"{InspectFlag} is given more than once");
                    }

                    inspect = true;
                    break;
                case PlayerFlag:
                    if (player)
                    {
                        throw new ArgumentException($"{PlayerFlag} is given more than once");
                    }

                    player = true;
                    break;
                case DeathFlag:
                    if (death is not null)
                    {
                        throw new ArgumentException($"{DeathFlag} is given more than once");
                    }

                    var value = Value(userArgs, ++i, $"{DeathFlag} needs {DeathFallValue} or {DeathScreenValue}");
                    death = value switch
                    {
                        DeathFallValue => DeathShot.Fall,
                        DeathScreenValue => DeathShot.Screen,
                        _ => throw new ArgumentException(
                            $"{DeathFlag} needs {DeathFallValue} or {DeathScreenValue}, got '{value}'"),
                    };
                    break;
                case MenuFlag:
                    if (menu is not null)
                    {
                        throw new ArgumentException($"{MenuFlag} is given more than once");
                    }

                    var menuValue = Value(userArgs, ++i, $"{MenuFlag} needs {MenuValues}");
                    menu = ParseMenu(menuValue)
                        ?? throw new ArgumentException($"{MenuFlag} needs {MenuValues}, got '{menuValue}'");
                    break;
                case SeedArg.Flag:
                    // Read by SeedArg.Parse; here a repeat is rejected and the value is only checked and skipped.
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

        if (!output!.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"the output path '{output}' must end in .png");
        }

        if (player && inspect)
        {
            // The inspector aims the screen centre at the look-at point; the player view aims it past the player.
            throw new ArgumentException($"{PlayerFlag} can't be combined with {InspectFlag}");
        }

        if (death is not null && (player || inspect))
        {
            // The death shots place the player themselves and photograph the death camera or the death screen.
            throw new ArgumentException($"{DeathFlag} can't be combined with {(player ? PlayerFlag : InspectFlag)}");
        }

        if (menu is not null)
        {
            // A menu covers the view; the other debug flags photograph something the menu would hide.
            var other = player ? PlayerFlag
                : inspect ? InspectFlag
                : death is not null ? DeathFlag
                : orthophoto ? OrthophotoFlag
                : roadGraph ? RoadGraphFlag
                : null;
            if (other is not null)
            {
                throw new ArgumentException($"{MenuFlag} can't be combined with {other}");
            }
        }

        return new ScreenshotArgs(preset!, output, presetsPath, mapId, orthophoto, roadGraph, inspect, player, death, menu);
    }

    /// <summary>The <see cref="MenuShot"/> of a <c>--menu</c> value; null when it isn't one.</summary>
    public static MenuShot? ParseMenu(string value) =>
        value switch
        {
            MenuMainValue => MenuShot.Main,
            MenuPauseValue => MenuShot.Pause,
            MenuSettingsValue => MenuShot.Settings,
            MenuSlotsValue => MenuShot.Slots,
            _ => null,
        };

    /// <summary>The <c>--menu</c> value of <paramref name="menu"/>.</summary>
    public static string MenuValue(MenuShot menu) =>
        menu switch
        {
            MenuShot.Main => MenuMainValue,
            MenuShot.Pause => MenuPauseValue,
            MenuShot.Settings => MenuSettingsValue,
            MenuShot.Slots => MenuSlotsValue,
            _ => throw new ArgumentOutOfRangeException(nameof(menu), menu, "not a menu shot"),
        };

    private static string Value(IReadOnlyList<string> args, int index, string error)
    {
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(error);
        }

        return args[index];
    }
}

/// <summary>The stage of the death sequence a <c>--death</c> screenshot shows.</summary>
public enum DeathShot
{
    /// <summary><c>fall</c>: the death camera's frame at the end of its pull-back.</summary>
    Fall,

    /// <summary><c>screen</c>: the death screen, faded in.</summary>
    Screen,
}

/// <summary>The menu a <c>--menu</c> screenshot shows.</summary>
public enum MenuShot
{
    /// <summary><c>main</c>: the main menu on its plain background.</summary>
    Main,

    /// <summary><c>pause</c>: the pause menu over the paused game.</summary>
    Pause,

    /// <summary><c>settings</c>: the settings view's Display tab, opened from the pause menu.</summary>
    Settings,

    /// <summary><c>slots</c>: the slot list in load mode with sample saves, opened from the pause menu.</summary>
    Slots,
}

/// <summary>Process exit codes of the screenshot tool, as read by <c>/verify</c>.</summary>
public static class ScreenshotExitCode
{
    /// <summary>The PNG was saved.</summary>
    public const int Saved = 0;

    /// <summary>
    /// Any failure: bad arguments, unknown preset, missing pack, a map that didn't load completely, headless
    /// display, flat-colour image, <c>--orthophoto</c> without orthophoto images in the pack, or
    /// <c>--road-graph</c> without a readable road graph or without the debug view (not a debug build), or
    /// <c>--inspect</c> without place data or without the inspector (not a debug build), or <c>--player</c> outside a
    /// debug build, with <c>--inspect</c>, or when the player didn't spawn or reach the ground, or <c>--death</c> outside a
    /// debug build, with <c>--player</c> or <c>--inspect</c>, without a spawned player, without world-edge geometry, or
    /// when the death sequence doesn't reach the stage to capture, or <c>--menu</c> outside a debug build, with another
    /// debug flag, or without <c>Main</c>.
    /// </summary>
    public const int Error = 1;

    /// <summary>
    /// Outside the built area although every manifest tile loaded: a downward ray at the preset's look-at x/z hits
    /// neither the terrain nor the world-edge cliff, a ground-relative preset has no terrain under its camera, or the
    /// <c>--inspect</c> centre ray hits nothing. Reported as N/A; no PNG is written.
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
