namespace Roadman.Core.Saves;

/// <summary>
/// Builds the save of a running game from its parts (<c>docs/specs/menu-settings-save.md</c>, "Saving"). The game
/// looks the parts up in the engine; the rules are here.
/// </summary>
public static class SaveFactory
{
    /// <summary>The <see cref="SaveGame.GameVersion"/> when the game has no version set.</summary>
    public const string DevVersion = "dev";

    /// <summary>The <see cref="SaveGame.MapGeneratedAt"/> when the manifest has no <c>generated_at</c>.</summary>
    public const string UnknownGeneratedAt = "unknown";

    /// <summary><paramref name="gameVersion"/>, or <see cref="DevVersion"/> when it is null, empty or blank.</summary>
    public static string GameVersionOrDev(string? gameVersion) =>
        string.IsNullOrWhiteSpace(gameVersion) ? DevVersion : gameVersion;

    /// <summary>
    /// The save: the player snapshot with its position and <paramref name="facingYaw"/> only when
    /// <paramref name="canSave"/> (the player is alive), else with neither; <paramref name="generatedAt"/> or
    /// <see cref="UnknownGeneratedAt"/>; <see cref="GameVersionOrDev"/>; <see cref="SaveFormat.CurrentVersion"/> and
    /// <see cref="SaveQuests.Empty"/>. Null for a manual save (<paramref name="autosave"/> false) when
    /// <paramref name="canSave"/> is false. The result isn't validated (<see cref="SaveGame.Problem"/>): the writer
    /// does that.
    /// </summary>
    /// <param name="autosave">True for the autosave, which is written in every death state.</param>
    /// <param name="canSave">True when a manual save is possible now (alive with a player).</param>
    /// <param name="player">The player's state.</param>
    /// <param name="facingYaw">The player's facing in radians, or null when there is no player node.</param>
    /// <param name="mapId">The map id.</param>
    /// <param name="generatedAt">The manifest's <c>generated_at</c>, or null without a manifest.</param>
    /// <param name="gameVersion">The game's <c>application/config/version</c>.</param>
    /// <param name="nowUtc">The save time.</param>
    /// <param name="playSeconds">The play time in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="mapId"/> is null.</exception>
    public static SaveGame? Make(
        bool autosave,
        bool canSave,
        PlayerState player,
        double? facingYaw,
        string mapId,
        string? generatedAt,
        string? gameVersion,
        DateTimeOffset nowUtc,
        double playSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(mapId);
        if (!autosave && !canSave)
        {
            return null;
        }

        var snapshot = player.Snapshot(includePosition: canSave);
        if (canSave && facingYaw is { } yaw)
        {
            snapshot = snapshot with { FacingYaw = yaw };
        }

        return new SaveGame(
            SaveFormat.CurrentVersion,
            GameVersionOrDev(gameVersion),
            nowUtc,
            playSeconds,
            mapId,
            string.IsNullOrWhiteSpace(generatedAt) ? UnknownGeneratedAt : generatedAt,
            snapshot,
            SaveQuests.Empty);
    }
}
