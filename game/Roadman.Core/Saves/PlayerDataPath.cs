namespace Roadman.Core.Saves;

/// <summary>
/// Which paths may hold the player's data (the saves folder, the settings file): a <c>user://</c> path or an absolute
/// path. Never a <c>res://</c> path, so player data can't go into the project or the map packs; never another
/// scheme; never a relative path, which would resolve against the working directory (the project folder under
/// <c>godot --path</c>).
/// </summary>
public static class PlayerDataPath
{
    public const string UserScheme = "user://";

    public const string ResScheme = "res://";

    /// <summary>Why <paramref name="path"/> can't hold player data; null when it can.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "the path is empty";
        }

        if (path.StartsWith(UserScheme, StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith(ResScheme, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{path}' is in the project (res://); player data goes in user:// or an absolute folder";
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return $"'{path}' has a scheme other than user://";
        }

        return Path.IsPathFullyQualified(path)
            ? null
            : $"'{path}' is a relative path; player data goes in user:// or an absolute folder";
    }
}
