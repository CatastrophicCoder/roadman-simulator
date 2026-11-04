using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Tests.Maps;
using static Roadman.Core.Tests.Edge.EdgeTestData;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="BarrierLayout"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 2.</summary>
/// <remarks>
/// "Posts" in the no-shared-post rule are fence posts (<see cref="PieceKind.Post"/>, a fence item's
/// <see cref="BarrierItemLayout.PostBases"/>). Roadblock stands are not posts: the contract puts a stand at both
/// ends of a roadblock and lets the fence beside it own the post at the same joint.
/// </remarks>
public class BarrierLayoutTests
{
    private const double MaxHeight = 0.9;
    private const double MaxOutward = 1.0;
    private const double MaxInward = 2.0;
    private const double MaxGap = 0.1;
    private const double SamePoint = 1e-3;
    private const double Epsilon = 1e-6;

    // Road 52's north roadblock has 3.39 m between the ground under it and its Start–End line; the test ground adds
    // a bump of that height in the middle of every roadblock.
    private const double BumpM = 3.39;

    private static readonly BarrierParameters Defaults = new();

    // The square (EdgeTestData.Square) on a sloping plane: fences on every side, a roadblock on the west side between
    // two land fences, a 0.05 m land fence between two land fences on the south side, a water fence on the east side,
    // and fence-to-fence joints at all four corners.
    private static readonly Boundary Synthetic = MakeBoundary(
        Square,
        roadblocks: [MakeRoadblock("roadblock:cut:way/10:0", (0, 40), (0, 47.5), 40, 47.5, (-1, 0))],
        fences:
        [
            MakeFence("fence:0", FenceType.Land, (0, 0), (0, 40), 0, 40, (-1, 0)),
            MakeFence("fence:1", FenceType.Land, (0, 47.5), (0, 100), 47.5, 100, (-1, 0)),
            MakeFence("fence:2", FenceType.Land, (0, 100), (50, 100), 100, 150, (0, 1)),
            MakeFence("fence:3", FenceType.Land, (50, 100), (50.05, 100), 150, 150.05, (0, 1)),
            MakeFence("fence:4", FenceType.Land, (50.05, 100), (100, 100), 150.05, 200, (0, 1)),
            MakeFence("fence:5", FenceType.Water, (100, 100), (100, 0), 200, 300, (1, 0)),
            MakeFence("fence:6", FenceType.Land, (100, 0), (0, 0), 300, 400, (0, -1)),
        ]);

    [Fact]
    public void BoxCorners_AreOriginPlusMinusHalfAxes()
    {
        var box = new BoxTransform(new LocalPoint(1, 2, 3), new LocalPoint(2, 0, 0), new LocalPoint(0, 4, 0), new LocalPoint(0, 0, 6));
        var corners = box.Corners();

        Assert.Equal(8, corners.Count);
        Assert.Equal(8, corners.Distinct().Count());
        Assert.All(corners, c =>
        {
            Assert.Contains(c.X, new[] { 0.0, 2.0 });
            Assert.Contains(c.Y, new[] { 0.0, 4.0 });
            Assert.Contains(c.Z, new[] { 0.0, 6.0 });
        });
    }

