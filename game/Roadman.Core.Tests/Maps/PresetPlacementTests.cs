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

    [Fact]
    public void IsInsideMap_WithHit_IsTrue()
    {
        Assert.True(PresetPlacement.IsInsideMap(72.3));
    }

    [Fact]
    public void IsInsideMap_WithoutHit_IsFalse()
    {
        Assert.False(PresetPlacement.IsInsideMap(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LooksAtMap_HitUnderLookAt_IsTrue(bool groundRelative)
    {
        // The camera itself is over nothing; only the look-at point counts.
        var preset = new CameraPreset("p", new LocalPoint(-285.4, 99.3, -1201.0), new LocalPoint(-325.4, 72.3, -1172.0), 70, groundRelative);
        double? Hit(double x, double z) => x == -325.4 && z == -1172.0 ? 87.3 : null;

        Assert.True(PresetPlacement.LooksAtMap(preset, Hit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LooksAtMap_NoHitUnderLookAt_IsFalse(bool groundRelative)
    {
        // A camera over the map looking at a point where the ray hits nothing.
        var preset = new CameraPreset("p", new LocalPoint(0, 100, 0), new LocalPoint(-1130, 0, 719), 70, groundRelative);
        double? Hit(double x, double z) => x == 0 && z == 0 ? 100 : null;

        Assert.False(PresetPlacement.LooksAtMap(preset, Hit));
    }

    [Fact]
    public void LooksAtMap_ProbesOnlyTheLookAtPoint()
    {
        var preset = new CameraPreset("p", new LocalPoint(1, 2, 3), new LocalPoint(4, 5, 6), 70, true);
        var probes = new List<(double X, double Z)>();

        PresetPlacement.LooksAtMap(preset, (x, z) =>
        {
            probes.Add((x, z));
            return 0;
        });

        Assert.Equal([(4.0, 6.0)], probes);
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
