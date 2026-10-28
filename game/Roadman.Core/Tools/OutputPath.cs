namespace Roadman.Core.Tools;

/// <summary>
/// Resolves a tool's output path the way a command-line user expects: relative to the directory the command was
/// run from. Godot changes the working directory to the project folder when started with <c>--path</c>, so the
/// process's own working directory can't be used. The shell's <c>PWD</c> variable still holds the original one.
/// </summary>
public static class OutputPath
{
    /// <summary>
    /// Returns <paramref name="path"/> as an absolute, normalised path.
    /// </summary>
    /// <param name="path">The path as given on the command line.</param>
    /// <param name="launchDirectory">The directory the command was run from, usually the <c>PWD</c> environment variable; may be null.</param>
    /// <exception cref="ArgumentException">
    /// The path is relative and <paramref name="launchDirectory"/> isn't an absolute path (for example, <c>PWD</c>
    /// isn't set by Windows shells). Pass an absolute output path then.
    /// </exception>
    public static string Resolve(string path, string? launchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        if (string.IsNullOrWhiteSpace(launchDirectory) || !Path.IsPathFullyQualified(launchDirectory))
        {
            throw new ArgumentException(
                $"can't resolve the relative path '{path}': the launch directory is unknown (PWD isn't set); pass an absolute path");
        }

        return Path.GetFullPath(Path.Combine(launchDirectory, path));
    }
}