    [Fact]
    public void Build_Synthetic_OneItemPerRoadblockAndFenceInRingOrder() => AssertItems(Synthetic, Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_OneItemPerRoadblockAndFenceInRingOrder() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertItems(boundary, Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_PiecesAtMostTheHeightAboveTheirBase() => AssertHeights(Synthetic, Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_PiecesAtMostTheHeightAboveTheirBase() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertHeights(boundary, Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_NothingReachesMoreThanOneMetreOutward() => AssertReach(Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_NothingReachesMoreThanOneMetreOutward() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertReach(Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_CollisionCoversTheRing() => AssertCoverage(Synthetic, Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_CollisionCoversTheRing() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertCoverage(boundary, Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_FencePostsAtRunEndsAndAtMostPostSpacingApart() => AssertPostSpacing(Synthetic, Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_FencePostsAtRunEndsAndAtMostPostSpacingApart() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertPostSpacing(boundary, Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_NoTwoPostsAtTheSamePoint() => AssertNoSharedPosts(Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_NoTwoPostsAtTheSamePoint() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertNoSharedPosts(Build(boundary));
        }
    }

    [Fact]
    public void Build_ShortSegmentBetweenFences_HasOnePostAtEachJoint() // criterion 2
    {
        var items = Build(Synthetic);
        var posts = items.Where(i => i.Kind != BarrierKind.Roadblock).SelectMany(i => i.PostBases).ToList();

        Assert.Single(posts, p => Planar(p, 50, 100) < SamePoint);
        Assert.Single(posts, p => Planar(p, 50.05, 100) < SamePoint);
        Assert.Single(posts, p => Planar(p, 0, 100) < SamePoint); // a corner joint
        Assert.Single(items, i => i.Id == "fence:3").PostBases.ToList()
            .ForEach(p => Assert.True(Planar(p, 50.05, 100) < SamePoint, $"fence:3 post at {p}"));
    }

    [Fact]
    public void Build_FenceBesideRoadblock_OwnsThePostAtTheJoint() // criterion 2
    {
        var items = Build(Synthetic);

        Assert.Contains(items.Single(i => i.Id == "fence:0").PostBases, p => Planar(p, 0, 40) < SamePoint);
        Assert.Contains(items.Single(i => i.Id == "fence:1").PostBases, p => Planar(p, 0, 47.5) < SamePoint);
    }

    [Fact]
    public void Build_Synthetic_FencePiecesStandOnTheFenceLine() => AssertFenceBases(Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_FencePiecesStandOnTheFenceLine() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertFenceBases(Build(boundary));
        }
    }

    [Fact]
    public void Build_Synthetic_RoadblockStandsOnTheGround() => AssertRoadblocks(Synthetic, Build(Synthetic));

    [PackFact]
    public void Build_GeneratedPacks_RoadblockStandsOnTheGround() // criterion 2
    {
        foreach (var (_, boundary) in RealPacks())
        {
            AssertRoadblocks(boundary, Build(boundary));
        }
    }

    [Fact]
    public void Build_NoGround_RoadblockStandsOnItsStartEndLine()
    {
        var items = BarrierLayout.Build(Synthetic, Defaults, (_, _) => null);
        var roadblock = Synthetic.Roadblocks[0];
        var item = items.Single(i => i.Kind == BarrierKind.Roadblock);

        Assert.NotEmpty(item.PostBases);
        Assert.All(item.PostBases, stand =>
        {
            var t = (stand.Z - roadblock.Start.Z) / (roadblock.End.Z - roadblock.Start.Z);
            Assert.Equal(roadblock.Start.Y + (t * (roadblock.End.Y - roadblock.Start.Y)), stand.Y, 1e-6);
        });
    }

    [Fact]
    public void Build_GroundMissingExactlyOnTheRing_StandsUseTheGroundStandGroundInsetInward()
    {
        // The meshes end on the ring: no ground on the roadblock's line (x = 0) or outward of it, ground inward of it.
        // Synthetic's roadblock runs along x = 0 with outward (−1, 0), so inward is +x.
        var roadblock = Synthetic.Roadblocks[0];
        var lookups = new List<(double X, double Z)>();
        double? Ground(double x, double z)
        {
            lookups.Add((x, z));
            return x > 1e-9 ? 100 + x : null;
        }

        var items = BarrierLayout.Build(Synthetic, Defaults, Ground);
        var item = items.Single(i => i.Kind == BarrierKind.Roadblock);

        Assert.Equal(0, roadblock.Start.X, 1e-9);
        Assert.Equal(0, roadblock.End.X, 1e-9);
        Assert.NotEmpty(item.PostBases);
        Assert.All(item.PostBases, stand =>
        {
            Assert.Equal(0, stand.X, 1e-9); // the stand stays on the ring
            Assert.Equal(100 + Defaults.StandGroundInset, stand.Y, 1e-9);
        });
        Assert.Contains(lookups, l => Math.Abs(l.X - Defaults.StandGroundInset) < 1e-9);
    }

    [Fact]
    public void StandGround_HitOnTheRing_IsUsedWithoutAnInwardLookup()
    {
        var calls = 0;
        var y = BarrierLayout.StandGround((_, _) => { calls++; return 5.0; }, 1, 2, new PlanarPoint(0, 1), 0.05);
        Assert.Equal(5.0, y);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void StandGround_MissOnTheRing_LooksInsetInwardAgainstOutward(double? onRing)
    {
        var lookups = new List<(double X, double Z)>();
        var y = BarrierLayout.StandGround(
            (x, z) =>
            {
                lookups.Add((x, z));
                return lookups.Count == 1 ? onRing : 7.0;
            },
            10, 20, new PlanarPoint(0.6, 0.8), 0.05);

        Assert.Equal(7.0, y);
        Assert.Equal(2, lookups.Count);
        Assert.Equal(10 - (0.6 * 0.05), lookups[1].X, 1e-12);
        Assert.Equal(20 - (0.8 * 0.05), lookups[1].Z, 1e-12);
    }

    [Fact]
    public void StandGround_BothMiss_IsNull_AndAZeroInsetLooksOnlyOnce()
    {
        Assert.Null(BarrierLayout.StandGround((_, _) => null, 0, 0, new PlanarPoint(1, 0), 0.05));
        var calls = 0;
        Assert.Null(BarrierLayout.StandGround((_, _) => { calls++; return null; }, 0, 0, new PlanarPoint(1, 0), 0));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(nameof(BarrierParameters.RoadblockHeight), 0)]
    [InlineData(nameof(BarrierParameters.RoadblockHeight), 0.91)]
    [InlineData(nameof(BarrierParameters.LandFenceHeight), -0.1)]
    [InlineData(nameof(BarrierParameters.LandFenceHeight), 1.0)]
    [InlineData(nameof(BarrierParameters.WaterFenceHeight), 0)]
    [InlineData(nameof(BarrierParameters.WaterFenceHeight), 0.95)]
    [InlineData(nameof(BarrierParameters.PostSpacing), 0)]
    [InlineData(nameof(BarrierParameters.PostSpacing), -1)]
    [InlineData(nameof(BarrierParameters.StandSpacing), 0)]
    [InlineData(nameof(BarrierParameters.StandGroundInset), -0.01)]
    [InlineData(nameof(BarrierParameters.StandGroundInset), double.NaN)]
    public void Parameters_OutOfRange_Throw(string name, double value)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            var parameters = name switch
            {
                nameof(BarrierParameters.RoadblockHeight) => new BarrierParameters { RoadblockHeight = value },
                nameof(BarrierParameters.LandFenceHeight) => new BarrierParameters { LandFenceHeight = value },
                nameof(BarrierParameters.WaterFenceHeight) => new BarrierParameters { WaterFenceHeight = value },
                nameof(BarrierParameters.PostSpacing) => new BarrierParameters { PostSpacing = value },
                nameof(BarrierParameters.StandGroundInset) => new BarrierParameters { StandGroundInset = value },
                _ => new BarrierParameters { StandSpacing = value },
            };
            BarrierLayout.Build(Synthetic, parameters, (_, _) => null);
        });
    }

    [Fact]
    public void Parameters_Defaults_KeepTheRules()
    {
        Assert.InRange(Defaults.RoadblockHeight, double.Epsilon, MaxHeight);
        Assert.InRange(Defaults.LandFenceHeight, double.Epsilon, MaxHeight);
        Assert.InRange(Defaults.WaterFenceHeight, double.Epsilon, MaxHeight);
        Assert.True(Defaults.PostSpacing > 0);
        Assert.True(Defaults.StandSpacing > 0);
    }

    // --- Rule checks ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<BarrierItemLayout> Build(Boundary boundary) =>
        BarrierLayout.Build(boundary, Defaults, TestGround(boundary));

    private static void AssertItems(Boundary boundary, IReadOnlyList<BarrierItemLayout> items)
    {
        Assert.Equal(boundary.Roadblocks.Count + boundary.Fences.Count, items.Count);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(items.OrderBy(i => i.SStart).Select(i => i.Id), items.Select(i => i.Id));

        foreach (var roadblock in boundary.Roadblocks)
        {
            var item = Assert.Single(items, i => i.Id == roadblock.Id);
            Assert.Equal(BarrierKind.Roadblock, item.Kind);
            AssertSource(item, roadblock.Start, roadblock.End, roadblock.SStart, roadblock.SEnd, roadblock.Outward);
        }

        foreach (var fence in boundary.Fences)
        {
            var item = Assert.Single(items, i => i.Id == fence.Id);
            Assert.Equal(fence.Type == FenceType.Water ? BarrierKind.WaterFence : BarrierKind.LandFence, item.Kind);
            AssertSource(item, fence.Start, fence.End, fence.SStart, fence.SEnd, fence.Outward);
        }
    }

    private static void AssertSource(BarrierItemLayout item, LocalPoint start, LocalPoint end, double sStart, double sEnd, LocalPoint outward)
    {
        Assert.Equal(start, item.Start);
        Assert.Equal(end, item.End);
        Assert.Equal(sStart, item.SStart);
        Assert.Equal(sEnd, item.SEnd);
        Assert.Equal(outward.X, item.Outward.X, 1e-9);
        Assert.Equal(outward.Z, item.Outward.Z, 1e-9);
        Assert.NotEmpty(item.Pieces);
        Assert.NotEmpty(item.Collision);
    }

    private static void AssertHeights(Boundary boundary, IReadOnlyList<BarrierItemLayout> items)
    {
        var failures = new List<string>();
        foreach (var item in items)
        {
            var height = item.Kind switch
            {
                BarrierKind.Roadblock => Defaults.RoadblockHeight,
                BarrierKind.LandFence => Defaults.LandFenceHeight,
                _ => Defaults.WaterFenceHeight,
            };
            Assert.True(height <= MaxHeight);

            foreach (var piece in item.Pieces)
            {
                foreach (var corner in piece.Box.Corners())
                {
                    var above = corner.Y - BaseY(piece.BaseA, piece.BaseB, corner);
                    if (above > height + Epsilon && failures.Count < 10)
                    {
                        failures.Add($"{item.Id} {piece.Kind}: corner {corner} is {above:0.####} m above its base");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{boundary.MapId}: {string.Join("; ", failures)}");
    }

    private static void AssertReach(IReadOnlyList<BarrierItemLayout> items)
    {
        var failures = new List<string>();
        foreach (var item in items)
        {
            var corners = item.Pieces.SelectMany(p => p.Box.Corners().Select(c => (What: p.Kind.ToString(), c)))
                .Concat(item.Collision.SelectMany(b => b.Corners().Select(c => (What: "collision", c))));
            foreach (var (what, corner) in corners)
            {
                var outward = ((corner.X - item.Start.X) * item.Outward.X) + ((corner.Z - item.Start.Z) * item.Outward.Z);
                if ((outward > MaxOutward + Epsilon || outward <= -MaxInward) && failures.Count < 10)
                {
                    failures.Add($"{item.Id} {what}: corner {corner} is {outward:0.####} m outward");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static void AssertCoverage(Boundary boundary, IReadOnlyList<BarrierItemLayout> items)
    {
        var length = boundary.Ring.LengthM;
        var intervals = new List<(double A, double B, string Id)>();
        foreach (var item in items)
        {
            var (dx, dz) = Direction(item.Start, item.End);
            foreach (var box in item.Collision)
            {
                var ts = box.Corners().Select(c => ((c.X - item.Start.X) * dx) + ((c.Z - item.Start.Z) * dz)).ToList();
                AddCyclic(intervals, item.SStart + ts.Min(), item.SStart + ts.Max(), length, item.Id);
            }
        }

        intervals.Sort((p, q) => p.A.CompareTo(q.A));
        var covered = 0.0;
        var coveredBy = "(ring start)";
        var gaps = new List<string>();
        foreach (var (a, b, id) in intervals)
        {
            if (a - covered > MaxGap && gaps.Count < 10)
            {
                gaps.Add($"s {covered:0.###}..{a:0.###} ({a - covered:0.###} m, after {coveredBy}, before {id})");
            }

            if (b > covered)
            {
                covered = b;
                coveredBy = id;
            }
        }

        if (length - covered > MaxGap)
        {
            gaps.Add($"s {covered:0.###}..{length:0.###} at the ring's end");
        }

        Assert.True(gaps.Count == 0, $"{boundary.MapId}: collision gaps over {MaxGap} m: {string.Join("; ", gaps)}");
    }

    private static void AssertPostSpacing(Boundary boundary, IReadOnlyList<BarrierItemLayout> items)
    {
        var length = boundary.Ring.LengthM;
        var failures = new List<string>();
        foreach (var run in FenceRuns(items))
        {
            var posts = new List<double>();
            var offset = 0.0;
            var previousStart = double.NegativeInfinity;
            foreach (var item in run)
            {
                if (item.SStart < previousStart)
                {
                    offset = length; // the run passes the ring's start
                }

                previousStart = item.SStart;
                var (dx, dz) = Direction(item.Start, item.End);
                posts.AddRange(item.PostBases.Select(p =>
                    offset + item.SStart + ((p.X - item.Start.X) * dx) + ((p.Z - item.Start.Z) * dz)));
            }

            posts.Sort();
            var runStart = run[0].SStart;
            var runEnd = run[^1].SEnd + offset;
            var name = $"{run[0].Id}..{run[^1].Id}";
            if (posts.Count == 0 || Math.Abs(posts[0] - runStart) > SamePoint || Math.Abs(posts[^1] - runEnd) > SamePoint)
            {
                failures.Add($"{name}: posts {(posts.Count == 0 ? "none" : $"{posts[0]:0.###}..{posts[^1]:0.###}")}, run s {runStart:0.###}..{runEnd:0.###}");
                continue;
            }

            for (var i = 1; i < posts.Count; i++)
            {
                if (posts[i] - posts[i - 1] > Defaults.PostSpacing + Epsilon && failures.Count < 10)
                {
                    failures.Add($"{name}: posts at s {posts[i - 1]:0.###} and {posts[i]:0.###} are {posts[i] - posts[i - 1]:0.###} m apart");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{boundary.MapId}: {string.Join("; ", failures)}");
    }

    private static void AssertNoSharedPosts(IReadOnlyList<BarrierItemLayout> items)
    {
        var posts = items.Where(i => i.Kind != BarrierKind.Roadblock)
            .SelectMany(i => i.PostBases.Select(p => (i.Id, p)))
            .OrderBy(e => e.p.X)
            .ToList();
        var failures = new List<string>();
        for (var i = 0; i < posts.Count; i++)
        {
            for (var j = i + 1; j < posts.Count && posts[j].p.X - posts[i].p.X < SamePoint; j++)
            {
                if (Planar(posts[i].p, posts[j].p.X, posts[j].p.Z) < SamePoint && failures.Count < 10)
                {
                    failures.Add($"{posts[i].Id} and {posts[j].Id} at {posts[i].p}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"two posts at one point: {string.Join("; ", failures)}");
    }

    private static void AssertFenceBases(IReadOnlyList<BarrierItemLayout> items)
    {
        foreach (var item in items.Where(i => i.Kind != BarrierKind.Roadblock))
        {
            Assert.Equal(item.PostBases.Count, item.Pieces.Count(p => p.Kind == PieceKind.Post));
            foreach (var point in item.Pieces.SelectMany(p => new[] { p.BaseA, p.BaseB }).Concat(item.PostBases))
            {
                var distance = DistanceToLine(item.Start, item.End, point);
                Assert.True(distance < Epsilon, $"{item.Id}: base {point} is {distance} m off the fence line");
            }
        }
    }

    private static void AssertRoadblocks(Boundary boundary, IReadOnlyList<BarrierItemLayout> items)
    {
        var ground = TestGround(boundary);
        foreach (var item in items.Where(i => i.Kind == BarrierKind.Roadblock))
        {
            var (dx, dz) = Direction(item.Start, item.End);
            var stands = item.PostBases.OrderBy(p => ((p.X - item.Start.X) * dx) + ((p.Z - item.Start.Z) * dz)).ToList();
            Assert.True(stands.Count >= 2, $"{item.Id}: {stands.Count} stands");
            Assert.True(Planar(stands[0], item.Start.X, item.Start.Z) < SamePoint, $"{item.Id}: first stand {stands[0]}");
            Assert.True(Planar(stands[^1], item.End.X, item.End.Z) < SamePoint, $"{item.Id}: last stand {stands[^1]}");
            Assert.Equal(stands.Count - 1, item.Collision.Count); // one collision box per bay

            for (var i = 0; i < stands.Count; i++)
            {
                var expected = ground(stands[i].X, stands[i].Z);
                Assert.NotNull(expected);
                Assert.True(
                    Math.Abs(stands[i].Y - expected.Value) <= 0.05,
                    $"{item.Id}: stand {stands[i]} is {stands[i].Y - expected.Value:0.###} m from the ground {expected:0.###}");
                if (i > 0)
                {
                    var gap = Planar(stands[i], stands[i - 1].X, stands[i - 1].Z);
                    Assert.True(gap <= Defaults.StandSpacing + Epsilon, $"{item.Id}: stands {gap:0.###} m apart");
                }
            }

            foreach (var piece in item.Pieces)
            {
                foreach (var point in new[] { piece.BaseA, piece.BaseB })
                {
                    Assert.True(
                        stands.Any(s => Planar(s, point.X, point.Z) < SamePoint && Math.Abs(s.Y - point.Y) < SamePoint),
                        $"{item.Id} {piece.Kind}: base {point} is not a stand base");
                }
            }
        }
    }

    // --- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ground under the roadblocks: the Start–End line plus a bump of <see cref="BumpM"/> in the middle
    /// (<c>sin(πt)</c>), so the ends match the line. Null away from every roadblock.
    /// </summary>
    private static Func<double, double, double?> TestGround(Boundary boundary) => (x, z) =>
    {
        double? best = null;
        var bestDistance = 1.0;
        foreach (var r in boundary.Roadblocks)
        {
            var lx = r.End.X - r.Start.X;
            var lz = r.End.Z - r.Start.Z;
            var len2 = (lx * lx) + (lz * lz);
            var t = (((x - r.Start.X) * lx) + ((z - r.Start.Z) * lz)) / len2;
            if (t < -0.01 || t > 1.01)
            {
                continue;
            }

            var distance = Math.Abs(((x - r.Start.X) * lz) - ((z - r.Start.Z) * lx)) / Math.Sqrt(len2);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                var tc = Math.Clamp(t, 0, 1);
                best = r.Start.Y + (tc * (r.End.Y - r.Start.Y)) + (BumpM * Math.Sin(Math.PI * tc));
            }
        }

        return best;
    };

    /// <summary>The height of the base line BaseA→BaseB under <paramref name="corner"/> (horizontal projection).</summary>
    private static double BaseY(LocalPoint a, LocalPoint b, LocalPoint corner)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var len2 = (dx * dx) + (dz * dz);
        if (len2 < 1e-18)
        {
            return a.Y;
        }

        var t = (((corner.X - a.X) * dx) + ((corner.Z - a.Z) * dz)) / len2;
        return a.Y + (t * (b.Y - a.Y));
    }

    private static (double Dx, double Dz) Direction(LocalPoint start, LocalPoint end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt((dx * dx) + (dz * dz));
        return (dx / length, dz / length);
    }

    private static double DistanceToLine(LocalPoint a, LocalPoint b, LocalPoint p)
    {
        var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = (p.X - a.X, p.Y - a.Y, p.Z - a.Z);
        var (cx, cy, cz) = ((uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx));
        return Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz)) / Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));
    }

    private static double Planar(LocalPoint p, double x, double z) =>
        Math.Sqrt(((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z)));

    /// <summary>Adds [a, b] to <paramref name="intervals"/>, wrapped onto [0, length).</summary>
    private static void AddCyclic(List<(double A, double B, string Id)> intervals, double a, double b, double length, string id)
    {
        if (b - a >= length)
        {
            intervals.Add((0, length, id));
            return;
        }

        var shift = Math.Floor(a / length) * length;
        a -= shift;
        b -= shift;
        if (b <= length)
        {
            intervals.Add((a, b, id));
        }
        else
        {
            intervals.Add((a, length, id));
            intervals.Add((0, b - length, id));
        }
    }

    /// <summary>Maximal runs of consecutive fence items in ring order (cyclic); needs at least one roadblock.</summary>
    private static List<List<BarrierItemLayout>> FenceRuns(IReadOnlyList<BarrierItemLayout> items)
    {
        var first = items.ToList().FindIndex(i => i.Kind == BarrierKind.Roadblock);
        Assert.True(first >= 0, "the post-spacing check needs at least one roadblock");
        var runs = new List<List<BarrierItemLayout>>();
        List<BarrierItemLayout>? run = null;
        for (var k = 1; k <= items.Count; k++)
        {
            var item = items[(first + k) % items.Count];
            if (item.Kind == BarrierKind.Roadblock)
            {
                run = null;
                continue;
            }

            if (run is null)
            {
                run = [];
                runs.Add(run);
            }

            run.Add(item);
        }

        return runs;
    }

    private static double PlaneY(double x, double z) => 100 + (0.01 * x) + (0.02 * z);

    private static Roadblock MakeRoadblock(string id, (double X, double Z) start, (double X, double Z) end, double sStart, double sEnd, (double X, double Z) outward) =>
        new(
            id,
            [new RoadblockNode("cut:way/10:0", "osm:way/10#0", RoadblockRole.SpawnDespawn)],
            "tertiary",
            true,
            true,
            7.5,
            RoadValueSource.Osm,
            new LocalPoint(start.X, PlaneY(start.X, start.Z), start.Z),
            new LocalPoint(end.X, PlaneY(end.X, end.Z), end.Z),
            new LocalPoint((start.X + end.X) / 2, PlaneY((start.X + end.X) / 2, (start.Z + end.Z) / 2), (start.Z + end.Z) / 2),
            0,
            new LocalPoint(outward.X, 0, outward.Z),
            sStart,
            sEnd,
            false);

    private static Fence MakeFence(string id, FenceType type, (double X, double Z) start, (double X, double Z) end, double sStart, double sEnd, (double X, double Z) outward) =>
        new(
            id,
            type,
            new LocalPoint(start.X, PlaneY(start.X, start.Z), start.Z),
            new LocalPoint(end.X, PlaneY(end.X, end.Z), end.Z),
            sStart,
            sEnd,
            new LocalPoint(outward.X, 0, outward.Z),
            type == FenceType.Water ? ["mml:jarvi/1"] : null);
}
