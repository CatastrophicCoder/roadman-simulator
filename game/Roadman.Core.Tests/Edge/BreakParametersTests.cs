using Roadman.Core.Edge;

namespace Roadman.Core.Tests.Edge;

/// <summary>
/// <see cref="BreakParameters"/>: when a barrier item breaks and how heavy its loose pieces are
/// (<c>docs/specs/world-edge-game.md</c>, "Breaking" and "Exports"; Part B plan amendment 2). The engine side is
/// criterion 12 in <c>game/tests/WorldEdgeBreakingTest.cs</c>.
/// </summary>
public class BreakParametersTests
{
    private readonly BreakParameters _defaults = new();

    [Fact]
    public void Defaults_AreTheSpecStartingValues()
    {
        Assert.Equal(3.0, _defaults.BreakSpeed); // spec "Exports": BreakSpeed 3 m/s
        Assert.Equal(300.0, _defaults.PieceDensity);
        Assert.Equal(0.5, _defaults.MinPieceMass);
        Assert.Equal(0.04, _defaults.SweepMargin);
    }

    [Theory]
    [InlineData(-1e-9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SweepMargin_NegativeOrNonFinite_Throws(double value)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new BreakParameters { SweepMargin = value });
        Assert.Contains(nameof(BreakParameters.SweepMargin), ex.Message);
    }

    [Fact]
    public void SweepMargin_Zero_IsAllowed()
    {
        Assert.Equal(0.0, new BreakParameters { SweepMargin = 0 }.SweepMargin);
    }

