using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Roadman.Core.Settings;

/// <summary>The result of <see cref="SettingsReader.Read"/>.</summary>
/// <param name="Settings">The settings: the file's valid values, defaults for the rest.</param>
/// <param name="Warnings">One line per problem, each naming the key path; empty for a clean file.</param>
public sealed record SettingsReadResult(GameSettings Settings, IReadOnlyList<string> Warnings);

/// <summary>
/// The <c>snake_case</c> names of the settings' enum values in <c>settings.json</c>.
/// </summary>
public static class SettingsNames
{
    public static IReadOnlyDictionary<string, WindowMode> WindowModes { get; } = new Dictionary<string, WindowMode>(StringComparer.Ordinal)
    {
        ["windowed"] = WindowMode.Windowed,
        ["borderless_fullscreen"] = WindowMode.BorderlessFullscreen,
    };

    public static IReadOnlyDictionary<string, QualityLevel> QualityLevels { get; } = new Dictionary<string, QualityLevel>(StringComparer.Ordinal)
    {
        ["low"] = QualityLevel.Low,
        ["medium"] = QualityLevel.Medium,
        ["high"] = QualityLevel.High,
    };

    public static IReadOnlyDictionary<string, MinimapCorner> MinimapCorners { get; } = new Dictionary<string, MinimapCorner>(StringComparer.Ordinal)
    {
        ["top_left"] = MinimapCorner.TopLeft,
        ["top_right"] = MinimapCorner.TopRight,
        ["bottom_left"] = MinimapCorner.BottomLeft,
        ["bottom_right"] = MinimapCorner.BottomRight,
    };

    /// <summary>The file name of <paramref name="value"/> in <paramref name="names"/>.</summary>
    public static string NameOf<T>(IReadOnlyDictionary<string, T> names, T value)
        where T : struct, Enum =>
        names.First(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key;
}

/// <summary>
/// Reads <c>settings.json</c>, forgiving per key: a value that is missing, of the wrong type, out of range or
/// unknown (an unknown enum name, a window size not in <see cref="WindowSizes.All"/>, a frame cap not in
/// <see cref="SettingsRules.FrameCaps"/>, a value off its step) falls back to its default with one warning naming
/// the key path (<c>display.render_scale</c>), and the other values are kept. An unknown key, or a key given twice
/// (the last one is used), is one warning. A missing group, or one that isn't an object, gives that group's defaults
/// with one warning. A file that isn't JSON, isn't an object, or has a <c>format_version</c> above
/// <see cref="GameSettings.CurrentFormatVersion"/> gives all defaults and one warning. A missing or invalid
/// <c>format_version</c> (not a whole number ≥ 1) is one warning and the file is read as version 1.
/// </summary>
/// <remarks>
/// Sensitivities count as on a step within <see cref="SettingsRules.SensitivityTolerance"/> and are stored snapped
/// (<see cref="SettingsRules.SnapSensitivity"/>). The file shape is shown on <see cref="SettingsWriter"/>.
/// </remarks>
public static class SettingsReader
{
    private const string Source = "settings.json";

    private static readonly string[] RootKeys = ["format_version", "display", "audio", "gameplay"];

    private static readonly string[] DisplayKeys = ["window_mode", "window_size", "render_scale", "quality", "vsync", "frame_cap"];
    private static readonly string[] AudioKeys = ["master_volume", "music_volume", "effects_volume"];
    private static readonly string[] GameplayKeys =
        ["invert_mouse_y", "invert_stick_y", "mouse_sensitivity", "stick_sensitivity", "minimap_corner"];

