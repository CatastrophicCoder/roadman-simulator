namespace Roadman.Core.Saves;

/// <summary>
/// The files of the saves folder, by plain file name (no folders). The game implements it over <c>user://saves/</c>;
/// the tests use an in-memory one. Failures are <see cref="IOException"/> or
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