    [Theory]
    [InlineData(-1e-9)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BreakSpeed_NegativeOrNonFinite_Throws(double value)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new BreakParameters { BreakSpeed = value });
        Assert.Contains(nameof(BreakParameters.BreakSpeed), ex.Message);
    }

    [Fact]
    public void BreakSpeed_Zero_IsAllowed()
    {
        Assert.Equal(0.0, new BreakParameters { BreakSpeed = 0 }.BreakSpeed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PieceDensity_ZeroNegativeOrNonFinite_Throws(double value)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new BreakParameters { PieceDensity = value });
        Assert.Contains(nameof(BreakParameters.PieceDensity), ex.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MinPieceMass_ZeroNegativeOrNonFinite_Throws(double value)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new BreakParameters { MinPieceMass = value });
        Assert.Contains(nameof(BreakParameters.MinPieceMass), ex.Message);
    }

    [Fact]
    public void PositiveValues_AreKept()
    {
        var parameters = new BreakParameters { BreakSpeed = 4.5, PieceDensity = 700, MinPieceMass = 0.01 };

        Assert.Equal(4.5, parameters.BreakSpeed);
        Assert.Equal(700, parameters.PieceDensity);
        Assert.Equal(0.01, parameters.MinPieceMass);
    }

    [Fact]
    public void Breaks_AtBreakSpeed_IsTrue() // criterion 12: "at least BreakSpeed"
    {
        Assert.True(_defaults.Breaks(_defaults.BreakSpeed));
    }

    [Fact]
    public void Breaks_JustBelowBreakSpeed_IsFalse()
    {
        Assert.False(_defaults.Breaks(Math.BitDecrement(_defaults.BreakSpeed)));
    }

    [Theory]
    [InlineData(15.0)] // criterion 12's breaking speed
    [InlineData(3.0000001)]
    [InlineData(1e6)]
    public void Breaks_AboveBreakSpeed_IsTrue(double speed)
    {
        Assert.True(_defaults.Breaks(speed));
    }

    [Theory]
    [InlineData(2.0)] // criterion 12's non-breaking speed
    [InlineData(0.0)]
    [InlineData(-15.0)]
    public void Breaks_BelowBreakSpeed_IsFalse(double speed)
    {
        Assert.False(_defaults.Breaks(speed));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Breaks_NonFiniteSpeed_IsFalse(double speed)
    {
        Assert.False(_defaults.Breaks(speed));
    }

    [Fact]
    public void Breaks_BreakSpeedZero_BreaksAtZero()
    {
        Assert.True(new BreakParameters { BreakSpeed = 0 }.Breaks(0));
    }

    [Fact]
    public void PieceVolume_Post_IsACylinderWithTheLargerOfXAndZAsDiameter()
    {
        Assert.Equal(Math.PI * 0.05 * 0.05 * 1.2, BreakParameters.PieceVolume(PieceKind.Post, 0.1, 1.2, 0.08), 12);
        Assert.Equal(Math.PI * 0.05 * 0.05 * 1.2, BreakParameters.PieceVolume(PieceKind.Post, 0.08, 1.2, 0.1), 12);
    }

    [Fact]
    public void PieceVolume_Float_IsAnEllipsoid()
    {
        Assert.Equal(4.0 / 3.0 * Math.PI * 0.1 * 0.075 * 0.05, BreakParameters.PieceVolume(PieceKind.Float, 0.2, 0.15, 0.1), 12);
    }

    [Theory]
    [InlineData(PieceKind.Strand)]
    [InlineData(PieceKind.FloatLine)]
    [InlineData(PieceKind.Stand)]
    [InlineData(PieceKind.BoardRed)]
    [InlineData(PieceKind.BoardWhite)]
    public void PieceVolume_OtherKinds_AreBoxes(PieceKind kind)
    {
        Assert.Equal(2.5 * 0.2 * 0.03, BreakParameters.PieceVolume(kind, 2.5, 0.2, 0.03), 12);
    }

    [Theory]
    [InlineData(-1.0, 1.0, 1.0)]
    [InlineData(1.0, double.NaN, 1.0)]
    [InlineData(1.0, 1.0, double.PositiveInfinity)]
    [InlineData(0.0, 1.0, 1.0)]
    public void PieceVolume_NegativeZeroOrNonFiniteEdge_CountsAsZero_ForEveryKindButPosts(double x, double y, double z)
    {
        // Posts: the diameter is the larger of x and z, so one bad horizontal edge alone doesn't empty them (below).
        foreach (var kind in Enum.GetValues<PieceKind>().Where(k => k != PieceKind.Post))
        {
            Assert.Equal(0.0, BreakParameters.PieceVolume(kind, x, y, z));
        }
    }

    [Theory]
    [InlineData(1.0, -1.0, 1.0)]
    [InlineData(1.0, double.NaN, 1.0)]
    [InlineData(-1.0, 1.0, double.NaN)]
    [InlineData(0.0, 1.0, 0.0)]
    public void PieceVolume_Post_BadHeightOrBothHorizontalEdgesBad_IsZero(double x, double y, double z)
    {
        Assert.Equal(0.0, BreakParameters.PieceVolume(PieceKind.Post, x, y, z));
    }

    [Fact]
    public void PieceVolume_Post_NegativeZEdgeUsesX()
    {
        // The diameter is the larger of x and z; a bad z counts as 0, so x alone gives it.
        Assert.Equal(Math.PI * 0.05 * 0.05 * 1.0, BreakParameters.PieceVolume(PieceKind.Post, 0.1, 1.0, -0.2), 12);
    }

    [Fact]
    public void PieceMass_IsVolumeTimesDensity_WhenAboveTheMinimum()
    {
        var parameters = new BreakParameters { PieceDensity = 300, MinPieceMass = 0.5 };
        var volume = BreakParameters.PieceVolume(PieceKind.BoardRed, 2.0, 0.15, 0.03); // 0.009 m³ -> 2.7 kg

        Assert.Equal(volume * 300, parameters.PieceMass(PieceKind.BoardRed, 2.0, 0.15, 0.03), 12);
        Assert.True(volume * 300 > parameters.MinPieceMass);
    }

    [Fact]
    public void PieceMass_LightPiece_IsClampedToMinPieceMass()
    {
        // A 2 cm barbed-wire strand, 2.5 m long: 0.001 m³ × 300 kg/m³ = 0.3 kg, under the 0.5 kg minimum.
        var volume = BreakParameters.PieceVolume(PieceKind.Strand, 2.5, 0.02, 0.02);
        Assert.True(volume * _defaults.PieceDensity < _defaults.MinPieceMass);

        Assert.Equal(_defaults.MinPieceMass, _defaults.PieceMass(PieceKind.Strand, 2.5, 0.02, 0.02));
    }

    [Fact]
    public void PieceMass_ZeroVolume_IsMinPieceMass()
    {
        Assert.Equal(_defaults.MinPieceMass, _defaults.PieceMass(PieceKind.Post, 0, 0, 0));
        Assert.Equal(_defaults.MinPieceMass, _defaults.PieceMass(PieceKind.BoardWhite, double.NaN, 1, 1));
    }

    [Fact]
    public void PieceMass_ExactlyTheMinimum_IsTheMinimum()
    {
        // A 1 × 1 × 1 box at density 0.5 weighs exactly the 0.5 kg minimum.
        var parameters = new BreakParameters { PieceDensity = 0.5, MinPieceMass = 0.5 };
        Assert.Equal(0.5, parameters.PieceMass(PieceKind.Stand, 1, 1, 1));
    }
}
