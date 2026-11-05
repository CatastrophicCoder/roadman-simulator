using Roadman.Core.IO;

namespace Roadman.Core.Settings;

/// <summary>
/// Reads and writes the settings file (<c>user://settings.json</c> in the game; the caller resolves the path).
/// </summary>
public static class SettingsFile
{
    /// <summary>
    /// Reads <paramref name="path"/>: no file gives <see cref="GameSettings.Defaults"/> for
    /// <paramref name="usableScreen"/> and no warnings; a file that can't be read (an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>) gives the defaults and one warning "could not read the file
    /// (&lt;reason&gt;); using the defaults"; else <see cref="SettingsReader.Read"/> of its text. Never writes.
    /// </summary>
    /// <param name="path">An absolute file path.</param>
    /// <param name="usableScreen">The screen's usable area, or null when unknown.</param>
    public static SettingsReadResult Load(string path, WindowSize? usableScreen)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return new SettingsReadResult(GameSettings.Defaults(usableScreen), []);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsReadResult(
                GameSettings.Defaults(usableScreen),
                [$"could not read the file ({ex.Message}); using the defaults"]);
        }

        return SettingsReader.Read(text, usableScreen);
    }

    /// <summary>
    /// Writes <paramref name="settings"/> (<see cref="SettingsWriter.Write"/>) to <paramref name="path"/>: creates its
    /// folder, writes <c>&lt;path&gt;.tmp</c> flushed to disk (<see cref="DurableFile.WriteAllText"/>), then renames it
    /// over <paramref name="path"/>.
    /// </summary>
    /// <param name="path">An absolute file path.</param>
    /// <param name="settings">The settings to write.</param>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder isn't writable.</exception>
    public static void Save(string path, GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var temp = path + ".tmp";
        DurableFile.WriteAllText(temp, SettingsWriter.Write(settings));
        File.Move(temp, path, overwrite: true);
    }
}
