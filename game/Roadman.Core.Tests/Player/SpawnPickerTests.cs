using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Tests.Maps;

namespace Roadman.Core.Tests.Player;

/// <summary><see cref="SpawnPicker"/>: <c>docs/specs/player-on-foot.md</c>, acceptance criteria 2 and 3.</summary>
public class SpawnPickerTests
{
    // Polygon A: a 100 m square (0..100 x 0..100) with a 20 m square hole (40..60), area 10,000 − 400 = 9,600 m².
    // Polygon B: a 50 m square (200..250 x 0..50), no hole, area 2,500 m². Total 12,100 m².
    private static readonly IReadOnlyList<PlanarPoint> HoleRing = Square(40, 40, 60, 60);

    private static readonly PlanarPolygon PolygonA = new(Square(0, 0, 100, 100), [HoleRing]);

    private static readonly PlanarPolygon PolygonB = new(Square(200, 0, 250, 50), []);

    private static readonly IReadOnlyList<PlanarPolygon> TestShape = [PolygonA, PolygonB];

    [Fact]
    public void Next_SameSeed_GivesSameSequence() // criterion 2
    {
        var first = new SpawnPicker(TestShape, new Random(20261102));
        var second = new SpawnPicker(TestShape, new Random(20261102));

        var a = Enumerable.Range(0, 1000).Select(_ => first.Next()).ToList();
        var b = Enumerable.Range(0, 1000).Select(_ => second.Next()).ToList();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Next_DifferentSeeds_GiveDifferentSequences()
    {
        var first = new SpawnPicker(TestShape, new Random(1));
        var second = new SpawnPicker(TestShape, new Random(2));

        var a = Enumerable.Range(0, 10).Select(_ => first.Next()).ToList();
        var b = Enumerable.Range(0, 10).Select(_ => second.Next()).ToList();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Next_TenThousandDraws_StayOnOpenGroundAndSplitByArea() // criterion 2
    {
        const int n = 10_000;
        var picker = new SpawnPicker(TestShape, new Random(42));
        var hole = new PlanarPolygon(HoleRing, []);
        var inA = 0;
        var inB = 0;

        for (var i = 0; i < n; i++)
        {
            var point = picker.Next();
            Assert.True(SpawnPicker.Contains(TestShape, point.X, point.Z), $"draw {i} {point} is outside the open ground");
            Assert.False(hole.Contains(point.X, point.Z), $"draw {i} {point} is in the hole");
            if (PolygonA.Contains(point.X, point.Z))
            {
                inA++;
            }
            else if (PolygonB.Contains(point.X, point.Z))
            {
                inB++;
            }
        }

        Assert.Equal(n, inA + inB);
        Assert.Equal(12_100, picker.TotalArea, 9);

        // Each draw lands in A with p = 9,600 / 12,100 ≈ 0.7934 (B: 1 − p), so the count in A is binomial with
        // σ = sqrt(n p (1 − p)) = sqrt(10,000 × 0.7934 × 0.2066) ≈ 40.5 draws. 4σ ≈ 162 draws; B's count is n − A's,
        // so it has the same σ.
        var p = PolygonA.Area / (PolygonA.Area + PolygonB.Area);
        var sigma = Math.Sqrt(n * p * (1 - p));
        Assert.InRange(inA, (n * p) - (4 * sigma), (n * p) + (4 * sigma));
        Assert.InRange(inB, (n * (1 - p)) - (4 * sigma), (n * (1 - p)) + (4 * sigma));
    }

    [Fact]
    public void Constructor_EmptyOpenGround_ThrowsArgumentException() // criterion 3
    {
        Assert.Throws<ArgumentException>(() => new SpawnPicker([], new Random(1)));
        Assert.Throws<ArgumentException>(() => new SpawnPicker(new SpawnArea(Square(0, 0, 1, 1), [], 1), new Random(1)));
    }

    [Fact]
    public void Constructor_ZeroAreaOpenGround_ThrowsArgumentException() // criterion 3
    {
        // Every point on one line.
        IReadOnlyList<PlanarPoint> line = [new(0, 0), new(1, 0), new(2, 0), new(0, 0)];
        Assert.Throws<ArgumentException>(() => new SpawnPicker([new PlanarPolygon(line, [])], new Random(1)));

        // A hole that covers the whole exterior.
        var square = Square(0, 0, 10, 10);
        Assert.Throws<ArgumentException>(() => new SpawnPicker([new PlanarPolygon(square, [square])], new Random(1)));

        // Several polygons, all without area.
        Assert.Throws<ArgumentException>(
            () => new SpawnPicker([new PlanarPolygon(line, []), new PlanarPolygon(line, [])], new Random(1)));
    }

    [PackFact]
    public void Next_GeneratedPack_StaysOnItsOpenGround()
    {
        foreach (var pack in GeneratedPacks.WithBoundary())
        {
            var manifest = MapManifestReader.Parse(File.ReadAllText(pack.Manifest), pack.Manifest);
            var boundary = BoundaryReader.Parse(File.ReadAllText(pack.Boundary), manifest, pack.Boundary);
            var picker = new SpawnPicker(boundary.SpawnArea, new Random(7));

            for (var i = 0; i < 1000; i++)
            {
                var point = picker.Next();
                Assert.True(
                    SpawnPicker.Contains(boundary.SpawnArea.OpenGround, point.X, point.Z),
                    $"{pack.MapId}: draw {i} {point} is outside the open ground");
            }
        }
    }

    /// <summary>A closed counter-clockwise square ring in (x, z).</summary>
    private static IReadOnlyList<PlanarPoint> Square(double minX, double minZ, double maxX, double maxZ) =>
        [new(minX, minZ), new(maxX, minZ), new(maxX, maxZ), new(minX, maxZ), new(minX, minZ)];
}
