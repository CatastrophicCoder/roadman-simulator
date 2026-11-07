using Roadman.Core.Maps;
using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapLabels"/>: <c>docs/specs/minimap.md</c>, "Labels" and acceptance criterion 8.</summary>
public class MinimapLabelsTests
{
    private const double Radius = 66;
    private const double Range = 150;
    private const double CharWidth = 6;
    private const double TextHeight = 10;

    private static readonly Func<string, (double Width, double Height)> Measure =
        text => (text.Length * CharWidth, TextHeight);

    private static readonly MinimapData Streets = MinimapData.Build(MinimapFixtures.CrossingStreets(), (PlaceIndex?)null, null);

    public static TheoryData<double, double> Centres => new()
    {
        { 0, 0 },
        { 35, -30 },
        { -70, 60 },
        { 12.5, 47 },
        { 140, -120 },
    };

    [Theory]
    [MemberData(nameof(Centres))]
    public void Ac8_CrossingStreets_EveryYawIn15DegreeSteps_PlacementsFollowTheRules(double cx, double cz)
    {
        var placedAny = 0;
        for (var step = 0; step < 24; step++)
        {
            var yaw = MinimapProjection.NormaliseAngle(step * Math.PI / 12);
            var (projection, placements, avoid) = PlaceAt(cx, cz, yaw);
            placedAny += placements.Count;

            for (var i = 0; i < placements.Count; i++)
            {
                var label = placements[i];
                var context = $"yaw {step * 15}°, label '{label.Text}'";

                // Inside the circle.
                Assert.All(label.Rect.Corners(), corner => Assert.True(corner.Length <= Radius, $"{context}: corner outside the rim"));

                // Never upside down: the text's up vector (sin a, −cos a) never points into the lower half-plane.
                Assert.True(-Math.Cos(label.Angle) <= 1e-9, $"{context}: reads upside down (angle {label.Angle})");

                // No overlap with the arrow, the north indicator or another label.
                Assert.All(avoid, rect => Assert.False(rect.Overlaps(label.Rect), $"{context}: overlaps the arrow or the north indicator"));
                for (var j = i + 1; j < placements.Count; j++)
                {
                    Assert.False(label.Rect.Overlaps(placements[j].Rect), $"{context}: overlaps '{placements[j].Text}'");
                }

                // Long enough a stretch, and the label is centred on it.
                var chord = PanelPoint.Distance(label.Stretch[0], label.Stretch[^1]);
                Assert.True(chord >= label.Width + MinimapLabelOptions.Default.Padding, $"{context}: stretch too short");
                var middle = (label.Stretch[0] + label.Stretch[^1]) * 0.5;
                Assert.Equal(middle.X, label.Anchor.X, 1e-9);
                Assert.Equal(middle.Y, label.Anchor.Y, 1e-9);
            }

            // Each label text at most once; only texts of named or ref'd edges; never the Swedish name or a class text.
            var texts = placements.Select(label => label.Text).ToList();
            Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
            Assert.All(texts, text => Assert.Contains(text, Streets.LabelTexts));
            Assert.DoesNotContain(texts, text => text.Contains("Oxvägen") || text.Contains("Torggatan"));
            Assert.DoesNotContain(texts, text => text == StreetLabel.ClassText("service"));

            _ = projection;
        }

        Assert.True(placedAny > 0, "the fixture should get some labels");
    }

    [Fact]
    public void Ac8_EdgesWithoutNameOrRef_GetNoLabel()
    {
        Assert.Null(MinimapData.LabelTextOf(MinimapFixtures.Edge("e", "service", [(0, 0), (10, 0)])));
        Assert.Null(MinimapData.LabelTextOf(MinimapFixtures.Edge("e", "service", [(0, 0), (10, 0)], nameSv: "Vägen")));

        // The unnamed service road in the fixture runs straight through the view; it never becomes a source.
        var projection = new MinimapProjection(0, -30, 0, Range, Radius);
        var sources = MinimapLabels.Collect(Streets.QueryRoads(0, -30, Range), projection);
        Assert.DoesNotContain(sources, source => source.Text == StreetLabel.ClassText("service"));
        Assert.Equal(
            Streets.Roads.Where(road => road.LabelText is not null).Select(road => road.LabelText).Distinct().Order(StringComparer.Ordinal),
            Streets.LabelTexts);
        Assert.DoesNotContain(Streets.Roads, road => road.EdgeId == "osm:way/3001#0" && road.LabelText is not null);
    }

    [Fact]
    public void Ac8_TheSwedishNameIsNeverALabel_TheRefIsAddedToTheName()
    {
        var edge = MinimapFixtures.Edge("e", "primary", [(0, 0), (10, 0)], name: "Härkätie", nameSv: "Oxvägen", reference: "52");

        Assert.Equal("Härkätie (52)", MinimapData.LabelTextOf(edge));
        Assert.Equal("2803", MinimapData.LabelTextOf(MinimapFixtures.Edge("e", "unclassified", [(0, 0), (1, 0)], reference: "2803")));
        Assert.DoesNotContain(Streets.LabelTexts, text => text.Contains("Oxvägen") || text.Contains("Torggatan"));
    }

