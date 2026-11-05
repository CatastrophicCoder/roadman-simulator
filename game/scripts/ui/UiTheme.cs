using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Roadman.Ui;

/// <summary>
/// The one UI <see cref="Theme"/> (<c>docs/specs/menu-settings-save.md</c>, "UI style"), built in code: flat panels
/// with square corners, a dark translucent panel colour, one accent colour for focus and the selected item, clear
/// focus outlines for gamepad use, readable text sizes, Godot's default font. The colours are placeholders kept here,
/// in one place, until the art direction sets the real UI style.
/// </summary>
public static class UiTheme
{
    /// <summary>The flat background behind the main menu and the loading screen.</summary>
    public static readonly Color Background = new(0.075f, 0.085f, 0.1f);

    /// <summary>Panels and dialogs.</summary>
    public static readonly Color Panel = new(0.1f, 0.115f, 0.135f, 0.94f);

    /// <summary>The dimming behind a dialog and over the paused game.</summary>
    public static readonly Color Dim = new(0f, 0f, 0f, 0.55f);

    /// <summary>The accent: focus outlines, hovered and pressed buttons.</summary>
    public static readonly Color Accent = new(0.96f, 0.68f, 0.18f);

    public static readonly Color ButtonNormal = new(0.18f, 0.2f, 0.23f);

    public static readonly Color ButtonHover = new(0.24f, 0.265f, 0.3f);

    public static readonly Color ButtonPressed = new(0.32f, 0.26f, 0.14f);

    public static readonly Color ButtonDisabled = new(0.13f, 0.14f, 0.16f);

    public static readonly Color Text = new(0.93f, 0.94f, 0.95f);

    public static readonly Color TextMuted = new(0.62f, 0.65f, 0.7f);

    public static readonly Color TextDisabled = new(0.42f, 0.44f, 0.48f);

    public const int FontSize = 22;

    public const int TitleFontSize = 44;

    public const int HeadingFontSize = 30;

    public const int SmallFontSize = 18;

    /// <summary>Theme type variation of the big title label.</summary>
    public const string TitleLabel = "TitleLabel";

    /// <summary>Theme type variation of a heading label (sub-view and dialog titles).</summary>
    public const string HeadingLabel = "HeadingLabel";

    /// <summary>Theme type variation of hint and status texts.</summary>
    public const string HintLabel = "HintLabel";

    private static Theme? _theme;

    /// <summary>The shared theme (built on first use).</summary>
    public static Theme Theme => _theme ??= Build();

    private static Theme Build()
    {
        var theme = new Theme { DefaultFontSize = FontSize };

        theme.SetStylebox("panel", "PanelContainer", Flat(Panel, margin: 28));
        theme.SetStylebox("panel", "Panel", Flat(Panel, margin: 28));

        theme.SetStylebox("normal", "Button", Flat(ButtonNormal, margin: 10));
        theme.SetStylebox("hover", "Button", Flat(ButtonHover, margin: 10));
        theme.SetStylebox("pressed", "Button", Flat(ButtonPressed, margin: 10));
        theme.SetStylebox("hover_pressed", "Button", Flat(ButtonPressed, margin: 10));
        theme.SetStylebox("disabled", "Button", Flat(ButtonDisabled, margin: 10));
        theme.SetStylebox("focus", "Button", FocusOutline());
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", Text);
        theme.SetColor("font_pressed_color", "Button", Accent);
        theme.SetColor("font_hover_pressed_color", "Button", Accent);
        theme.SetColor("font_focus_color", "Button", Text);
        theme.SetColor("font_disabled_color", "Button", TextDisabled);

        theme.SetColor("font_color", "Label", Text);

        theme.SetTypeVariation(TitleLabel, "Label");
        theme.SetFontSize("font_size", TitleLabel, TitleFontSize);
        theme.SetColor("font_color", TitleLabel, Text);

        theme.SetTypeVariation(HeadingLabel, "Label");
        theme.SetFontSize("font_size", HeadingLabel, HeadingFontSize);
        theme.SetColor("font_color", HeadingLabel, Text);

        theme.SetTypeVariation(HintLabel, "Label");
        theme.SetFontSize("font_size", HintLabel, SmallFontSize);
        theme.SetColor("font_color", HintLabel, TextMuted);

        theme.SetConstant("separation", "VBoxContainer", 12);
        theme.SetConstant("separation", "HBoxContainer", 12);
        return theme;
    }

