using System.Collections.Generic;
using Godot;

namespace Roadman.Ui;

/// <summary>
/// The main menu (<c>docs/specs/menu-settings-save.md</c>, "Main menu"): the title, the buttons
/// <see cref="NewGameButton"/>, <see cref="ContinueButton"/>, <see cref="LoadButton"/>, <see cref="SettingsButton"/>
/// and <see cref="QuitButton"/> in that order, and the version in the bottom-right corner. <c>Main</c> connects the
/// buttons and calls <see cref="Refresh"/>. Settings stays disabled until the settings view exists
/// (<see cref="SettingsEnabled"/>).
/// </summary>
public partial class MainMenu : Control
{
    public const string TitleText = "Roadman Simulator: Somero-City";

    public Label Title { get; private set; } = null!;

    public Button NewGameButton { get; private set; } = null!;

    public Button ContinueButton { get; private set; } = null!;

    public Button LoadButton { get; private set; } = null!;

    public Button SettingsButton { get; private set; } = null!;

    public Button QuitButton { get; private set; } = null!;

    /// <summary>"Version &lt;application/config/version&gt;", bottom right.</summary>
    public Label VersionLabel { get; private set; } = null!;

    /// <summary>The five buttons in order.</summary>
    public IReadOnlyList<Button> Buttons => [NewGameButton, ContinueButton, LoadButton, SettingsButton, QuitButton];

    /// <summary>Whether Settings can be chosen (false until the settings view exists).</summary>
    public bool SettingsEnabled { get; set; }

    /// <summary>The version shown; set before ready.</summary>
    public string Version { get; set; } = "dev";

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        var box = UiBuild.CentredPanel(this, "Centre", 520f);
        Title = UiBuild.AddLabel(box, "Title", TitleText, UiTheme.TitleLabel);
        box.AddChild(new Control { Name = "Gap", CustomMinimumSize = new Vector2(0f, 16f) });
        NewGameButton = UiBuild.AddButton(box, "NewGame", "New game");
        ContinueButton = UiBuild.AddButton(box, "Continue", "Continue");
        LoadButton = UiBuild.AddButton(box, "Load", "Load");
        SettingsButton = UiBuild.AddButton(box, "Settings", "Settings");
        QuitButton = UiBuild.AddButton(box, "Quit", "Quit");

        VersionLabel = new Label
        {
            Name = "Version",
            Text = $"Version {Version}",
            ThemeTypeVariation = UiTheme.HintLabel,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        VersionLabel.SetAnchorsPreset(LayoutPreset.BottomRight);
        VersionLabel.GrowHorizontal = GrowDirection.Begin;
        VersionLabel.GrowVertical = GrowDirection.Begin;
        VersionLabel.OffsetRight = -20f;
        VersionLabel.OffsetBottom = -14f;
        AddChild(VersionLabel);
        Refresh(canContinue: false, canLoad: false);
    }

    /// <summary>
    /// Enables Continue (a valid save exists) and Load (a valid slot exists), relinks the focus and focuses the first
    /// enabled button.
    /// </summary>
    public void Refresh(bool canContinue, bool canLoad)
    {
        UiFocus.SetEnabled(NewGameButton, true);
        UiFocus.SetEnabled(ContinueButton, canContinue);
        UiFocus.SetEnabled(LoadButton, canLoad);
        UiFocus.SetEnabled(SettingsButton, SettingsEnabled);
        UiFocus.SetEnabled(QuitButton, true);
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
