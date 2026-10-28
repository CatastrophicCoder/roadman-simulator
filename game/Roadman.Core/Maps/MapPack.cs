using System.Text.RegularExpressions;

namespace Roadman.Core.Maps;

/// <summary>
/// File names and path rules for map packs (<c>game/maps/&lt;map-id&gt;/</c>, ADR 0006).
/// </summary>
public static partial class MapPack
{
    public const string ManifestFile = "manifest.json";
    public const string CameraPresetsFile = "camera_presets.json";

    /// <summary>The hint added to every pack error: the pack is generated, so the fix is to rebuild it.</summary>
    public static string RegenHint(string mapId) => $"run /regen-map {mapId} to (re)build the pack";

    /// <summary>
    /// The pipeline's <c>SLUG_PATTERN</c> (<c>tools/mapgen/src/mapgen/config.py</c>), used there for map ids and
    /// camera preset names: lowercase letters and digits in runs joined by single <c>-</c> or <c>_</c>.
    /// </summary>
    public const string SlugPattern = "^[a-z0-9]+(?:[-_][a-z0-9]+)*$";

    /// <summary>True when <paramref name="value"/> matches <see cref="SlugPattern"/>.</summary>
    public static bool IsSlug(string? value) => value is not null && SlugRegex().IsMatch(value);

    /// <summary>A map id is a lowercase slug (<see cref="SlugPattern"/>), the same rule as <c>map.yaml</c>'s <c>id</c>.</summary>
    public static bool IsValidMapId(string? mapId) => IsSlug(mapId);

    /// <summary>
    /// True when <paramref name="path"/> is a relative path inside the pack: not empty, not rooted, no drive or
    /// URI scheme, no <c>..</c> segment and no backslashes.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':'))
        {
            return false;
        }

        if (path.StartsWith('/') || Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    // SlugPattern with \z for $: in .NET, $ also matches before a final "\n", while pydantic's regex engine
    // (which checks the pattern in the pipeline) treats $ as the end of the text.
    [GeneratedRegex(@"^[a-z0-9]+(?:[-_][a-z0-9]+)*\z")]
    private static partial Regex SlugRegex();
}