    /// <param name="json">The file's text.</param>
    /// <param name="usableScreen">The screen's usable area, for the default window size (<see cref="GameSettings.Defaults"/>).</param>
    public static SettingsReadResult Read(string json, WindowSize? usableScreen = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var defaults = GameSettings.Defaults(usableScreen);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new SettingsReadResult(defaults, [$"{Source}: not valid JSON ({ex.Message}); using the defaults"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingsReadResult(defaults, [$"{Source}: the top level isn't a JSON object; using the defaults"]);
            }

            var warnings = new List<string>();
            if (root.TryGetProperty("format_version", out var versionValue)
                && versionValue.ValueKind == JsonValueKind.Number
                && versionValue.TryGetInt32(out var version)
                && version >= 1)
            {
                if (version > GameSettings.CurrentFormatVersion)
                {
                    return new SettingsReadResult(
                        defaults,
                        [Inv($"{Source}: 'format_version' is {version}: saved by a newer version of the game; using the defaults")]);
                }
            }
            else
            {
                warnings.Add(Inv($"{Source}: 'format_version' is missing or not a whole number ≥ 1; reading it as version {GameSettings.CurrentFormatVersion}"));
            }

            var keys = new Keys(root, "", RootKeys, warnings);
            var display = keys.Group("display", DisplayKeys, defaults.Display, ReadDisplay);
            var audio = keys.Group("audio", AudioKeys, defaults.Audio, ReadAudio);
            var gameplay = keys.Group("gameplay", GameplayKeys, defaults.Gameplay, ReadGameplay);
            return new SettingsReadResult(new GameSettings(display, audio, gameplay), warnings);
        }
    }

    private static DisplaySettings ReadDisplay(Keys keys, DisplaySettings d) =>
        new(
            keys.Choice("window_mode", SettingsNames.WindowModes, d.WindowMode),
            keys.Value("window_size", d.WindowSize, WindowSizeText, ParseWindowSize),
            keys.Int("render_scale", d.RenderScale, SettingsRules.IsValidRenderScale, "50..100 in steps of 5"),
            keys.Choice("quality", SettingsNames.QualityLevels, d.Quality),
            keys.Bool("vsync", d.VSync),
            keys.Int("frame_cap", d.FrameCap, SettingsRules.IsValidFrameCap, "0 (off), 30, 60 or 120"));

    private static AudioSettings ReadAudio(Keys keys, AudioSettings d) =>
        new(
            keys.Int("master_volume", d.MasterVolume, SettingsRules.IsValidVolume, "0..100 in steps of 5"),
            keys.Int("music_volume", d.MusicVolume, SettingsRules.IsValidVolume, "0..100 in steps of 5"),
            keys.Int("effects_volume", d.EffectsVolume, SettingsRules.IsValidVolume, "0..100 in steps of 5"));

    private static GameplaySettings ReadGameplay(Keys keys, GameplaySettings d) =>
        new(
            keys.Bool("invert_mouse_y", d.InvertMouseY),
            keys.Bool("invert_stick_y", d.InvertStickY),
            keys.Sensitivity("mouse_sensitivity", d.MouseSensitivity),
            keys.Sensitivity("stick_sensitivity", d.StickSensitivity),
            keys.Choice("minimap_corner", SettingsNames.MinimapCorners, d.MinimapCorner));

    private static string? ParseWindowSize(JsonElement value, out WindowSize size)
    {
        size = default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return "must be an object {\"width\": w, \"height\": h}";
        }

        int? width = null;
        int? height = null;
        foreach (var property in value.EnumerateObject())
        {
            var isInt = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out _);
            switch (property.Name)
            {
                case "width" when width is null && isInt:
                    width = property.Value.GetInt32();
                    break;
                case "height" when height is null && isInt:
                    height = property.Value.GetInt32();
                    break;
                default:
                    return "must be an object with exactly the whole numbers width and height";
            }
        }

        if (width is null || height is null)
        {
            return "must be an object with exactly the whole numbers width and height";
        }

