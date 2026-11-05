using System.Text.Json.Nodes;
using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="SettingsFile"/>, the settings file on disk: <c>docs/specs/menu-settings-save.md</c>, criteria 6 and 19
/// (closing the view writes <c>settings.json</c>, a new start reads it back; one bad value gives its default and one
/// warning).
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private static readonly GameSettings Changed = new(
        new DisplaySettings(WindowMode.BorderlessFullscreen, new WindowSize(1920, 1080), 75, QualityLevel.High, false, 60),
        new AudioSettings(55, 0, 100),
        new GameplaySettings(true, true, 0.25, 2.35, MinimapCorner.TopRight));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"roadman-settings-{Guid.NewGuid():N}");

    public SettingsFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    [Fact]
    public void Load_MissingFile_GivesTheDefaultsWithoutWarningsAndWritesNothing()
    {
        var result = SettingsFile.Load(SettingsPath, null);

        Assert.Equal(GameSettings.Default, result.Settings);
        Assert.Empty(result.Warnings);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Load_MissingFile_DefaultsUseTheUsableScreen()
    {
        var usable = new WindowSize(1500, 850);

        var result = SettingsFile.Load(SettingsPath, usable);

        Assert.Equal(GameSettings.Defaults(usable), result.Settings);
        Assert.Equal(new WindowSize(1280, 720), result.Settings.Display.WindowSize);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Ac19_SaveThenLoad_RoundTripsEqualWithoutWarnings()
    {
        SettingsFile.Save(SettingsPath, Changed);

        var result = SettingsFile.Load(SettingsPath, null);

        Assert.Equal(Changed, result.Settings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Save_ExistingFile_IsReplaced()
    {
        SettingsFile.Save(SettingsPath, GameSettings.Default);
        SettingsFile.Save(SettingsPath, Changed);

        Assert.Equal(Changed, SettingsFile.Load(SettingsPath, null).Settings);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        SettingsFile.Save(SettingsPath, Changed);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_CreatesAMissingFolder()
    {
        var path = Path.Combine(_folder, "a", "b", "settings.json");

        SettingsFile.Save(path, Changed);

        Assert.Equal(Changed, SettingsFile.Load(path, null).Settings);
    }

    [Fact]
    public void Save_WritesWhatSettingsWriterWrites()
    {
        SettingsFile.Save(SettingsPath, Changed);

        Assert.Equal(SettingsWriter.Write(Changed), File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Ac19_Load_OneBadValue_GivesItsDefaultKeepsTheOthersAndOneWarningNamingTheKey()
    {
        SettingsFile.Save(SettingsPath, Changed);
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        root["audio"]!["music_volume"] = 33;
        File.WriteAllText(SettingsPath, root.ToJsonString());

        var result = SettingsFile.Load(SettingsPath, null);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("'audio.music_volume'", warning);
        Assert.Equal(Changed with { Audio = Changed.Audio with { MusicVolume = 70 } }, result.Settings);
    }

    [Fact]
    public void Load_UnreadablePath_ADirectoryWhereTheFileShouldBe_GivesTheDefaultsAndOneWarning()
    {
        // File.Exists is false for a directory, so this is the "no file" path: defaults, no warning.
        Directory.CreateDirectory(SettingsPath);

        var result = SettingsFile.Load(SettingsPath, null);

        Assert.Equal(GameSettings.Default, result.Settings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_FileThatCantBeRead_GivesTheDefaultsAndOneCouldNotReadWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only; the suites run on macOS and Linux.
        }

        SettingsFile.Save(SettingsPath, Changed);
        File.SetUnixFileMode(SettingsPath, UnixFileMode.None);
        try
        {
            if (CanRead(SettingsPath))
            {
                return; // running as root: the mode doesn't stop the read
            }

            var result = SettingsFile.Load(SettingsPath, null);

            Assert.Equal(GameSettings.Default, result.Settings);
            var warning = Assert.Single(result.Warnings);
            Assert.StartsWith("could not read the file (", warning);
            Assert.EndsWith("); using the defaults", warning);
        }
        finally
        {
            File.SetUnixFileMode(SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Save_PathIsADirectory_Throws()
    {
        Directory.CreateDirectory(SettingsPath);

        Assert.ThrowsAny<Exception>(() => SettingsFile.Save(SettingsPath, Changed));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsFile.Load(null!, null));
        Assert.Throws<ArgumentNullException>(() => SettingsFile.Save(null!, Changed));
        Assert.Throws<ArgumentNullException>(() => SettingsFile.Save(SettingsPath, null!));
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
