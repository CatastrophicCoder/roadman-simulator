namespace Roadman.Core.Tests.Maps;

/// <summary>
/// The generated map packs in <c>game/maps/&lt;id&gt;/</c> (git-ignored pipeline output; a full pack locally, a
/// test-area pack in CI). Tests that read them use <see cref="PackFactAttribute"/>, so they are skipped when no pack
/// has been built.
/// </summary>
internal static class GeneratedPacks
{
    /// <summary>One pack's files.</summary>
    internal sealed record Pack(string MapId, string Manifest, string Boundary);

    /// <summary>The <c>game/</c> folder (the one holding <c>RoadmanSimulator.sln</c>), or null.</summary>
    internal static string? GameDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoadmanSimulator.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Every pack that has both <c>manifest.json</c> and <c>boundary.json</c>, sorted by map id.</summary>
    internal static IReadOnlyList<Pack> WithBoundary()
    {
        var game = GameDirectory();
        var maps = game is null ? null : Path.Combine(game, "maps");
        if (maps is null || !Directory.Exists(maps))
        {
            return [];
        }

        return Directory.GetDirectories(maps)
            .Select(dir => new Pack(
                Path.GetFileName(dir),
                Path.Combine(dir, "manifest.json"),
                Path.Combine(dir, "boundary.json")))
            .Where(pack => File.Exists(pack.Manifest) && File.Exists(pack.Boundary))
            .OrderBy(pack => pack.MapId, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>A fact that is skipped when no generated pack with a <c>boundary.json</c> exists.</summary>
public sealed class PackFactAttribute : FactAttribute
{
    public PackFactAttribute()
    {
        if (GeneratedPacks.WithBoundary().Count == 0)
        {
            Skip = "No generated map pack with boundary.json in game/maps/; run /regen-map <id>";
        }
    }
}