        size = new WindowSize(width.Value, height.Value);
        return WindowSizes.IsKnown(size)
            ? null
            : $"{size} is not one of {string.Join(", ", WindowSizes.All)}";
    }

    private static string WindowSizeText(WindowSize size) => size.ToString();

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>The keys of one object: reads each known key once, warns for problems, unknown and repeated keys.</summary>
    private sealed class Keys
    {
        private readonly JsonElement _element;
        private readonly string _path;
        private readonly List<string> _warnings;

        public Keys(JsonElement element, string path, string[] allowed, List<string> warnings)
        {
            _element = element;
            _path = path;
            _warnings = warnings;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var keyPath = Join(property.Name);
                if (Array.IndexOf(allowed, property.Name) < 0)
                {
                    warnings.Add($"{Source}: '{keyPath}' is not a known setting; ignored");
                }
                else if (!seen.Add(property.Name))
                {
                    warnings.Add($"{Source}: '{keyPath}' is given more than once; using the last one");
                }
            }
        }

        public T Group<T>(string name, string[] allowed, T defaults, Func<Keys, T, T> read)
        {
            if (!_element.TryGetProperty(name, out var value))
            {
                _warnings.Add($"{Source}: '{Join(name)}' is missing; using its defaults");
                return defaults;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                _warnings.Add($"{Source}: '{Join(name)}' must be an object; using its defaults");
                return defaults;
            }

            return read(new Keys(value, Join(name), allowed, _warnings), defaults);
        }

        public T Value<T>(string name, T fallback, Func<T, string> text, ParseValue<T> parse)
        {
            var keyPath = Join(name);
            if (!_element.TryGetProperty(name, out var value))
            {
                _warnings.Add($"{Source}: '{keyPath}' is missing; using the default {text(fallback)}");
                return fallback;
            }

            if (parse(value, out var result) is { } problem)
            {
                _warnings.Add($"{Source}: '{keyPath}' {problem}; using the default {text(fallback)}");
                return fallback;
            }

            return result;
        }

        public bool Bool(string name, bool fallback) =>
            Value(name, fallback, b => b ? "true" : "false", (JsonElement v, out bool r) =>
            {
                r = v.ValueKind == JsonValueKind.True;
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false";
            });

        public int Int(string name, int fallback, Func<int, bool> valid, string range) =>
            Value(name, fallback, i => i.ToString(CultureInfo.InvariantCulture), (JsonElement v, out int r) =>
            {
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out r))
                {
                    r = 0;
                    return "must be a whole number";
                }

                return valid(r) ? null : Inv($"must be {range}, is {r}");
            });

        public double Sensitivity(string name, double fallback) =>
            Value(name, fallback, s => s.ToString("0.00", CultureInfo.InvariantCulture), (JsonElement v, out double r) =>
            {
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out r) || !double.IsFinite(r))
                {
                    r = 0;
                    return "must be a finite number";
                }

                var raw = r;
                r = SettingsRules.SnapSensitivity(raw);
                return SettingsRules.IsValidSensitivity(raw)
                    ? null
                    : Inv($"must be 0.25..3.0 in steps of 0.05, is {raw.ToString("R", CultureInfo.InvariantCulture)}");
            });

        public T Choice<T>(string name, IReadOnlyDictionary<string, T> names, T fallback)
            where T : struct, Enum =>
            Value(name, fallback, f => SettingsNames.NameOf(names, f), (JsonElement v, out T r) =>
            {
                r = default;
                if (v.ValueKind != JsonValueKind.String)
                {
                    return $"must be one of {string.Join(", ", names.Keys)}";
                }

                var text = v.GetString()!;
                return names.TryGetValue(text, out r)
                    ? null
                    : $"'{text}' is not one of {string.Join(", ", names.Keys)}";
            });

        private string Join(string name) => _path.Length == 0 ? name : $"{_path}.{name}";
    }

    private delegate string? ParseValue<T>(JsonElement value, out T result);
}

/// <summary>
/// Writes <c>settings.json</c> (indented, <c>snake_case</c> keys, enums as <c>snake_case</c> strings), readable by
/// <see cref="SettingsReader"/> with no warnings and equal to the input:
/// <code>
/// {
///   "format_version": 1,
///   "display": {
///     "window_mode": "windowed",
///     "window_size": { "width": 1600, "height": 900 },
///     "render_scale": 100,
///     "quality": "medium",
///     "vsync": true,
///     "frame_cap": 0
///   },
///   "audio": { "master_volume": 100, "music_volume": 70, "effects_volume": 80 },
///   "gameplay": {
///     "invert_mouse_y": false,
///     "invert_stick_y": false,
///     "mouse_sensitivity": 1,
///     "stick_sensitivity": 1,
///     "minimap_corner": "bottom_left"
///   }
/// }
/// </code>
/// </summary>
public static class SettingsWriter
{
    private static readonly JsonWriterOptions Options = new() { Indented = true };

