using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

public class OutputPathTests
{
    private static readonly string LaunchDir = Path.Combine(Path.GetTempPath(), "repo");

    [Fact]
    public void Resolve_RelativePath_UsesLaunchDirectory()
    {
        var resolved = OutputPath.Resolve("build/screenshots/overview.png", LaunchDir);

        Assert.Equal(Path.Combine(LaunchDir, "build", "screenshots", "overview.png"), resolved);
    }

    [Fact]
    public void Resolve_RelativePathWithDots_IsNormalised()
    {
        var resolved = OutputPath.Resolve("./a/../shot.png", LaunchDir);

        Assert.Equal(Path.Combine(LaunchDir, "shot.png"), resolved);
    }

    [Fact]
    public void Resolve_AbsolutePath_IgnoresLaunchDirectory()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "shot.png");

        Assert.Equal(absolute, OutputPath.Resolve(absolute, null));
        Assert.Equal(absolute, OutputPath.Resolve(absolute, LaunchDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/dir")]
    public void Resolve_RelativePathWithoutUsableLaunchDirectory_Throws(string? launchDirectory)
    {
        var ex = Assert.Throws<ArgumentException>(() => OutputPath.Resolve("shot.png", launchDirectory));
        Assert.Contains("absolute path", ex.Message);
    }
}
