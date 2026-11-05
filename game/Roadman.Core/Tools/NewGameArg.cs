namespace Roadman.Core.Tools;

/// <summary>
/// The <c>--new-game</c> user argument (after <c>--</c> on the Godot command line): <c>Main</c> starts a new game at
/// once, skipping the main menu (<c>docs/specs/menu-settings-save.md</c>, "Startup and scenes"). It takes no value
/// and ignores every other argument, so it stands with <c>--seed &lt;n&gt;</c> (<see cref="SeedArg"/>). It isn't
/// meant to be combined with <c>--screenshot</c> or <c>--perf</c>: those parsers (<see cref="ScreenshotArgs"/>,
/// <see cref="PerfArgs"/>) reject arguments they don't know, <c>--new-game</c> included.
/// </summary>
public static class NewGameArg
{
    public const string Flag = "--new-game";

    /// <returns>True when <c>--new-game</c> is among <paramref name="userArgs"/>.</returns>
    /// <exception cref="ArgumentException"><c>--new-game</c> is given more than once.</exception>
    public static bool Parse(IReadOnlyList<string> userArgs)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        var count = userArgs.Count(arg => arg == Flag);
        return count > 1 ? throw new ArgumentException($"{Flag} is given more than once") : count == 1;
    }
}
