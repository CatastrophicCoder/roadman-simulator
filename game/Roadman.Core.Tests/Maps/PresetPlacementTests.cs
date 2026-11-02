using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class PresetPlacementTests
{
    [Fact]
    public void ResolveHeight_WithHit_AddsRelativeHeightToGround()
    {
        var result = PresetPlacement.ResolveHeight(1.7, 104.3);

        Assert.False(result.IsOutsideBuiltArea);
        Assert.Equal(106.0, result.Height, 9);
    }

    [Fact]
    public void ResolveHeight_WithoutHit_IsOutsideBuiltArea()
    {
        var result = PresetPlacement.ResolveHeight(1.7, null);

        Assert.True(result.IsOutsideBuiltArea);
    }

    [Fact]
    public void Resolve_GroundRelative_UsesTerrainUnderEachPoint()
    {
        var preset = new CameraPreset("p", new LocalPoint(10, 600, 500), new LocalPoint(0, 0, 0), 70, true);
        double? Ground(double x, double z) => x == 10 && z == 500 ? 120 : 90;

        var result = PresetPlacement.Resolve(preset, Ground);

        Assert.False(result.IsOutsideBuiltArea);
        Assert.Equal(new LocalPoint(10, 720, 500), result.Position);
        Assert.Equal(new LocalPoint(0, 90, 0), result.LookAt);
    }

    [Fact]
    public void Resolve_GroundRelative_NoTerrainUnderPosition_IsOutsideBuiltArea()
    {
        var preset = new CameraPreset("p", new LocalPoint(50_000, 600, 50_000), new LocalPoint(0, 0, 0), 70, true);

        var result = PresetPlacement.Resolve(preset, (x, z) => x == 0 && z == 0 ? 100 : null);

        Assert.True(result.IsOutsideBuiltArea);
    }

    [Fact]
    public void Resolve_GroundRelative_NoTerrainUnderLookAt_MeasuresFromPositionGround()
    {
        var preset = new CameraPreset("p", new LocalPoint(0, 2, 0), new LocalPoint(0, 2, -9_000), 70, true);

        var result = PresetPlacement.Resolve(preset, (_, z) => z == 0 ? 100 : null);

        Assert.False(result.IsOutsideBuiltArea);
        Assert.Equal(new LocalPoint(0, 102, -9_000), result.LookAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LooksAtLoadedTiles_LookAtInsideExtent_IsTrue(bool groundRelative)
    {
        // The camera itself may be outside the extent; only the look-at point counts.
        var preset = new CameraPreset("p", new LocalPoint(-285.4, 99.3, -1201.0), new LocalPoint(-325.4, 72.3, -1173.0), 70, groundRelative);
        var extent = new PlanExtent(-1153, -1175, 1153, 1171);

        Assert.True(PresetPlacement.LooksAtLoadedTiles(preset, extent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LooksAtLoadedTiles_LookAtOutsideExtent_IsFalse(bool groundRelative)
    {
        // A camera inside the extent looking at a point beyond it.
        var preset = new CameraPreset("p", new LocalPoint(0, 100, 0), new LocalPoint(-325.4, 72.3, -1173.0), 70, groundRelative);
        var extent = new PlanExtent(-512, -512, 512, 512);

        Assert.False(PresetPlacement.LooksAtLoadedTiles(preset, extent));
    }

    [Fact]
    public void LooksAtLoadedTiles_LookAtOnEdge_IsTrue()
    {
        var preset = new CameraPreset("p", new LocalPoint(0, 100, 0), new LocalPoint(512, 0, -512), 70, false);

        Assert.True(PresetPlacement.LooksAtLoadedTiles(preset, new PlanExtent(-512, -512, 512, 512)));
    }

    [Fact]
    public void LooksAtLoadedTiles_NoExtent_IsFalse()
    {
        var preset = new CameraPreset("p", new LocalPoint(0, 100, 0), new LocalPoint(0, 0, 0), 70, false);

        Assert.False(PresetPlacement.LooksAtLoadedTiles(preset, null));
    }

    [Fact]
    public void Resolve_Absolute_DoesNotProbeTerrain()
    {
        var preset = new CameraPreset("p", new LocalPoint(1, 200, 3), new LocalPoint(0, 90, 0), 70, false);
        var probes = 0;

        var result = PresetPlacement.Resolve(preset, (_, _) =>
        {
            probes++;
            return null;
        });

        Assert.Equal(0, probes);
        Assert.False(result.IsOutsideBuiltArea);
        Assert.Equal(preset.Position, result.Position);
        Assert.Equal(preset.LookAt, result.LookAt);
    }
}
