using System.Globalization;

namespace Roadman.Core.Tools;

/// <summary>
/// The optional <c>--seed &lt;n&gt;</c> user argument (after <c>--</c> on the Godot command line), which seeds the
/// player's spawn draws for tests and screenshots. It may stand with any other user arguments, including the
/// screenshot tool's (<see cref="ScreenshotArgs"/> skips it and its value).
/// </summary>
public static class SeedArg
{
    public const string Flag = "--seed";

    /// <summary>Finds <c>--seed &lt;n&gt;</c> among <paramref name="userArgs"/>.</summary>
    /// <returns>The seed, or null when <c>--seed</c> isn't given.</returns>
    /// <exception cref="ArgumentException">
    /// <c>--seed</c> is given more than once, or its value is missing or not a 32-bit integer.
    /// </exception>
    public static int? Parse(IReadOnlyList<string> userArgs)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        int? seed = null;
        for (var i = 0; i < userArgs.Count; i++)
        {
            if (userArgs[i] != Flag)
            {
                continue;
            }

            if (seed is not null)
            {
                throw new ArgumentException($"{Flag} is given more than once");
            }

            seed = ParseValue(userArgs, i + 1);
            i++;
        }

        return seed;
    }

    /// <summary>The value of <c>--seed</c> at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentException">The value is missing or not a 32-bit integer.</exception>
    public static int ParseValue(IReadOnlyList<string> userArgs, int index)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        if (index >= userArgs.Count || string.IsNullOrWhiteSpace(userArgs[index]))
        {
            throw new ArgumentException($"{Flag} needs an integer value");
        }

        var text = userArgs[index];
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"{Flag} needs an integer value from {int.MinValue} to {int.MaxValue}, got '{text}'"));
        }

        return value;
    }
}
