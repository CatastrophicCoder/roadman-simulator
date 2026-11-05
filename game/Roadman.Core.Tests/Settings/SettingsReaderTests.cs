using System.Text.Json.Nodes;
using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="SettingsReader"/>, <see cref="SettingsWriter"/> and the defaults of <see cref="GameSettings"/>:
/// <c>docs/specs/menu-settings-save.md</c>, acceptance criterion 6.
/// </summary>
public class SettingsReaderTests
{
    // Every value differs from its default, so a fallback to the default is visible.
    private const string FullFile = """
        {
          "format_version": 1,
          "display": {
            "window_mode": "borderless_fullscreen",
            "window_size": { "width": 1920, "height": 1080 },
            "render_scale": 75,
            "quality": "high",
            "vsync": false,
            "frame_cap": 60
          },
          "audio": { "master_volume": 55, "music_volume": 0, "effects_volume": 100 },
          "gameplay": {
            "invert_mouse_y": true,
            "invert_stick_y": true,
            "mouse_sensitivity": 0.25,
            "stick_sensitivity": 2.35,
            "minimap_corner": "top_right"
          }
        }
        """;

    private static readonly GameSettings Full = new(
        new DisplaySettings(WindowMode.BorderlessFullscreen, new WindowSize(1920, 1080), 75, QualityLevel.High, false, 60),
        new AudioSettings(55, 0, 100),
        new GameplaySettings(true, true, 0.25, 2.35, MinimapCorner.TopRight));

    // The value each key falls back to: the spec's default for that key, the rest as in Full.
    private static readonly Dictionary<string, Func<GameSettings, GameSettings>> FallBack = new()
    {
        ["display.window_mode"] = s => s with { Display = s.Display with { WindowMode = WindowMode.Windowed } },
        ["display.window_size"] = s => s with { Display = s.Display with { WindowSize = new WindowSize(1600, 900) } },
        ["display.render_scale"] = s => s with { Display = s.Display with { RenderScale = 100 } },
        ["display.quality"] = s => s with { Display = s.Display with { Quality = QualityLevel.Medium } },
        ["display.vsync"] = s => s with { Display = s.Display with { VSync = true } },
        ["display.frame_cap"] = s => s with { Display = s.Display with { FrameCap = 0 } },
        ["audio.master_volume"] = s => s with { Audio = s.Audio with { MasterVolume = 100 } },
        ["audio.music_volume"] = s => s with { Audio = s.Audio with { MusicVolume = 70 } },
        ["audio.effects_volume"] = s => s with { Audio = s.Audio with { EffectsVolume = 80 } },
        ["gameplay.invert_mouse_y"] = s => s with { Gameplay = s.Gameplay with { InvertMouseY = false } },
        ["gameplay.invert_stick_y"] = s => s with { Gameplay = s.Gameplay with { InvertStickY = false } },
        ["gameplay.mouse_sensitivity"] = s => s with { Gameplay = s.Gameplay with { MouseSensitivity = 1.0 } },
        ["gameplay.stick_sensitivity"] = s => s with { Gameplay = s.Gameplay with { StickSensitivity = 1.0 } },
        ["gameplay.minimap_corner"] = s => s with { Gameplay = s.Gameplay with { MinimapCorner = MinimapCorner.BottomLeft } },
    };

    [Fact]
    public void Defaults_AreTheSpecTable()
    {
        var d = GameSettings.Default;

        Assert.Equal(WindowMode.Windowed, d.Display.WindowMode);
        Assert.Equal(new WindowSize(1600, 900), d.Display.WindowSize);
        Assert.Equal(100, d.Display.RenderScale);
        Assert.Equal(QualityLevel.Medium, d.Display.Quality);
        Assert.True(d.Display.VSync);
        Assert.Equal(0, d.Display.FrameCap);
        Assert.Equal(100, d.Audio.MasterVolume);
        Assert.Equal(70, d.Audio.MusicVolume);
        Assert.Equal(80, d.Audio.EffectsVolume);
        Assert.False(d.Gameplay.InvertMouseY);
        Assert.False(d.Gameplay.InvertStickY);
        Assert.Equal(1.0, d.Gameplay.MouseSensitivity);
        Assert.Equal(1.0, d.Gameplay.StickSensitivity);
        Assert.Equal(MinimapCorner.BottomLeft, d.Gameplay.MinimapCorner);
        Assert.Equal(1, d.FormatVersion);
    }

