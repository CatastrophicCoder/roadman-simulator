namespace Roadman.Core.Player;

/// <summary>
/// The temporary fall rule of <c>docs/specs/player-on-foot.md</c> ("Temporary fall respawn"): a player far below the
/// slab respawns. The world-edge death spec replaces it.
/// </summary>
public static class FallReset
{
    /// <summary>
    /// True when <paramref name="y"/> is more than <paramref name="depth"/> metres below
    /// <paramref name="slabBottomY"/> (<see cref="Maps.Cliff.BottomY"/>), that is <c>y &lt; slabBottomY − depth</c>.
    /// </summary>
    public static bool IsBelow(double y, double slabBottomY, double depth) => y < slabBottomY - depth;
}
