using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

/// <summary>
/// Which Somero pack a test loads (<c>docs/specs/test-performance.md</c>, Behaviour 3): the test-area pack in
/// <see cref="TestAreaPath"/> (built by <c>.claude/scripts/build-test-pack.sh</c>) through
/// <see cref="MapLoader.PackFolderOverride"/>, or the pack in <c>res://maps/somero</c> (full locally). The map id is
/// <c>somero</c> either way. A suite that uses the test pack calls <see cref="UseTestArea"/> before each test, and
/// every suite resets the override to null after each test (<see cref="UseFullPack"/>).
/// </summary>
public static class TestPacks
{
    /// <summary>The test-area pack folder (git-ignored; outside <c>maps/</c>, excluded from exports by <c>tests/*</c>).</summary>
    public const string TestAreaPath = "res://tests/packs/somero-test";

    /// <summary>The Somero pack in the game's pack folder.</summary>
    public const string FullPath = "res://maps/somero";

    /// <summary>The script that builds <see cref="TestAreaPath"/>.</summary>
    public const string BuildScript = ".claude/scripts/build-test-pack.sh";

    /// <summary>The folder of the pack the tests load now: the override when set, else <see cref="FullPath"/>.</summary>
    public static string PackPath => MapLoader.PackFolderOverride ?? FullPath;

    /// <summary>
    /// Points <see cref="MapLoader.PackFolderOverride"/> at the test-area pack. Fails, naming the build script, when its
    /// manifest isn't there; never falls back to the full pack.
    /// </summary>
    public static void UseTestArea()
    {
        var manifest = $"{TestAreaPath}/{MapPack.ManifestFile}";
        AssertBool(FileAccess.FileExists(manifest))
            .OverrideFailureMessage($"no test-area pack at {manifest}; build it with {BuildScript}")
            .IsTrue();
        MapLoader.PackFolderOverride = TestAreaPath;
    }

    /// <summary>What to run when the pack in <see cref="PackPath"/> is missing.</summary>
    public static string MissingPackHint(string mapId) =>
        MapLoader.PackFolderOverride is null ? MapPack.RegenHint(mapId) : $"build it with {BuildScript}";

    /// <summary>Clears the override, so <c>res://maps/&lt;id&gt;</c> is loaded again (the full pack locally).</summary>
    public static void UseFullPack() => MapLoader.PackFolderOverride = null;

    /// <summary>
    /// Copies the minimap files (<c>minimap.json</c> with <c>map_id</c> set to <paramref name="mapId"/>,
    /// <c>minimap.png</c> and <c>minimap.png.import</c>) from the pack folder <paramref name="sourceFolder"/> to
    /// <paramref name="targetFolder"/> (both absolute), so a temporary pack that leaves out another file pushes no
    /// minimap error (<c>docs/specs/minimap.md</c>, "Data and map loads").
    /// </summary>
    public static void CopyMinimap(string sourceFolder, string targetFolder, string mapId)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            System.IO.File.ReadAllText(System.IO.Path.Combine(sourceFolder, MapPack.MinimapFile)))!;
        json["map_id"] = mapId;
        System.IO.File.WriteAllText(System.IO.Path.Combine(targetFolder, MapPack.MinimapFile), json.ToJsonString());
        foreach (var file in new[] { MapPack.MinimapImageFile, MapPack.MinimapImageFile + ".import" })
        {
            System.IO.File.Copy(System.IO.Path.Combine(sourceFolder, file), System.IO.Path.Combine(targetFolder, file), overwrite: true);
        }
    }
}