    [Theory]
    [InlineData(1920, 1080, 1600, 900)]
    [InlineData(1600, 900, 1600, 900)]
    [InlineData(1440, 875, 1280, 720)]
    [InlineData(1024, 600, 1280, 720)]
    public void Defaults_WindowSize_Is1600x900OrTheLargestThatFits(int screenW, int screenH, int expectedW, int expectedH)
    {
        var defaults = GameSettings.Defaults(new WindowSize(screenW, screenH));

        Assert.Equal(new WindowSize(expectedW, expectedH), defaults.Display.WindowSize);
    }

    [Fact]
    public void Read_FullValidFile_ReadsAsWrittenWithoutWarnings()
    {
        var result = SettingsReader.Read(FullFile);

        Assert.Empty(result.Warnings);
        Assert.Equal(Full, result.Settings);
    }

    [Theory]
    // Wrong type
    [InlineData("display.window_mode", "1")]
    [InlineData("display.window_size", "\"1920x1080\"")]
    [InlineData("display.render_scale", "\"75\"")]
    [InlineData("display.quality", "true")]
    [InlineData("display.vsync", "1")]
    [InlineData("display.frame_cap", "60.5")]
    [InlineData("audio.master_volume", "\"55\"")]
    [InlineData("audio.music_volume", "null")]
    [InlineData("audio.effects_volume", "[100]")]
    [InlineData("gameplay.invert_mouse_y", "\"true\"")]
    [InlineData("gameplay.invert_stick_y", "0")]
    [InlineData("gameplay.mouse_sensitivity", "\"1.5\"")]
    [InlineData("gameplay.stick_sensitivity", "1e400")]
    [InlineData("gameplay.minimap_corner", "3")]
    // Out of range or off its step
    [InlineData("display.render_scale", "45")]
    [InlineData("display.render_scale", "105")]
    [InlineData("display.render_scale", "77")]
    [InlineData("display.frame_cap", "90")]
    [InlineData("display.frame_cap", "-30")]
    [InlineData("audio.master_volume", "101")]
    [InlineData("audio.music_volume", "-5")]
    [InlineData("audio.effects_volume", "52")]
    [InlineData("gameplay.mouse_sensitivity", "0.2")]
    [InlineData("gameplay.stick_sensitivity", "3.05")]
    [InlineData("gameplay.stick_sensitivity", "1.02")]
    // Unknown enum value
    [InlineData("display.window_mode", "\"fullscreen\"")]
    [InlineData("display.quality", "\"ultra\"")]
    [InlineData("display.quality", "\"High\"")]
    [InlineData("gameplay.minimap_corner", "\"centre\"")]
    // Window size not in the list, or not the right shape
    [InlineData("display.window_size", "{ \"width\": 1024, \"height\": 768 }")]
    [InlineData("display.window_size", "{ \"width\": 900, \"height\": 1600 }")]
    [InlineData("display.window_size", "{ \"width\": 1920 }")]
    [InlineData("display.window_size", "{ \"width\": 1920, \"height\": 1080, \"depth\": 1 }")]
    public void Read_OneBadValue_FallsBackToItsDefaultKeepsTheOthersAndWarnsOnce(string key, string literal)
    {
        var json = Modify(root => Set(root, key, JsonNode.Parse(literal)));

        var result = SettingsReader.Read(json);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains($"'{key}'", warning);
        Assert.Equal(FallBack[key](Full), result.Settings);
    }

    [Theory]
    [InlineData("display.window_mode")]
    [InlineData("audio.effects_volume")]
    [InlineData("gameplay.minimap_corner")]
    public void Read_MissingValue_FallsBackToItsDefaultAndWarnsOnce(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        var json = Modify(root => root[key[..dot]]!.AsObject().Remove(key[(dot + 1)..]));

        var result = SettingsReader.Read(json);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains($"'{key}'", warning);
        Assert.Equal(FallBack[key](Full), result.Settings);
    }

