using Godot;

namespace Roadman.Ui;

/// <summary>
/// The loading screen (<c>docs/specs/menu-settings-save.md</c>, "Loading screen"): "Loading &lt;name&gt;…" on the flat
/// background while <c>World</c> loads; after a failure the error text and <see cref="BackButton"/> ("Back to main
/// menu").
/// </summary>
public partial class LoadingScreen : Control
{
    public Label Status { get; private set; } = null!;

    /// <summary>The error text; hidden while loading.</summary>
    public Label Error { get; private set; } = null!;

    /// <summary>"Back to main menu"; shown only after a failure.</summary>
    public Button BackButton { get; private set; } = null!;

    /// <summary>Whether it shows a failure.</summary>
    public bool Failed => Error.Visible;

    public string StatusText => Status.Text;

    public string ErrorText => Error.Text;

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Background", UiTheme.Background);
        var centre = new CenterContainer { Name = "Centre", MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var box = new VBoxContainer { Name = "Box", CustomMinimumSize = new Vector2(760f, 0f) };
        centre.AddChild(box);
        Status = UiBuild.AddLabel(box, "Status", "", UiTheme.HeadingLabel);
        Error = UiBuild.AddLabel(box, "Error", "");
        Error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Error.CustomMinimumSize = new Vector2(760f, 0f);
        BackButton = UiBuild.AddButton(box, "Back", "Back to main menu");
        BackButton.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        ShowLoading("");
    }

    /// <summary>"Loading <paramref name="displayName"/>…", no error, no button.</summary>
    public void ShowLoading(string displayName)
    {
        Status.Text = $"Loading {displayName}…";
        Error.Visible = false;
        Error.Text = "";
        BackButton.Visible = false;
    }

    /// <summary>The failure: <paramref name="message"/> and the focused "Back to main menu" button.</summary>
    public void ShowError(string message)
    {
        Status.Text = "Could not start the game";
        Error.Text = message;
        Error.Visible = true;
        BackButton.Visible = true;
        if (IsVisibleInTree())
        {
            UiFocus.LinkColumn([BackButton])?.GrabFocus();
        }
    }
}