    [Fact]
    public void Ac8_StretchShorterThanTextPlusPadding_GetsNoLabel()
    {
        var text = "Torikatu"; // 48 px wide
        var needed = (text.Length * CharWidth) + MinimapLabelOptions.Default.Padding;

        var tooShort = Source(text, RoadGroup.Road, new PanelPoint(-(needed - 0.01) / 2, 20), new PanelPoint((needed - 0.01) / 2, 20));
        var justLong = Source(text, RoadGroup.Road, new PanelPoint(-needed / 2, 20), new PanelPoint(needed / 2, 20));

        Assert.Empty(MinimapLabels.Place([tooShort], Measure, Radius, []));
        Assert.Single(MinimapLabels.Place([justLong], Measure, Radius, []));
    }

    [Fact]
    public void Ac8_SameInput_GivesTheSamePlacements()
    {
        var (_, first, _) = PlaceAt(12.5, 47, 0.4);
        var (_, second, _) = PlaceAt(12.5, 47, 0.4);

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Text, second[i].Text);
            Assert.Equal(first[i].Anchor, second[i].Anchor);
            Assert.Equal(first[i].Angle, second[i].Angle);
            Assert.Equal(first[i].Stretch, second[i].Stretch);
        }
    }

    [Fact]
    public void Ac8_SourceOrder_DoesNotChangeTheLabels()
    {
        var projection = new MinimapProjection(35, -30, 0.9, Range, Radius);
        var sources = MinimapLabels.Collect(Streets.QueryRoads(35, -30, Range), projection);
        var avoid = Avoid(projection);

        var forward = MinimapLabels.Place(sources, Measure, Radius, avoid);
        var backward = MinimapLabels.Place(Enumerable.Reverse(sources).ToList(), Measure, Radius, avoid);

        Assert.Equal(forward.Select(label => (label.Text, label.Anchor)), backward.Select(label => (label.Text, label.Anchor)));
    }

    [Fact]
    public void Label_IsCentredOnTheStretchAndRotatedAlongIt()
    {
        var label = Assert.Single(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new PanelPoint(-40, -30), new PanelPoint(20, 30))],
            Measure, Radius, []));

        Assert.Equal(new PanelPoint(-10, 0), label.Anchor);
        Assert.Equal(Math.PI / 4, label.Angle, 1e-12);
        Assert.Equal(Asemakatu.Length * CharWidth, label.Width);
        Assert.Equal(TextHeight, label.Height);
    }

    [Fact]
    public void Label_RunningRightToLeft_IsTurnedToReadLeftToRight()
    {
        var label = Assert.Single(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new PanelPoint(40, 10), new PanelPoint(-40, 10))],
            Measure, Radius, []));

        Assert.Equal(0, label.Angle, 1e-12);
    }

    [Fact]
    public void Label_Vertical_ReadsBottomToTop()
    {
        var down = Assert.Single(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new PanelPoint(20, -40), new PanelPoint(20, 40))], Measure, Radius, []));
        var up = Assert.Single(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new PanelPoint(20, 40), new PanelPoint(20, -40))], Measure, Radius, []));

        Assert.Equal(-Math.PI / 2, down.Angle, 1e-12);
        Assert.Equal(-Math.PI / 2, up.Angle, 1e-12);
    }

    [Fact]
    public void Label_UsesTheLongestStraightEnoughStretch()
    {
        // A short straight run, a sharp bend, then a longer straight run.
        var label = Assert.Single(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new(-50, 0), new(-10, 0), new(-10, 20), new(60, 20))],
            Measure, Radius, []));

        Assert.Equal([new PanelPoint(-10, 20), new PanelPoint(60, 20)], label.Stretch);
        Assert.Equal(new PanelPoint(25, 20), label.Anchor);
    }

    [Fact]
    public void Label_PiecesOfOneStreetMeetingEndToEnd_AreJoined()
    {
        // Two 35 px edges: neither is long enough on its own (62 px needed), together they are.
        var label = Assert.Single(MinimapLabels.Place(
            [
                Source("Asemakatu", RoadGroup.Road, new PanelPoint(35, 5), new PanelPoint(0, 5)),
                Source("Asemakatu", RoadGroup.Road, new PanelPoint(-35, 5), new PanelPoint(0, 5)),
            ],
            Measure, Radius, []));

        Assert.Equal(70, PanelPoint.Distance(label.Stretch[0], label.Stretch[^1]), 1e-12);
    }

    [Fact]
    public void Label_OverlappingAnAvoidedRectangle_IsDropped()
    {
        var arrow = MinimapRect.AxisAligned(new PanelPoint(0, 0), 14, 14);

        Assert.Empty(MinimapLabels.Place(
            [Source("Asemakatu", RoadGroup.Road, new PanelPoint(-40, 0), new PanelPoint(40, 0))], Measure, Radius, [arrow]));
    }

    [Fact]
    public void Label_TouchingTheRim_MovesToAStretchInsideOrIsDropped()
    {
        // The only stretch runs 3 px from the rim: the text box would cross it.
        var y = Radius - 3;
        var x = Math.Sqrt((Radius * Radius) - (y * y));
        Assert.Empty(MinimapLabels.Place([Source("Tie", RoadGroup.Road, new PanelPoint(-x, y), new PanelPoint(x, y))], Measure, Radius, []));
    }

    [Fact]
    public void Candidates_ArePlacedByGroupThenVisibleLengthThenText()
    {
        // Three labels that would all overlap at the same place: only the first in order survives.
        MinimapLabelSource Horizontal(string text, RoadGroup group, double half) =>
            Source(text, group, new PanelPoint(-half, 30), new PanelPoint(half, 30));

        var mainWins = MinimapLabels.Place(
            [Horizontal("Bbbb", RoadGroup.Road, 50), Horizontal("Aaaa", RoadGroup.Path, 50), Horizontal("Cccc", RoadGroup.MainRoad, 20)],
            Measure, Radius, []);
        Assert.Equal(["Cccc"], mainWins.Select(label => label.Text));

        var longerWins = MinimapLabels.Place(
            [Horizontal("Aaaa", RoadGroup.Road, 30), Horizontal("Bbbb", RoadGroup.Road, 45)], Measure, Radius, []);
        Assert.Equal(["Bbbb"], longerWins.Select(label => label.Text));

        var ordinalWins = MinimapLabels.Place(
            [Horizontal("Bbbb", RoadGroup.Road, 30), Horizontal("Aaaa", RoadGroup.Road, 30)], Measure, Radius, []);
        Assert.Equal(["Aaaa"], ordinalWins.Select(label => label.Text));
    }

    [Fact]
    public void WorldLabel_ReprojectsWithTheMap()
    {
        var projection = new MinimapProjection(35, -30, 0.3, Range, Radius);
        var (_, placements, _) = PlaceAt(35, -30, 0.3);
        var label = placements[0];

        var stored = label.ToWorld(projection);

        // Same projection: the same anchor and angle.
        Assert.Equal(label.Anchor.X, stored.AnchorIn(projection).X, 1e-9);
        Assert.Equal(label.Anchor.Y, stored.AnchorIn(projection).Y, 1e-9);
        Assert.Equal(label.Angle, stored.AngleIn(projection), 1e-9);

        // The map turns a little: the label turns with it and stays upright.
        var turned = new MinimapProjection(35, -30, 0.45, Range, Radius);
        Assert.True(Math.Cos(stored.AngleIn(turned)) >= -1e-9);
        var world = stored.Anchor;
        Assert.Equal(turned.ToPanel(world), stored.AnchorIn(turned));
    }

    [Fact]
    public void Rect_Overlaps_UsesTheRotatedShapes()
    {
        // Two thin rotated boxes whose axis-aligned bounds overlap but which don't touch.
        var a = new MinimapRect(new PanelPoint(0, 0), 20, 1, Math.PI / 4);
        var b = new MinimapRect(new PanelPoint(10, -10), 20, 1, Math.PI / 4);
        var c = new MinimapRect(new PanelPoint(5, 5), 20, 1, -Math.PI / 4);

        Assert.False(a.Overlaps(b));
        Assert.False(b.Overlaps(a));
        Assert.True(a.Overlaps(c));
        Assert.True(c.Overlaps(a));
    }

    private const string Asemakatu = "Asemakatu";

    private static MinimapLabelSource Source(string text, RoadGroup group, params PanelPoint[] points) => new(text, group, points);

    private static List<MinimapRect> Avoid(MinimapProjection projection)
    {
        var north = MinimapProjection.DirectionOf(projection.NorthBearing) * (Radius - 7);
        return [MinimapRect.AxisAligned(new PanelPoint(0, 0), 14, 16), MinimapRect.AxisAligned(north, 12, 14)];
    }

    private static (MinimapProjection Projection, List<MinimapLabelPlacement> Placements, List<MinimapRect> Avoid) PlaceAt(
        double cx, double cz, double yaw)
    {
        var projection = new MinimapProjection(cx, cz, yaw, Range, Radius);
        var sources = MinimapLabels.Collect(Streets.QueryRoads(cx, cz, Range), projection);
        var avoid = Avoid(projection);
        return (projection, MinimapLabels.Place(sources, Measure, Radius, avoid), avoid);
    }
}