    [Theory]
    [InlineData("", "language")]
    [InlineData("display", "brightness")]
    [InlineData("audio", "voice_volume")]
    [InlineData("gameplay", "fov")]
    public void Read_UnknownKey_KeepsEveryValueAndWarnsOnceNamingIt(string parent, string key)
    {
        var json = Modify(root => (parent.Length == 0 ? root : root[parent]!)[key] = 1);

        var result = SettingsReader.Read(json);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains($"'{(parent.Length == 0 ? key : $"{parent}.{key}")}'", warning);
        Assert.Equal(Full, result.Settings);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format_version\": 1, ")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    public void Read_NotAJsonObject_GivesAllDefaultsAndOneWarning(string text)
    {
        var result = SettingsReader.Read(text);

        Assert.Single(result.Warnings);
        Assert.Equal(GameSettings.Default, result.Settings);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void Read_NewerFormatVersion_GivesAllDefaultsAndOneWarning(int version)
    {
        var json = Modify(root => root["format_version"] = version);

        var result = SettingsReader.Read(json);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("format_version", warning);
        Assert.Equal(GameSettings.Default, result.Settings);
    }

    [Fact]
    public void Read_NotJsonWithASmallScreen_UsesTheLargestFittingWindowSize()
    {
        var result = SettingsReader.Read("garbage", new WindowSize(1440, 875));

        Assert.Equal(new WindowSize(1280, 720), result.Settings.Display.WindowSize);
    }

    [Fact]
    public void Read_SensitivityWithinToleranceOfAStep_IsStoredSnapped()
    {
        var json = Modify(root => root["gameplay"]!["mouse_sensitivity"] = 1.1500000001);

        var result = SettingsReader.Read(json);

        Assert.Empty(result.Warnings);
        Assert.Equal(1.15, result.Settings.Gameplay.MouseSensitivity);
    }

    [Fact]
    public void WriteThenRead_FullSettings_GivesEqualValueWithoutWarnings()
    {
        var result = SettingsReader.Read(SettingsWriter.Write(Full));

        Assert.Empty(result.Warnings);
        Assert.Equal(Full, result.Settings);
    }

    [Fact]
    public void WriteThenRead_Defaults_GivesEqualValueWithoutWarnings()
    {
        var result = SettingsReader.Read(SettingsWriter.Write(GameSettings.Default));

        Assert.Empty(result.Warnings);
        Assert.Equal(GameSettings.Default, result.Settings);
    }

    [Fact]
    public void Write_UsesSnakeCaseKeysAndEnumNames()
    {
        var root = JsonNode.Parse(SettingsWriter.Write(Full))!;

        Assert.Equal(1, root["format_version"]!.GetValue<int>());
        Assert.Equal("borderless_fullscreen", root["display"]!["window_mode"]!.GetValue<string>());
        Assert.Equal(1920, root["display"]!["window_size"]!["width"]!.GetValue<int>());
        Assert.Equal("high", root["display"]!["quality"]!.GetValue<string>());
        Assert.Equal(55, root["audio"]!["master_volume"]!.GetValue<int>());
        Assert.Equal("top_right", root["gameplay"]!["minimap_corner"]!.GetValue<string>());
        Assert.Equal(2.35, root["gameplay"]!["stick_sensitivity"]!.GetValue<double>());
    }

    [Fact]
    public void Write_InvalidValue_ThrowsNamingTheKey()
    {
        var bad = Full with { Audio = Full.Audio with { MusicVolume = 33 } };

        var ex = Assert.Throws<ArgumentException>(() => SettingsWriter.Write(bad));

        Assert.Contains("audio.music_volume", ex.Message);
    }

    private static void Set(JsonNode root, string key, JsonNode? value)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        root[key[..dot]]![key[(dot + 1)..]] = value;
    }

    private static string Modify(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(FullFile)!;
        change(root);
        return root.ToJsonString();
    }
}
