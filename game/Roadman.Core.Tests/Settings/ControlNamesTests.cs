using System.Text.RegularExpressions;
using Roadman.Core.Settings;
using Roadman.Core.Tests.Maps;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="ControlNames"/>: <c>docs/specs/menu-settings-save.md</c>, criteria 21 (every non-debug action in
/// <c>project.godot</c> is listed with a label; <c>pause</c> shows "Start button"; no gamepad name contains a console
/// maker's button names) and 25 (gamepad inputs are named generically). ADR 0016 and ADR 0018, "Gamepad names".
/// </summary>
public class ControlNamesTests
{
    /// <summary>
    /// Console makers' button, stick, trigger and controller names, and the makers and consoles themselves. Matched as
    /// whole words, case-insensitive. The generic names this game uses ("Back button", "Start button", "Guide button",
    /// "face button", "shoulder button", "trigger", "D-pad") are not on the list.
    /// </summary>
    private static readonly string[] ConsoleWords =
    [
        "A", "B", "X", "Y",
        "Cross", "Circle", "Square", "Triangle",
        "L1", "R1", "L2", "R2", "L3", "R3",
        "LB", "RB", "LT", "RT", "LS", "RS",
        "ZL", "ZR", "SL", "SR",
        "Xbox", "PlayStation", "PS", "PS4", "PS5", "DualShock", "DualSense",
        "Nintendo", "Switch", "Joy-Con", "Joycon", "Wii",
        "Options", "Share", "Create", "Select", "View", "Menu", "Home", "Capture", "Touchpad",
        "Minus", "Plus", "Steam", "Sony", "Microsoft",
    ];

    private static readonly Regex ConsoleWordPattern = new(
        @"(?<![\p{L}\p{N}])(" + string.Join("|", ConsoleWords.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ActionLine = new(@"^([A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.CultureInvariant);

    /// <summary>The action names in the <c>[input]</c> section of <c>game/project.godot</c>.</summary>
    private static List<string> ProjectActions()
    {
        var game = GeneratedPacks.GameDirectory();
        Assert.True(game is not null, "game/ (the folder holding RoadmanSimulator.sln) not found above the test assembly");
        var path = Path.Combine(game, "project.godot");
        Assert.True(File.Exists(path), $"{path} not found");
        var actions = new List<string>();
        var inInput = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inInput = line == "[input]";
                continue;
            }

            if (inInput && ActionLine.Match(line) is { Success: true } match)
            {
                actions.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(actions);
        return actions;
    }

    /// <summary>Godot's built-in <c>ui_*</c> actions, which <c>project.godot</c> overrides for the menus (not game controls).</summary>
    private static bool IsBuiltInUiAction(string action) => action.StartsWith("ui_", StringComparison.Ordinal);

    private static IEnumerable<string> AllGamepadNames()
    {
        for (var button = 0; button <= 20; button++)
        {
            yield return ControlNames.GamepadButtonName(button);
        }

        for (var axis = 0; axis <= 7; axis++)
        {
            yield return ControlNames.GamepadAxisName(axis, -1f);
            yield return ControlNames.GamepadAxisName(axis, 1f);
            yield return ControlNames.GamepadAxisName(axis, 0f);
        }
    }

    [Fact]
    public void Ac21_EveryGameActionInProjectGodot_HasALabelThatIsNotItsRawName()
    {
        var unlabelled = ProjectActions()
            .Where(a => !IsBuiltInUiAction(a))
            .Where(a => ControlNames.ActionLabel(a) == a || string.IsNullOrWhiteSpace(ControlNames.ActionLabel(a)))
            .ToList();

        Assert.True(unlabelled.Count == 0, $"actions without a label: {string.Join(", ", unlabelled)}");
    }

    [Fact]
    public void Ac21_EveryNonDebugActionInProjectGodot_IsInTheGameplayOrderOnce_AndNothingElseIs()
    {
        var gameplay = ProjectActions()
            .Where(a => !IsBuiltInUiAction(a) && !ControlNames.IsDebugAction(a))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(gameplay, ControlNames.GameplayActionOrder.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(ControlNames.GameplayActionOrder.Count, ControlNames.GameplayActionOrder.Distinct().Count());
    }

    [Fact]
    public void Ac21_GameplayOrder_HasNoDebugAction()
    {
        Assert.DoesNotContain(ControlNames.GameplayActionOrder, ControlNames.IsDebugAction);
    }

    [Fact]
    public void ActionLabels_AreDistinct()
    {
        var labels = ProjectActions().Where(a => !IsBuiltInUiAction(a)).Select(ControlNames.ActionLabel).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("move_forward", "Move forward")]
    [InlineData("run", "Run (hold)")]
    [InlineData("run_toggle", "Run toggle")]
    [InlineData("pause", "Pause")]
    [InlineData("debug_camera", "Debug camera")]
    [InlineData("not_an_action", "not_an_action")]
    public void ActionLabel_KnownGivesItsLabel_UnknownGivesItsName(string action, string label)
    {
        Assert.Equal(label, ControlNames.ActionLabel(action));
    }

    [Theory]
    [InlineData("debug_camera", true)]
    [InlineData("debug_orthophoto", true)]
    [InlineData("debug_road_graph", true)]
    [InlineData("debug_inspect", true)]
    [InlineData("debug_fast", true)]
    [InlineData("move_forward", false)]
    [InlineData("pause", false)]
    [InlineData("jump", false)]
    [InlineData("debug", false)]
    [InlineData("Debug_camera", false)]
    [InlineData("my_debug_action", false)]
    public void Ac21_IsDebugAction_IsTrueForDebugPrefixedActionsOnly(string action, bool debug)
    {
        Assert.Equal(debug, ControlNames.IsDebugAction(action));
    }

    [Fact]
    public void Ac21_EveryDebugActionInProjectGodot_IsADebugAction()
    {
        var debugNamed = ProjectActions().Where(a => a.Contains("debug", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(debugNamed);
        Assert.All(debugNamed, a => Assert.True(ControlNames.IsDebugAction(a), a));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ControlNames.ActionLabel(null!));
        Assert.Throws<ArgumentNullException>(() => ControlNames.IsDebugAction(null!));
    }

    [Theory]
    [InlineData(0, "South face button")]
    [InlineData(1, "East face button")]
    [InlineData(2, "West face button")]
    [InlineData(3, "North face button")]
    [InlineData(4, "Back button")]
    [InlineData(5, "Guide button")]
    [InlineData(6, "Start button")]
    [InlineData(7, "Left-stick click")]
    [InlineData(8, "Right-stick click")]
    [InlineData(9, "Left shoulder button")]
    [InlineData(10, "Right shoulder button")]
    [InlineData(11, "D-pad up")]
    [InlineData(12, "D-pad down")]
    [InlineData(13, "D-pad left")]
    [InlineData(14, "D-pad right")]
    [InlineData(15, "Button 15")]
    [InlineData(20, "Button 20")]
    [InlineData(-1, "Button -1")]
    public void Ac21_Ac25_GamepadButtonName_IsGeneric(int button, string name)
    {
        Assert.Equal(name, ControlNames.GamepadButtonName(button));
    }

    [Fact]
    public void Ac21_GamepadButtonNames0To14_AreDistinct()
    {
        var names = Enumerable.Range(0, 15).Select(ControlNames.GamepadButtonName).ToList();

        Assert.Equal(15, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, -1f, "Left stick left")]
    [InlineData(0, 1f, "Left stick right")]
    [InlineData(1, -1f, "Left stick up")]
    [InlineData(1, 1f, "Left stick down")]
    [InlineData(2, -1f, "Right stick left")]
    [InlineData(2, 1f, "Right stick right")]
    [InlineData(3, -1f, "Right stick up")]
    [InlineData(3, 1f, "Right stick down")]
    [InlineData(4, 1f, "Left trigger")]
    [InlineData(4, -1f, "Left trigger")]
    [InlineData(5, 1f, "Right trigger")]
    [InlineData(5, -1f, "Right trigger")]
    [InlineData(1, 0f, "Left stick down")]
    [InlineData(6, 1f, "Axis 6 +")]
    [InlineData(6, 0f, "Axis 6 +")]
    [InlineData(7, -1f, "Axis 7 −")]
    public void Ac21_Ac25_GamepadAxisName_IsGeneric(int axis, float sign, string name)
    {
        Assert.Equal(name, ControlNames.GamepadAxisName(axis, sign));
    }

    [Theory]
    [InlineData(1, "Left mouse button")]
    [InlineData(2, "Right mouse button")]
    [InlineData(3, "Middle mouse button")]
    [InlineData(4, "Mouse wheel up")]
    [InlineData(5, "Mouse wheel down")]
    [InlineData(8, "Mouse button 8")]
    [InlineData(0, "Mouse button 0")]
    public void MouseButtonName_NamesTheButton(int button, string name)
    {
        Assert.Equal(name, ControlNames.MouseButtonName(button));
    }

    [Fact]
    public void Ac21_Ac25_NoGamepadName_ContainsAConsoleMakersWord()
    {
        var offending = AllGamepadNames()
            .Select(name => (Name: name, Match: ConsoleWordPattern.Match(name)))
            .Where(x => x.Match.Success)
            .Select(x => $"'{x.Name}' ({x.Match.Value})")
            .ToList();

        Assert.True(offending.Count == 0, $"gamepad names with a console maker's word: {string.Join(", ", offending)}");
    }

    [Theory]
    [InlineData("Press A to jump")]
    [InlineData("R2")]
    [InlineData("Xbox button")]
    [InlineData("joy-con left")]
    [InlineData("Options button")]
    [InlineData("left (LB)")]
    public void ConsoleWordList_CatchesConsoleNames(string text)
    {
        Assert.Matches(ConsoleWordPattern, text);
    }

    [Theory]
    [InlineData("Start button")]
    [InlineData("Back button")]
    [InlineData("South face button")]
    [InlineData("Left-stick click")]
    [InlineData("Axis 6 +")]
    public void ConsoleWordList_LetsGenericNamesThrough(string text)
    {
        Assert.DoesNotMatch(ConsoleWordPattern, text);
    }

    [Fact]
    public void Ac21_PauseIsBoundToTheStartButton_Named()
    {
        // project.godot binds pause to button 6 (GamepadButtons.Start); the table names it "Start button".
        Assert.Equal("Start button", ControlNames.GamepadButtonName(6));
        Assert.Contains("pause", ProjectActions());
    }
}
