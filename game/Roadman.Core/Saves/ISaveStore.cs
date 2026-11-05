namespace Roadman.Core.Saves;

/// <summary>
/// The files of the saves folder, by plain file name (no folders). The game implements it over <c>user://saves/</c>;
/// <see cref="MemorySaveStore"/> is the in-memory one for tests. Failures are <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>.
/// </summary>
public interface ISaveStore
{
    /// <summary>The names of the files in the folder (any order).</summary>
    IReadOnlyList<string> List();

    bool Exists(string name);

    /// <exception cref="FileNotFoundException">No such file.</exception>
    string ReadText(string name);

    /// <summary>Creates or replaces the file.</summary>
    void WriteText(string name, string text);

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/>, replacing <paramref name="to"/> if it exists.</summary>
    /// <exception cref="FileNotFoundException"><paramref name="from"/> doesn't exist.</exception>
    void Rename(string from, string to);

    /// <summary>Deletes the file; nothing happens when it doesn't exist.</summary>
    void Delete(string name);
}

/// <summary>
/// An in-memory <see cref="ISaveStore"/> for tests. <see cref="FailWrite"/>, <see cref="FailRename"/> and
/// <see cref="FailRead"/> make those operations throw an <see cref="IOException"/> without changing anything.
/// </summary>
public sealed class MemorySaveStore : ISaveStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>When true, <see cref="WriteText"/> throws and writes nothing.</summary>
    public bool FailWrite { get; set; }

    /// <summary>When true, <see cref="Rename"/> throws and renames nothing.</summary>
    public bool FailRename { get; set; }

    /// <summary>When true, <see cref="ReadText"/> throws.</summary>
    public bool FailRead { get; set; }

    /// <summary>The files and their text, for checks.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    public IReadOnlyList<string> List() => [.. _files.Keys];

    public bool Exists(string name) => _files.ContainsKey(name);

    public string ReadText(string name)
    {
        if (FailRead)
        {
            throw new IOException($"reading {name} failed (MemorySaveStore.FailRead)");
        }

        return _files.TryGetValue(name, out var text) ? text : throw new FileNotFoundException($"no file {name}", name);
    }

    public void WriteText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);
        if (FailWrite)
        {
            throw new IOException($"writing {name} failed (MemorySaveStore.FailWrite)");
        }

        _files[name] = text;
    }

    public void Rename(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(to);
        if (FailRename)
        {
            throw new IOException($"renaming {from} to {to} failed (MemorySaveStore.FailRename)");
        }

        if (!_files.Remove(from, out var text))
        {
            throw new FileNotFoundException($"no file {from}", from);
        }

        _files[to] = text;
    }

    public void Delete(string name) => _files.Remove(name);
}
