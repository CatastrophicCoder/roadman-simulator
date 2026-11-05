using System.Text;

namespace Roadman.Core.IO;

/// <summary>File writes that reach the disk before they return, for the write-then-rename of saves and settings.</summary>
public static class DurableFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Creates or replaces <paramref name="path"/> with <paramref name="text"/> in UTF-8 without a byte order mark
    /// (as <see cref="File.WriteAllText(string, string?)"/>), and flushes it to disk
    /// (<see cref="FileStream.Flush(bool)"/> with <c>flushToDisk</c>) before closing it.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or folder isn't writable.</exception>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = Utf8NoBom.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
