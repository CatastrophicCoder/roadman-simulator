using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.Hud;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

/// <summary>
/// The death screen's full-screen fade covers the whole viewport it is in (<c>docs/specs/world-edge-game.md</c>,
/// "Death screen"): in a <see cref="SubViewport"/>, after that viewport is resized, and after the screen is moved into
/// another viewport (as the <c>--death screen</c> screenshot does with its capture viewport).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class DeathScreenLayoutTest
{
    [TestCase]
    public async Task Background_FillsItsViewport_AfterAResizeAndAMoveIntoAnotherViewport()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var first = new SubViewport { Name = "DeathScreenFirst", Size = new Vector2I(1600, 900) };
        var second = new SubViewport { Name = "DeathScreenSecond", Size = new Vector2I(1024, 1280) };
        tree.Root.AddChild(first);
        tree.Root.AddChild(second);
        try
        {
            var screen = new DeathScreen { Name = "DeathScreen", FadeTime = 0f };
            first.AddChild(screen);
            screen.ShowLosses(DeathScreenText.Format(new DeathLosses(0, [])));
            await AssertFills(screen, first, "in a 1600×900 viewport");

            first.Size = new Vector2I(800, 1200);
            await AssertFills(screen, first, "after the viewport was resized to 800×1200");

            first.RemoveChild(screen);
            second.AddChild(screen);
            await AssertFills(screen, second, "after the move into a 1024×1280 viewport");
        }
        finally
        {
            first.QueueFree();
            second.QueueFree();
        }
    }

    private static async Task AssertFills(DeathScreen screen, SubViewport viewport, string when)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await screen.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await screen.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var expected = new Rect2(Vector2.Zero, viewport.GetVisibleRect().Size);
        AssertThat(screen.Background.GetGlobalRect())
            .OverrideFailureMessage($"{when}: the fade covers {screen.Background.GetGlobalRect()}, not {expected}")
            .IsEqual(expected);
        AssertThat(screen.Root.GetGlobalRect())
            .OverrideFailureMessage($"{when}: the root covers {screen.Root.GetGlobalRect()}, not {expected}")
            .IsEqual(expected);
    }
}
