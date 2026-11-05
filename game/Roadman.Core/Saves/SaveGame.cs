using Roadman.Core.Maps;

namespace Roadman.Core.Saves;

/// <summary>Save format constants (<c>docs/specs/menu-settings-save.md</c>, "Roadman.Core").</summary>
public static class SaveFormat
{
    /// <summary>The format version this game writes and reads without migration.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// The data of one save. Immutable, with value equality (the player's gear list is compared item by item), so a
/// <see cref="SaveWriter"/> then <see cref="SaveReader"/> round trip gives an equal value.
/// </summary>
/// <param name="FormatVersion">The save format version; <see cref="SaveFormat.CurrentVersion"/> for every save this game writes.</param>
/// <param name="GameVersion">The game's <c>application/config/version</c> (<c>dev</c>, <c>0.2.0</c>, ...); not blank. Information only.</param>
/// <param name="SavedAtUtc">When the save was written. Compared as an instant; written in UTC.</param>
/// <param name="PlaySeconds">Total play time in seconds, finite, ≥ 0.</param>
/// <param name="MapId">The map id, a slug (<see cref="MapPack.SlugPattern"/>).</param>
/// <param name="MapGeneratedAt">The manifest's <c>generated_at</c>, as written there; not blank.</param>
/// <param name="Player">The player's part.</param>
/// <param name="Quests">Quest progress; empty in format version 1.</param>
public sealed record SaveGame(
    int FormatVersion,
    string GameVersion,
    DateTimeOffset SavedAtUtc,
    double PlaySeconds,
    string MapId,
    string MapGeneratedAt,
    SavePlayer Player,
    SaveQuests Quests)
{
    /// <summary>
    /// Every rule of a valid save, the same as <see cref="SaveReader"/>'s: the current format version, a non-blank
    /// game version and <c>generated_at</c>, a slug map id, finite non-negative play time, a valid
    /// <see cref="Player"/> (<see cref="SavePlayer.Problem(bool)"/> with the facing pair required) and non-null quests.
    /// </summary>
    /// <returns>Null when valid, else the key path (as in the JSON) and the problem.</returns>
    public (string KeyPath, string Problem)? Problem()
    {
        if (FormatVersion != SaveFormat.CurrentVersion)
        {
            return ("format_version", $"must be {SaveFormat.CurrentVersion}, is {FormatVersion}");
        }

        if (string.IsNullOrWhiteSpace(GameVersion))
        {
            return ("game_version", "must not be empty");
        }

        if (!double.IsFinite(PlaySeconds) || PlaySeconds < 0)
        {
            return ("play_seconds", $"must be a finite number ≥ 0, is {SaveJson.Format(PlaySeconds)}");
        }

        if (!MapPack.IsValidMapId(MapId))
        {
            return ("map_id", $"'{MapId}' must be a lowercase slug (letters and digits joined by single '-' or '_')");
        }

        if (string.IsNullOrWhiteSpace(MapGeneratedAt))
        {
            return ("map_generated_at", "must not be empty");
        }

        if (Player is null)
        {
            return ("player", "is missing");
        }

        if (Player.Problem(requireFacingPair: true) is { } player)
        {
            return (SaveJson.Join("player", player.KeyPath), player.Problem);
        }

        if (Quests is null)
        {
            return ("quests", "is missing");
        }

        return null;
    }
}

/// <summary>
/// Quest progress in a save. Format version 1 holds nothing (<c>"quests": {}</c>); the quest system spec defines its
/// content and raises the format version. All instances are equal.
/// </summary>
public sealed record SaveQuests
{
    /// <summary>The empty quest progress of format version 1.</summary>
    public static SaveQuests Empty { get; } = new();
}

/// <summary>
/// The player's part of a save, made by <see cref="PlayerState.Snapshot"/> (which leaves <see cref="FacingYaw"/>
/// empty; the caller sets it from the player node with <c>with { FacingYaw = yaw }</c>) and applied by
/// <see cref="PlayerState.Restore"/>.
/// </summary>
/// <remarks>
/// Construction normalises: <see cref="FacingYaw"/> is wrapped into (−π, π] (a value already in it is kept as is;
/// a non-finite value is kept and fails <see cref="Problem"/>), and <see cref="Gear"/> is copied and sorted
/// ordinal. Nothing else is checked on construction; <see cref="Problem"/> holds the rules. Equality compares the
/// gear item by item.
/// </remarks>
public sealed record SavePlayer
{
    private readonly double? _facingYaw;
    private readonly IReadOnlyList<string> _gear = [];

