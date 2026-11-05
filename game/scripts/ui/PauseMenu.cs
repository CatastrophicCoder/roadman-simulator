using System.Collections.Generic;
using Godot;

namespace Roadman.Ui;

/// <summary>
/// The pause menu (<c>docs/specs/menu-settings-save.md</c>, "Pause menu"), over the dimmed paused game:
/// <see cref="ResumeButton"/>, <see cref="SaveButton"/> (with <see cref="SaveHint"/> while saving isn't possible),
/// <see cref="LoadButton"/>, <see cref="SettingsButton"/> and <see cref="QuitToMenuButton"/>. <c>Main</c> connects the
/// buttons and calls <see cref="Refresh"/>.
/// </summary>
public partial class PauseMenu : Control
{
    public const string SaveHintText = "You can save when you're not falling off the world";

    public Label Heading { get; private set; } = null!;

    public Button ResumeButton { get; private set; } = null!;

    public Button SaveButton { get; private set; } = null!;

    /// <summary>Shown under Save while it is disabled.</summary>
    public Label SaveHint { get; private set; } = null!;

    public Button LoadButton { get; private set; } = null!;

    public Button SettingsButton { get; private set; } = null!;

    public Button QuitToMenuButton { get; private set; } = null!;

    /// <summary>The five buttons in order.</summary>
    public IReadOnlyList<Button> Buttons => [ResumeButton, SaveButton, LoadButton, SettingsButton, QuitToMenuButton];

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Dim", UiTheme.Dim);
        var box = UiBuild.CentredPanel(this, "Centre", 520f);
        Heading = UiBuild.AddLabel(box, "Heading", "Paused", UiTheme.HeadingLabel);
        ResumeButton = UiBuild.AddButton(box, "Resume", "Resume");
        SaveButton = UiBuild.AddButton(box, "Save", "Save");
        SaveHint = UiBuild.AddLabel(box, "SaveHint", SaveHintText, UiTheme.HintLabel);
        SaveHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        LoadButton = UiBuild.AddButton(box, "Load", "Load");
        SettingsButton = UiBuild.AddButton(box, "Settings", "Settings");
        QuitToMenuButton = UiBuild.AddButton(box, "QuitToMenu", "Quit to menu");
        Refresh(canSave: false, canLoad: false);
    }

    /// <summary>
    /// Enables Save (with its hint hidden) when <paramref name="canSave"/>, Load when <paramref name="canLoad"/>,
    /// relinks the focus and focuses the first enabled button.
    /// </summary>
    public void Refresh(bool canSave, bool canLoad)
    {
        UiFocus.SetEnabled(ResumeButton, true);
        UiFocus.SetEnabled(SaveButton, canSave);
        SaveHint.Visible = !canSave;
        UiFocus.SetEnabled(LoadButton, canLoad);
        UiFocus.SetEnabled(SettingsButton, true);
        UiFocus.SetEnabled(QuitToMenuButton, true);
        FocusFirst();
    }

    /// <summary>Relinks the focus among the enabled buttons and focuses the first (when visible).</summary>
    public void FocusFirst()
    {
        if (IsVisibleInTree())
        {
            UiFocus.LinkColumn(Buttons)?.GrabFocus();
        }
    }
}
