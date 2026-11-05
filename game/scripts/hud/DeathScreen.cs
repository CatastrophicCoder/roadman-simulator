using Godot;
using Roadman.Core.Edge;
using Roadman.Ui;

namespace Roadman.Hud;

/// <summary>
/// The death screen (<c>docs/specs/world-edge-game.md</c>, "Death screen"), added by <c>World</c> in every build and
/// hidden until a death: a dark full-screen fade with a centred flat panel in the <see cref="UiTheme"/> style holding
/// three lines in the default font (title, subtitle, losses from <see cref="DeathScreenText.Format"/>). <see cref="ShowLosses"/> fades it in over <see cref="FadeTime"/>,
/// <see cref="FadeOutAndHide"/> fades it out and hides it, <see cref="HideNow"/> hides it at once. It takes no input.
/// </summary>
public partial class DeathScreen : CanvasLayer
{
    private float _opacity;
    private float _fadeDirection;

    /// <summary>Seconds a fade in or out takes.</summary>
    [Export]
    public float FadeTime { get; set; } = 0.5f;

    /// <summary>Colour of the full-screen fade at full opacity.</summary>
    [Export]
    public Color BackgroundColor { get; set; } = UiTheme.Shade;

    [Export]
    public int TitleFontSize { get; set; } = UiTheme.TitleFontSize + 8;

    [Export]
    public int TextFontSize { get; set; } = UiTheme.FontSize + 4;

    /// <summary>Canvas layer, above the HUD and the debug overlays.</summary>
    [Export]
    public int ScreenLayer { get; set; } = 100;

    /// <summary>The root control; its alpha is the fade.</summary>
    public Control Root { get; private set; } = null!;

    public ColorRect Background { get; private set; } = null!;

    public Label Title { get; private set; } = null!;

    public Label Subtitle { get; private set; } = null!;

    public Label Losses { get; private set; } = null!;

    public string TitleText => Title.Text;

    public string SubtitleText => Subtitle.Text;

    public string LossesText => Losses.Text;

    /// <summary>Whether the screen is visible (fading in, shown, or fading out).</summary>
    public bool IsShown => Visible;

    /// <summary>The fade's opacity, 0 (clear) to 1 (fully in).</summary>
    public float Opacity => _opacity;

    /// <summary>Whether it is fading out (after <see cref="FadeOutAndHide"/>, until hidden).</summary>
    public bool IsFadingOut => _fadeDirection < 0f;

    public override void _Ready()
    {
        Layer = ScreenLayer;
        Visible = false;
        Root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Theme };
        Root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(Root);

        Background = new ColorRect { Name = "Background", Color = BackgroundColor, MouseFilter = Control.MouseFilterEnum.Ignore };
        Background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Root.AddChild(Background);

        var centre = new CenterContainer { Name = "Centre", MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Root.AddChild(centre);

        var panel = new PanelContainer
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(640f, 0f),
        };
        centre.AddChild(panel);
        var lines = new VBoxContainer { Name = "Lines", MouseFilter = Control.MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 16);
        panel.AddChild(lines);
        Title = NewLabel("Title", TitleFontSize, UiTheme.Accent);
        Subtitle = NewLabel("Subtitle", TextFontSize, UiTheme.Text);
        Losses = NewLabel("Losses", TextFontSize, UiTheme.TextMuted);
        lines.AddChild(Title);
        lines.AddChild(Subtitle);
        lines.AddChild(Losses);
        SetOpacity(0f);
    }

    public override void _Process(double delta)
    {
        if (_fadeDirection == 0f)
        {
            return;
        }

        var step = FadeTime > 0f ? (float)delta / FadeTime : 1f;
        SetOpacity(Mathf.Clamp(_opacity + (_fadeDirection * step), 0f, 1f));
        if (_fadeDirection > 0f && _opacity >= 1f)
        {
            _fadeDirection = 0f;
        }
        else if (_fadeDirection < 0f && _opacity <= 0f)
        {
            HideNow();
        }
    }

    /// <summary>Sets the three lines and fades the screen in over <see cref="FadeTime"/> (from where it is).</summary>
    public void ShowLosses(DeathScreenLines lines)
    {
        Title.Text = lines.Title;
        Subtitle.Text = lines.Subtitle;
        Losses.Text = lines.Losses;
        Visible = true;
        _fadeDirection = 1f;
        if (FadeTime <= 0f)
        {
            SetOpacity(1f);
            _fadeDirection = 0f;
        }
    }

    /// <summary>Fades the screen out over <see cref="FadeTime"/>, then hides it. Nothing happens when it is hidden.</summary>
    public void FadeOutAndHide()
    {
        if (!Visible)
        {
            return;
        }

        _fadeDirection = -1f;
        if (FadeTime <= 0f)
        {
            HideNow();
        }
    }

    /// <summary>Hides the screen at once, fully faded out.</summary>
    public void HideNow()
    {
        _fadeDirection = 0f;
        SetOpacity(0f);
        Visible = false;
    }

    private void SetOpacity(float opacity)
    {
        _opacity = opacity;
        if (Root is not null)
        {
            Root.Modulate = new Color(1f, 1f, 1f, opacity);
        }
    }

    private static Label NewLabel(string name, int fontSize, Color colour) =>
        new()
        {
            Name = name,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings { FontSize = fontSize, FontColor = colour },
        };
}
