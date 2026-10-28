namespace Roadman.Core.Maps;

/// <summary>
/// Pack conventions and text for the orthophoto debug view. The pipeline writes one JPEG per tile to
/// <c>debug/orthophoto/&lt;tile_id&gt;.jpg</c> in the pack (a <c>.gdignore</c>d folder, so Godot neither imports nor
/// exports it), and credits the imagery in the manifest with the source <see cref="CreditSource"/>.
/// </summary>
public static class Orthophoto
{
    /// <summary>The <c>source</c> of the orthophoto credit in the manifest.</summary>
    public const string CreditSource = "mml_orthophoto";

    /// <summary>The folder of the per-tile images, relative to the pack folder.</summary>
    public const string PackFolder = "debug/orthophoto";

    public const string FileExtension = ".jpg";

    /// <summary>The folder of the per-tile images in the pack at <paramref name="packPath"/>.</summary>
    public static string Folder(string packPath) => Join(packPath, PackFolder);

    /// <summary>The image of tile <paramref name="tileId"/> in <paramref name="folder"/>.</summary>
    public static string TexturePath(string folder, string tileId) => Join(folder, tileId + FileExtension);

    /// <summary>Shown when the view is switched on but the pack has no orthophoto images.</summary>
    public static string NoTexturesMessage(string mapId) => $"No orthophoto in this pack; run /regen-map {mapId}";

    /// <summary>The credits whose source is <see cref="CreditSource"/>, in manifest order.</summary>
    public static IReadOnlyList<MapCredit> Credits(IEnumerable<MapCredit> credits)
    {
        ArgumentNullException.ThrowIfNull(credits);
        return credits.Where(credit => credit.Source == CreditSource).ToList();
    }

    /// <summary>
    /// The attribution overlay text: one line per orthophoto credit, <c>&lt;attribution&gt; (&lt;licence&gt;)</c>,
    /// with repeated lines left out. Empty when the credits have no orthophoto entry.
    /// </summary>
    public static string AttributionText(IEnumerable<MapCredit> credits) =>
        string.Join('\n', Credits(credits).Select(credit => $"{credit.Attribution} ({credit.Licence})").Distinct());

    private static string Join(string folder, string name) => $"{folder.TrimEnd('/')}/{name}";
}
