using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary><see cref="PlayerDataPath"/>: player data never goes into the project (<c>res://</c>).</summary>
public class PlayerDataPathTests
{
    [Theory]
    [InlineData("user://saves")]
    [InlineData("user://settings.json")]
    public void Problem_UserPaths_AreAccepted(string path)
    {
        Assert.Null(PlayerDataPath.Problem(path));
    }

    [Fact]
    public void Problem_AnAbsolutePath_IsAccepted()
    {
        Assert.Null(PlayerDataPath.Problem(Path.Combine(Path.GetTempPath(), "roadman", "saves")));
    }

    [Theory]
    [InlineData("res://saves")]
    [InlineData("res://maps/somero/saves")]
    [InlineData("RES://saves")]
    public void Problem_ResPaths_AreRejectedNamingTheProject(string path)
    {
        var problem = PlayerDataPath.Problem(path);
        Assert.NotNull(problem);
        Assert.Contains("res://", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.invalid/saves")]
    [InlineData("uid://abc")]
    public void Problem_OtherSchemes_AreRejected(string path)
    {
        Assert.NotNull(PlayerDataPath.Problem(path));
    }

    [Theory]
    [InlineData("saves")]
    [InlineData("maps/somero")]
    [InlineData("")]
    [InlineData(null)]
    public void Problem_RelativeOrEmptyPaths_AreRejected(string? path)
    {
        Assert.NotNull(PlayerDataPath.Problem(path));
    }
}