    private static StyleBoxFlat Flat(Color color, int margin) =>
        new()
        {
            BgColor = color,
            ContentMarginLeft = margin * 1.6f,
            ContentMarginRight = margin * 1.6f,
            ContentMarginTop = margin,
            ContentMarginBottom = margin,
            AntiAliasing = false,
        };

    /// <summary>A 3 px accent outline drawn over the button, outside its edge, with no fill.</summary>
    private static StyleBoxFlat FocusOutline() =>
        new()
        {
            DrawCenter = false,
            BorderColor = Accent,
            BorderWidthLeft = 3,
            BorderWidthRight = 3,
            BorderWidthTop = 3,
            BorderWidthBottom = 3,
            ExpandMarginLeft = 3,
            ExpandMarginRight = 3,
            ExpandMarginTop = 3,
            ExpandMarginBottom = 3,
            AntiAliasing = false,
        };
}

/// <summary>Focus helpers shared by the menus: explicit neighbours so focus never leaves a menu or dialog.</summary>
public static class UiFocus
{
    /// <summary>Whether <paramref name="control"/> can take keyboard or gamepad focus now.</summary>
    public static bool IsFocusable(Control control) =>
        GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && control.FocusMode != Control.FocusModeEnum.None
        && !(control is BaseButton { Disabled: true });

    /// <summary>
    /// Enables or disables <paramref name="button"/>; a disabled button isn't focusable (and loses the focus).
    /// </summary>
    public static void SetEnabled(BaseButton button, bool enabled)
    {
        button.Disabled = !enabled;
        button.FocusMode = enabled ? Control.FocusModeEnum.All : Control.FocusModeEnum.None;
        if (!enabled && button.HasFocus())
        {
            button.ReleaseFocus();
        }
    }

    /// <summary>
    /// Links the focusable controls of <paramref name="rows"/> as a grid: up and down move between rows (to the same
    /// column, or the row's last control), left and right within a row; every edge wraps; next and previous follow
    /// reading order. Controls that can't take focus are skipped.
    /// </summary>
    /// <returns>The first focusable control, or null.</returns>
    public static Control? LinkGrid(IEnumerable<IEnumerable<Control>> rows)
    {
        var grid = rows.Select(row => row.Where(IsFocusable).ToList()).Where(row => row.Count > 0).ToList();
        var flat = grid.SelectMany(row => row).ToList();
        for (var r = 0; r < grid.Count; r++)
        {
            var row = grid[r];
            var up = grid[(r - 1 + grid.Count) % grid.Count];
            var down = grid[(r + 1) % grid.Count];
            for (var c = 0; c < row.Count; c++)
            {
                var control = row[c];
                Link(control, Side.Top, up[System.Math.Min(c, up.Count - 1)]);
                Link(control, Side.Bottom, down[System.Math.Min(c, down.Count - 1)]);
                Link(control, Side.Left, row[(c - 1 + row.Count) % row.Count]);
                Link(control, Side.Right, row[(c + 1) % row.Count]);
                var index = flat.IndexOf(control);
                control.FocusNext = control.GetPathTo(flat[(index + 1) % flat.Count]);
                control.FocusPrevious = control.GetPathTo(flat[(index - 1 + flat.Count) % flat.Count]);
            }
        }

        return flat.Count > 0 ? flat[0] : null;
    }

    /// <summary>Links <paramref name="controls"/> as one column (<see cref="LinkGrid"/> with one control per row).</summary>
    public static Control? LinkColumn(IEnumerable<Control> controls) => LinkGrid(controls.Select(c => new[] { c }));

    /// <summary>Links <paramref name="controls"/> as one row.</summary>
    public static Control? LinkRow(IEnumerable<Control> controls) => LinkGrid([controls]);

    private static void Link(Control control, Side side, Control target) =>
        control.SetFocusNeighbor(side, control.GetPathTo(target));
}