    /// <exception cref="ArgumentNullException"><paramref name="gear"/> is null.</exception>
    public SavePlayer(LocalPoint? position, double? facingYaw, int money, IEnumerable<string> gear, int wantedLevel)
    {
        Position = position;
        FacingYaw = facingYaw;
        Money = money;
        Gear = gear.ToList();
        WantedLevel = wantedLevel;
    }

    /// <summary>The player's feet in Godot local metres; null for an empty position (a save written during a death).</summary>
    public LocalPoint? Position { get; init; }

    /// <summary>The player's yaw in radians, in (−π, π] once set; null exactly when <see cref="Position"/> is (in a save).</summary>
    public double? FacingYaw
    {
        get => _facingYaw;
        init => _facingYaw = value is { } yaw ? NormalizeYaw(yaw) : null;
    }

    /// <summary>Money, ≥ 0.</summary>
    public int Money { get; init; }

    /// <summary>Gear ids, non-blank, no duplicates, in ordinal order (sorted on construction).</summary>
    public IReadOnlyList<string> Gear
    {
        get => _gear;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Gear));
            var sorted = value.ToArray();
            Array.Sort(sorted, StringComparer.Ordinal);
            _gear = sorted;
        }
    }

    /// <summary>Wanted level, 0..<see cref="PlayerState.MaxWantedLevel"/>.</summary>
    public int WantedLevel { get; init; }

    /// <summary>
    /// Wraps <paramref name="yaw"/> into (−π, π]. A value already in that range is returned unchanged; a non-finite
    /// value is returned unchanged too.
    /// </summary>
    public static double NormalizeYaw(double yaw)
    {
        if (!double.IsFinite(yaw) || (yaw > -Math.PI && yaw <= Math.PI))
        {
            return yaw;
        }

        var wrapped = Math.IEEERemainder(yaw, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + (2 * Math.PI) : wrapped > Math.PI ? wrapped - (2 * Math.PI) : wrapped;
    }

    /// <summary>
    /// The rules of a valid player part, shared by <see cref="SaveReader"/>, <see cref="SaveWriter"/> and
    /// <see cref="PlayerState.Restore"/>: money ≥ 0; wanted level 0..<see cref="PlayerState.MaxWantedLevel"/>; gear
    /// ids not null or blank and not repeated; a finite position when present; a finite facing in (−π, π] when
    /// present; and a facing only with a position.
    /// </summary>
    /// <param name="requireFacingPair">
    /// True for a save (reader and writer): a position also needs a facing. False for
    /// <see cref="PlayerState.Restore"/>, which has no facing and accepts <see cref="PlayerState.Snapshot"/>'s output
    /// (a position with no facing) as it is; a facing without a position is an error either way.
    /// </param>
    /// <returns>Null when valid, else the key path relative to <c>player</c> and the problem.</returns>
    public (string KeyPath, string Problem)? Problem(bool requireFacingPair)
    {
        if (Money < 0)
        {
            return ("money", $"must be ≥ 0, is {Money}");
        }

        for (var i = 0; i < Gear.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Gear[i]))
            {
                return ($"gear[{i}]", "must be a non-blank string");
            }

            if (i > 0 && string.Equals(Gear[i], Gear[i - 1], StringComparison.Ordinal))
            {
                return ($"gear[{i}]", $"'{Gear[i]}' appears more than once");
            }
        }

        if (WantedLevel is < 0 or > PlayerState.MaxWantedLevel)
        {
            return ("wanted_level", $"must be 0..{PlayerState.MaxWantedLevel}, is {WantedLevel}");
        }

        if (Position is { } p && !(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)))
        {
            return ("position", $"must be finite, is {p}");
        }

        if (FacingYaw is { } yaw && !(double.IsFinite(yaw) && yaw > -Math.PI && yaw <= Math.PI))
        {
            return ("facing_yaw", $"must be a finite number in (-π, π], is {SaveJson.Format(yaw)}");
        }

        if (Position is null && FacingYaw is not null)
        {
            return ("facing_yaw", "must be null when position is null");
        }

        if (requireFacingPair && Position is not null && FacingYaw is null)
        {
            return ("facing_yaw", "must not be null when position isn't");
        }

        return null;
    }

    public bool Equals(SavePlayer? other) =>
        other is not null
        && Position == other.Position
        && Nullable.Equals(FacingYaw, other.FacingYaw)
        && Money == other.Money
        && WantedLevel == other.WantedLevel
        && Gear.SequenceEqual(other.Gear, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Position);
        hash.Add(FacingYaw);
        hash.Add(Money);
        hash.Add(WantedLevel);
        foreach (var item in Gear)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.GetHashCode();
    }
}
