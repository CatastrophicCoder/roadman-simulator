using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class OrthophotoTests
{
    private static readonly MapCredit Dem = new(
        "mml_dem_2m",
        "Elevation model 2 m",
        "CC BY 4.0",
        "Contains data from the National Land Survey of Finland Elevation model 2 m, 10/2026",
        "2026-10-27T14:44:30Z");

    private static readonly MapCredit Ortho = new(
        "mml_orthophoto",
        "Orthophotos",
        "CC BY 4.0",
        "Contains data from the National Land Survey of Finland Orthophotos, 10/2026",
        "2026-10-28T09:00:00Z");

    [Fact]
    public void TexturePath_IsTileIdJpgInTheDebugFolder()
    {
        var folder = Orthophoto.Folder("res://maps/somero");

        Assert.Equal("res://maps/somero/debug/orthophoto", folder);
        Assert.Equal("res://maps/somero/debug/orthophoto/tile_x-1_y0.jpg", Orthophoto.TexturePath(folder, "tile_x-1_y0"));
        Assert.Equal("/tmp/o/tile_x0_y0.jpg", Orthophoto.TexturePath("/tmp/o/", "tile_x0_y0"));
    }

    [Fact]
    public void NoTexturesMessage_NamesTheRegenCommand()
    {
        Assert.Equal("No orthophoto in this pack; run /regen-map somero", Orthophoto.NoTexturesMessage("somero"));
    }

    [Fact]
    public void Credits_KeepsOnlyOrthophotoSources()
    {
        Assert.Equal([Ortho], Orthophoto.Credits([Dem, Ortho]));
        Assert.Empty(Orthophoto.Credits([Dem]));
    }

    [Fact]
    public void AttributionText_IsAttributionAndLicenceOfEachOrthophotoCredit()
    {
        var other = Ortho with { Dataset = "Orthophotos, false colour", Attribution = "Other text", Licence = "CC BY 4.0" };

        var text = Orthophoto.AttributionText([Dem, Ortho, other]);

        Assert.Equal(
            "Contains data from the National Land Survey of Finland Orthophotos, 10/2026 (CC BY 4.0)\nOther text (CC BY 4.0)",
            text);
    }

    [Fact]
    public void AttributionText_LeavesOutRepeatedLines()
    {
        var sameText = Ortho with { Dataset = "Orthophotos 2", RetrievedAt = "2026-10-29T00:00:00Z" };

        Assert.Equal(
            "Contains data from the National Land Survey of Finland Orthophotos, 10/2026 (CC BY 4.0)",
            Orthophoto.AttributionText([Ortho, sameText]));
    }

    [Fact]
    public void AttributionText_WithoutOrthophotoCredit_IsEmpty()
    {
        Assert.Equal("", Orthophoto.AttributionText([Dem]));
        Assert.Equal("", Orthophoto.AttributionText([]));
    }
}
