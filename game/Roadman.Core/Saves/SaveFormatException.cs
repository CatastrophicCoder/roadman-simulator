namespace Roadman.Core.Saves;

/// <summary>
/// A save file can't be read: it isn't JSON, a key is missing, unknown, mistyped or out of range, or a newer version
/// of the game wrote it. <see cref="KeyPath"/> names the offending key (for example <c>player.money</c> or
/// <c>player.gear[2]</c>); the message starts with it in quotes.
/// </summary>
public sealed class SaveFormatException : Exception
{
    public SaveFormatException(string? keyPath, string message, bool newerVersion = false, Exception? innerException = null)
        : base(message, innerException)
    {
        KeyPath = keyPath;
        NewerVersion = newerVersion;
    }

    /// <summary>
    /// The key path of the problem (dot-separated, array indices in brackets), or null when the problem is the file
    /// as a whole (not JSON, the top level not an object).
    /// </summary>
    public string? KeyPath { get; }

    /// <summary>True when the file's <c>format_version</c> is newer than <see cref="SaveFormat.CurrentVersion"/>.</summary>
    public bool NewerVersion { get; }
}