    /// <summary>
    /// Writes <paramref name="settings"/>; sensitivities are written snapped to their step
    /// (<see cref="SettingsRules.SnapSensitivity"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A value is outside its rules (the message names the key path).</exception>
    public static string Write(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Check(settings);
        var d = settings.Display;
        var a = settings.Audio;
        var g = settings.Gameplay;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format_version", GameSettings.CurrentFormatVersion);

            writer.WriteStartObject("display");
            writer.WriteString("window_mode", SettingsNames.NameOf(SettingsNames.WindowModes, d.WindowMode));
            writer.WriteStartObject("window_size");
            writer.WriteNumber("width", d.WindowSize.Width);
            writer.WriteNumber("height", d.WindowSize.Height);
            writer.WriteEndObject();
            writer.WriteNumber("render_scale", d.RenderScale);
            writer.WriteString("quality", SettingsNames.NameOf(SettingsNames.QualityLevels, d.Quality));
            writer.WriteBoolean("vsync", d.VSync);
            writer.WriteNumber("frame_cap", d.FrameCap);
            writer.WriteEndObject();

            writer.WriteStartObject("audio");
            writer.WriteNumber("master_volume", a.MasterVolume);
            writer.WriteNumber("music_volume", a.MusicVolume);
            writer.WriteNumber("effects_volume", a.EffectsVolume);
            writer.WriteEndObject();

            writer.WriteStartObject("gameplay");
            writer.WriteBoolean("invert_mouse_y", g.InvertMouseY);
            writer.WriteBoolean("invert_stick_y", g.InvertStickY);
            writer.WriteNumber("mouse_sensitivity", SettingsRules.SnapSensitivity(g.MouseSensitivity));
            writer.WriteNumber("stick_sensitivity", SettingsRules.SnapSensitivity(g.StickSensitivity));
            writer.WriteString("minimap_corner", SettingsNames.NameOf(SettingsNames.MinimapCorners, g.MinimapCorner));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Check(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings.Display, nameof(settings));
        ArgumentNullException.ThrowIfNull(settings.Audio, nameof(settings));
        ArgumentNullException.ThrowIfNull(settings.Gameplay, nameof(settings));
        var d = settings.Display;
        var a = settings.Audio;
        var g = settings.Gameplay;
        Require(Enum.IsDefined(d.WindowMode), "display.window_mode", d.WindowMode);
        Require(WindowSizes.IsKnown(d.WindowSize), "display.window_size", d.WindowSize);
        Require(SettingsRules.IsValidRenderScale(d.RenderScale), "display.render_scale", d.RenderScale);
        Require(Enum.IsDefined(d.Quality), "display.quality", d.Quality);
        Require(SettingsRules.IsValidFrameCap(d.FrameCap), "display.frame_cap", d.FrameCap);
        Require(SettingsRules.IsValidVolume(a.MasterVolume), "audio.master_volume", a.MasterVolume);
        Require(SettingsRules.IsValidVolume(a.MusicVolume), "audio.music_volume", a.MusicVolume);
        Require(SettingsRules.IsValidVolume(a.EffectsVolume), "audio.effects_volume", a.EffectsVolume);
        Require(SettingsRules.IsValidSensitivity(g.MouseSensitivity), "gameplay.mouse_sensitivity", g.MouseSensitivity);
        Require(SettingsRules.IsValidSensitivity(g.StickSensitivity), "gameplay.stick_sensitivity", g.StickSensitivity);
        Require(Enum.IsDefined(g.MinimapCorner), "gameplay.minimap_corner", g.MinimapCorner);
    }

    private static void Require(bool valid, string keyPath, object value)
    {
        if (!valid)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the settings can't be written: '{keyPath}' is invalid ({value})"),
                "settings");
        }
    }
}
