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

    /// <summary>The near-opaque full-screen fade behind the death screen's panel.</summary>
    public static readonly Color Shade = new(0.02f, 0.023f, 0.03f, 0.92f);

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

    // The minimap's live layers (docs/specs/minimap.md, "Style"): flat placeholder colours until the art-direction
    // palette exists. The background image's colours are the pipeline's (tools/mapgen, minimap step); the ground colour
    // here matches its "ground" entry, so a pack without the image still reads the same.

    /// <summary>The minimap's void outside the world edge.</summary>
    public static readonly Color MinimapVoid = new(0.07f, 0.08f, 0.11f);

    /// <summary>The minimap's ground where there is no background image (the pipeline's ground colour).</summary>
    public static readonly Color MinimapGround = new(218 / 255f, 214 / 255f, 196 / 255f);

    /// <summary>The boundary ring line.</summary>
    public static readonly Color MinimapRing = new(0.16f, 0.17f, 0.2f);

    /// <summary>The roadblock bars across the road at the edge.</summary>
    public static readonly Color MinimapRoadblock = new(0.82f, 0.2f, 0.18f);

    /// <summary>Main roads (<c>primary</c>, <c>secondary</c>).</summary>
    public static readonly Color MinimapMainRoad = new(0.27f, 0.28f, 0.33f);

    /// <summary>Roads (<c>tertiary</c>, <c>residential</c> and the like).</summary>
    public static readonly Color MinimapRoad = new(0.4f, 0.41f, 0.46f);

    /// <summary>Minor drivable roads (<c>service</c>, <c>driveway</c> and the like).</summary>
    public static readonly Color MinimapMinorRoad = new(0.52f, 0.53f, 0.57f);

    /// <summary>Paths (<c>cycleway</c>, <c>footway</c> and the like); drawn fainter (<c>MinimapStyle.StrokeOf</c>).</summary>
    public static readonly Color MinimapPath = new(0.46f, 0.4f, 0.33f);

    /// <summary>Street label text; outlined with <see cref="Shade"/>.</summary>
    public static readonly Color MinimapLabel = new(0.97f, 0.97f, 0.96f);

    /// <summary>Shop symbols (squares).</summary>
    public static readonly Color MinimapShop = new(0.8f, 0.3f, 0.55f);

    /// <summary>Food and drink symbols (circles).</summary>
    public static readonly Color MinimapFood = new(0.9f, 0.48f, 0.12f);

    /// <summary>Public service symbols (diamonds).</summary>
    public static readonly Color MinimapPublicService = new(0.45f, 0.32f, 0.72f);

    /// <summary>Parking symbols (rounded squares with a letter).</summary>
    public static readonly Color MinimapParking = new(0.2f, 0.42f, 0.8f);

    /// <summary>Other points of interest (small dots).</summary>
    public static readonly Color MinimapOtherPoi = new(0.32f, 0.33f, 0.38f);

    /// <summary>Markers (the marker layer; empty until the quest system adds some).</summary>
    public static readonly Color MinimapMarker = new(0.98f, 0.86f, 0.2f);

    public const int FontSize = 22;

    public const int TitleFontSize = 44;

    public const int HeadingFontSize = 30;

    public const int SmallFontSize = 18;

    /// <summary>Text size of read-only tables (the controls tab).</summary>
    public const int TableFontSize = 16;

    /// <summary>Theme type variation of the big title label.</summary>
    public const string TitleLabel = "TitleLabel";

    /// <summary>Theme type variation of a heading label (sub-view and dialog titles).</summary>
    public const string HeadingLabel = "HeadingLabel";

    /// <summary>Theme type variation of hint and status texts.</summary>
    public const string HintLabel = "HintLabel";

    /// <summary>Theme type variation of the settings view's tab buttons: the selected (pressed) one has an accent bar.</summary>
    public const string TabButton = "TabButton";

    /// <summary>Theme type variation of the cells of a read-only table (the controls tab).</summary>
    public const string TableLabel = "TableLabel";

    /// <summary>Theme type variation of a table's column headings and group headings.</summary>
    public const string TableHeading = "TableHeading";

    /// <summary>
    /// Side of the square reserved for an item icon in the inventory screen's rows (drawn as nothing until icons
    /// exist; <c>docs/specs/inventory.md</c>, "Layout").
    /// </summary>
    public const float ItemIconSize = 48f;

    /// <summary>Text size of a section heading inside a view (the inventory screen's Equipped, Items and Debug).</summary>
    public const int SectionFontSize = 24;

    /// <summary>Theme type variation of a section heading inside a view.</summary>
    public const string SectionLabel = "SectionLabel";

    /// <summary>Theme type variation of a short accent tag after a name (the inventory screen's "Equipped").</summary>
    public const string TagLabel = "TagLabel";

    /// <summary>The panel colour without transparency, for pop-up lists over other panels.</summary>
    public static readonly Color PopupPanel = new(0.1f, 0.115f, 0.135f);

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

        theme.SetTypeVariation(SectionLabel, "Label");
        theme.SetFontSize("font_size", SectionLabel, SectionFontSize);
        theme.SetColor("font_color", SectionLabel, Text);

        theme.SetTypeVariation(TagLabel, "Label");
        theme.SetFontSize("font_size", TagLabel, SmallFontSize);
        theme.SetColor("font_color", TagLabel, Accent);

        theme.SetTypeVariation(TabButton, "Button");
        theme.SetStylebox("pressed", TabButton, AccentBar(ButtonPressed));
        theme.SetStylebox("hover_pressed", TabButton, AccentBar(ButtonPressed));
        theme.SetColor("font_pressed_color", TabButton, Accent);
        theme.SetColor("font_hover_pressed_color", TabButton, Accent);

        theme.SetTypeVariation(TableLabel, "Label");
        theme.SetFontSize("font_size", TableLabel, TableFontSize);
        theme.SetColor("font_color", TableLabel, Text);

        theme.SetTypeVariation(TableHeading, "Label");
        theme.SetFontSize("font_size", TableHeading, TableFontSize);
        theme.SetColor("font_color", TableHeading, Accent);

        // Toggles: the switch icon shows the state, so the background stays the normal button's.
        theme.SetStylebox("pressed", "CheckButton", Flat(ButtonNormal, margin: 10));
        theme.SetStylebox("hover_pressed", "CheckButton", Flat(ButtonHover, margin: 10));
        theme.SetColor("font_pressed_color", "CheckButton", Text);
        theme.SetColor("font_hover_pressed_color", "CheckButton", Text);

        // Sliders: a flat track, the filled part and the grabber area in the accent colour, the same focus outline.
        theme.SetStylebox("slider", "HSlider", Track(ButtonNormal));
        theme.SetStylebox("grabber_area", "HSlider", Track(Accent));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Track(Accent));
        theme.SetStylebox("focus", "HSlider", FocusOutline());

        // The option buttons' pop-up lists: an opaque flat panel, the hovered item in the hover colour.
        theme.SetStylebox("panel", "PopupMenu", Flat(PopupPanel, margin: 6));
        theme.SetStylebox("hover", "PopupMenu", Flat(ButtonHover, margin: 6));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Accent);
        theme.SetFontSize("font_size", "PopupMenu", FontSize);

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

    /// <summary>A flat button background with a 4 px accent bar along its bottom edge (the selected tab).</summary>
    private static StyleBoxFlat AccentBar(Color color)
    {
        var box = Flat(color, margin: 10);
        box.BorderColor = Accent;
        box.BorderWidthBottom = 4;
        return box;
    }

    /// <summary>A slider track 6 px high.</summary>
    private static StyleBoxFlat Track(Color color) =>
        new()
        {
            BgColor = color,
            ContentMarginTop = 3,
            ContentMarginBottom = 3,
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
