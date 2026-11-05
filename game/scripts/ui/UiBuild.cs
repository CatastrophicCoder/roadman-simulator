using Godot;

namespace Roadman.Ui;

/// <summary>Small node-tree builders shared by the menus.</summary>
public static class UiBuild
{
    /// <summary>
    /// A full-screen control that ignores the mouse, with a centred panel holding a vertical box; returns the box.
    /// </summary>
    public static VBoxContainer CentredPanel(Control parent, string name, float minWidth)
    {
        var centre = new CenterContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(centre);
        var panel = new PanelContainer { Name = "Panel", CustomMinimumSize = new Vector2(minWidth, 0f) };
        centre.AddChild(panel);
        var box = new VBoxContainer { Name = "Box" };
        panel.AddChild(box);
        return box;
    }

    /// <summary>A full-screen colour rectangle that stops the mouse (so nothing under it is clicked).</summary>
    public static ColorRect FullRect(Control parent, string name, Color color)
    {
        var rect = new ColorRect { Name = name, Color = color, MouseFilter = Control.MouseFilterEnum.Stop };
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(rect);
        return rect;
    }

    public static Button AddButton(Control parent, string name, string text)
    {
        var button = new Button { Name = name, Text = text, FocusMode = Control.FocusModeEnum.All };
        parent.AddChild(button);
        return button;
    }

    public static Label AddLabel(Control parent, string name, string text, string? variation = null)
    {
        var label = new Label { Name = name, Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        if (variation is not null)
        {
            label.ThemeTypeVariation = variation;
        }

        parent.AddChild(label);
        return label;
    }

    /// <summary>Makes <paramref name="control"/> fill its parent (a full-screen menu view).</summary>
    public static void Fill(Control control)
    {
        control.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
    }
}
