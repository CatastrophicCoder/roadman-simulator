using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Roadman.Core.IO;
using Roadman.Core.Saves;

namespace Roadman.Saves;

/// <summary>
/// An <see cref="ISaveStore"/> over one folder of the file system: by default <see cref="DefaultFolder"/>
/// (<c>user://saves/</c>), or any absolute folder (tests point it at a temporary one); never a <c>res://</c> folder
/// (<see cref="PlayerDataPath"/>). The folder is created on the first write; listing a folder that doesn't exist gives
/// no files. Writes are flushed to disk before they return (<see cref="DurableFile"/>), so the rename after them
/// never publishes a file whose data isn't on disk. Plain file names only.
/// </summary>
public sealed class FileSaveStore : ISaveStore
{
    /// <summary>The saves folder in the user data folder.</summary>
    public const string DefaultFolder = "user://saves";

    /// <param name="folder">
    /// An absolute folder, or a <c>user://</c> path (globalized); null or empty is <see cref="DefaultFolder"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="folder"/> is a <c>res://</c> path, has another scheme or is relative (<see cref="PlayerDataPath"/>).
    /// </exception>
    public FileSaveStore(string? folder = null)
    {
        var path = string.IsNullOrEmpty(folder) ? DefaultFolder : folder;
        if (PlayerDataPath.Problem(path) is { } problem)
        {
            throw new ArgumentException($"not a saves folder: {problem}", nameof(folder));
        }

        Folder = path.StartsWith(PlayerDataPath.UserScheme, StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
    }

    /// <summary>The absolute folder.</summary>
    public string Folder { get; }

    public IReadOnlyList<string> List() =>
        Directory.Exists(Folder)
            ? [.. Directory.EnumerateFiles(Folder).Select(Path.GetFileName).OfType<string>()]
            : [];

    public bool Exists(string name) => File.Exists(PathOf(name));

    public string ReadText(string name) => File.ReadAllText(PathOf(name));

    public void WriteText(string name, string text)
    {
        Directory.CreateDirectory(Folder);
        DurableFile.WriteAllText(PathOf(name), text);
    }

    public void Rename(string from, string to)
    {
        var source = PathOf(from);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"no such save file: {from}", source);
        }

        File.Move(source, PathOf(to), overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathOf(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name))
        {
            throw new ArgumentException($"'{name}' is not a plain file name", nameof(name));
        }

        return Path.Combine(Folder, name);
    }
}
