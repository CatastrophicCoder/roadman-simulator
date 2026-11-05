using System.Text;
using Roadman.Core.IO;

namespace Roadman.Core.Tests.IO;

/// <summary>
/// <see cref="DurableFile"/>, the flushed write under the write-then-rename of saves and settings
/// (<c>docs/specs/menu-settings-save.md</c>, criteria 3 and 17).
/// </summary>
public sealed class DurableFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"roadman-durable-{Guid.NewGuid():N}");

    public DurableFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void WriteAllText_NewFile_ReadsBackEqual()
    {
        var path = Path.Combine(_folder, "a.json");
        const string text = "{ \"name\": \"Härkälä – ÅÄÖ €\" }\n";

        DurableFile.WriteAllText(path, text);

        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_IsUtf8WithoutByteOrderMark()
    {
        var path = Path.Combine(_folder, "a.json");
        const string text = "ä{}";

        DurableFile.WriteAllText(path, text);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new UTF8Encoding(false).GetBytes(text), bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the file starts with a BOM");
    }

    [Fact]
    public void WriteAllText_ExistingLongerFile_IsReplacedNotAppendedOrPadded()
    {
        var path = Path.Combine(_folder, "a.json");
        File.WriteAllText(path, new string('x', 1000));

        DurableFile.WriteAllText(path, "short");

        Assert.Equal("short", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_EmptyText_GivesAnEmptyFile()
    {
        var path = Path.Combine(_folder, "empty.json");

        DurableFile.WriteAllText(path, "");

        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllText_MissingFolder_Throws()
    {
        var path = Path.Combine(_folder, "missing", "a.json");

        Assert.ThrowsAny<IOException>(() => DurableFile.WriteAllText(path, "x"));
    }

    [Fact]
    public void WriteAllText_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => DurableFile.WriteAllText(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => DurableFile.WriteAllText(Path.Combine(_folder, "a"), null!));
    }
}
