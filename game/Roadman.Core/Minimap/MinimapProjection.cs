using Roadman.Core.Maps;

namespace Roadman.Core.Minimap;

/// <summary>
/// A point on the minimap panel in pixels (base units): origin at the panel centre, +x right, +y down, as Godot 2D.
/// </summary>
public readonly record struct PanelPoint(double X, double Y)
{
    /// <summary>Distance from the panel centre.</summary>
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static PanelPoint operator +(PanelPoint a, PanelPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static PanelPoint operator -(PanelPoint a, PanelPoint b) => new(a.X - b.X, a.Y - b.Y);

    public static PanelPoint operator *(PanelPoint a, double k) => new(a.X * k, a.Y * k);

    /// <summary>Distance between two panel points.</summary>
    public static double Distance(PanelPoint a, PanelPoint b) => (a - b).Length;
}

/// <summary>Where to draw the background image on the panel (see <see cref="MinimapProjection.ImageTransform"/>).</summary>
/// <param name="Origin">Panel position of the image's top-left corner (its pixel (0, 0)).</param>
/// <param name="Rotation">Godot 2D rotation in radians (clockwise on screen, since +y is down).</param>
/// <param name="Scale">Panel pixels per image pixel, the same on both axes.</param>
public readonly record struct MinimapImageTransform(PanelPoint Origin, double Rotation, double Scale);

/// <summary>
/// The minimap's mapping between Godot ground points and the round panel (<c>docs/specs/minimap.md</c>,
/// "Roadman.Core"). Godot's axes: x east, z south (north is −z). Yaws follow <c>Node3D.Rotation.Y</c>: 0 faces −z
/// (north), and a positive yaw turns counter-clockwise seen from above, so +90° faces west (−x) and −90° faces east.
/// The panel's origin is its centre, +y is down, and the heading direction is panel up. Panel angles (as in
/// <see cref="NorthBearing"/> and <see cref="ArrowAngle"/>) are radians clockwise from panel up, which is also the
/// Godot 2D rotation that turns an up-pointing shape to that direction.
/// </summary>
public sealed class MinimapProjection
{
    private readonly double _cos;
    private readonly double _sin;

    /// <param name="centreX">The ground point at the panel centre (the player), x.</param>
    /// <param name="centreZ">The ground point at the panel centre, z.</param>
    /// <param name="headingYaw">The yaw (radians) that is panel up: the player camera's yaw.</param>
    /// <param name="rangeM">Metres from the centre to the rim, above 0.</param>
    /// <param name="radiusPx">The panel radius in pixels, above 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite, or the range or radius isn't above 0.</exception>
    public MinimapProjection(double centreX, double centreZ, double headingYaw, double rangeM, double radiusPx)
    {
        Finite(centreX, nameof(centreX));
        Finite(centreZ, nameof(centreZ));
        Finite(headingYaw, nameof(headingYaw));
        Positive(rangeM, nameof(rangeM));
        Positive(radiusPx, nameof(radiusPx));
        CentreX = centreX;
        CentreZ = centreZ;
        HeadingYaw = headingYaw;
        RangeM = rangeM;
        RadiusPx = radiusPx;
        Scale = radiusPx / rangeM;
        (_sin, _cos) = Math.SinCos(headingYaw);
    }

    public double CentreX { get; }

    public double CentreZ { get; }

    /// <summary>The yaw that is panel up, radians.</summary>
    public double HeadingYaw { get; }

    public double RangeM { get; }

    public double RadiusPx { get; }

    /// <summary>Panel pixels per metre: <see cref="RadiusPx"/> / <see cref="RangeM"/>.</summary>
    public double Scale { get; }

    /// <summary>
    /// The panel angle of north (radians clockwise from up, in (−π, π]). It equals the heading yaw, and it is also the
    /// Godot 2D rotation that turns a north-up map (x east = panel right, z south = panel down) into this panel.
    /// </summary>
    public double NorthBearing => NormaliseAngle(HeadingYaw);

    /// <summary>
    /// The panel position of the ground point (<paramref name="x"/>, <paramref name="z"/>): the heading direction is
    /// panel up and the scale is <see cref="Scale"/>. Points beyond the range lie outside the radius.
    /// </summary>
    public PanelPoint ToPanel(double x, double z)
    {
        var dx = (x - CentreX) * Scale;
        var dz = (z - CentreZ) * Scale;
        return new PanelPoint((dx * _cos) - (dz * _sin), (dx * _sin) + (dz * _cos));
    }

    /// <inheritdoc cref="ToPanel(double, double)"/>
    public PanelPoint ToPanel(PlanarPoint point) => ToPanel(point.X, point.Z);

    /// <summary>The inverse of <see cref="ToPanel(double, double)"/>: the ground point (x, z) at a panel position.</summary>
    public PlanarPoint ToWorld(double px, double py)
    {
        var dx = (px * _cos) + (py * _sin);
        var dz = (-px * _sin) + (py * _cos);
        return new PlanarPoint(CentreX + (dx / Scale), CentreZ + (dz / Scale));
    }

    /// <inheritdoc cref="ToWorld(double, double)"/>
    public PlanarPoint ToWorld(PanelPoint point) => ToWorld(point.X, point.Y);

    /// <summary>
    /// The panel angle (radians clockwise from up, in (−π, π]) of the player arrow for a player facing
    /// <paramref name="facingYaw"/>: 0 when it equals the heading yaw, π/2 (right) when the player faces 90° clockwise
    /// of the heading (seen from above).
    /// </summary>
    public double ArrowAngle(double facingYaw) => NormaliseAngle(HeadingYaw - facingYaw);

    /// <summary>
    /// How to draw <paramref name="image"/> so that its pixels land on <see cref="ToPanel(double, double)"/> of their
    /// ground points: translate to <see cref="MinimapImageTransform.Origin"/>, rotate by
    /// <see cref="MinimapImageTransform.Rotation"/>, scale by <see cref="MinimapImageTransform.Scale"/>.
    /// </summary>
    public MinimapImageTransform ImageTransform(MinimapImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new MinimapImageTransform(
            ToPanel(image.Bounds.MinX, image.Bounds.MinZ), NorthBearing, Scale * image.MetresPerPixel);
    }

    /// <summary>The panel angle (radians clockwise from up) of the direction from the centre to <paramref name="point"/>.</summary>
    public static double AngleOf(PanelPoint point) => Math.Atan2(point.X, -point.Y);

    /// <summary>The unit panel vector at panel angle <paramref name="angle"/> (radians clockwise from up).</summary>
    public static PanelPoint DirectionOf(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new PanelPoint(sin, -cos);
    }

    /// <summary><paramref name="angle"/> in radians brought into (−π, π].</summary>
    public static double NormaliseAngle(double angle)
    {
        var result = Math.IEEERemainder(angle, 2 * Math.PI);
        return result <= -Math.PI ? result + (2 * Math.PI) : result;
    }

    private static void Finite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "must be a finite number");
        }
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "must be a finite number above 0");
        }
    }
}
