using Godot;

namespace Roadman.Ui;

/// <summary>
/// A short notice at the top centre for <see cref="Seconds"/> (real time; it runs while paused too), for example
/// "The map has changed since this save, so you start somewhere else."
/// </summary>
public partial class NoticeLabel : PanelContainer
{
    private double _left;

    /// <summary>How long a notice shows, s.</summary>
    [Export]
    public double Seconds { get; set; } = 5d;

    public Label Label { get; private set; } = null!;

    public string Text => Label.Text;

    /// <summary>Seconds until it hides.</summary>
    public double TimeLeft => _left;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        GrowHorizontal = GrowDirection.Both;
        OffsetTop = 40f;
        Label = new Label { Name = "Text", HorizontalAlignment = HorizontalAlignment.Center };
        AddChild(Label);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }

        _left -= delta;
        if (_left <= 0d)
        {
            Visible = false;
        }
    }

    /// <summary>Shows <paramref name="text"/> for <see cref="Seconds"/>.</summary>
    public void ShowNotice(string text)
    {
        Label.Text = text;
        _left = Seconds;
        Visible = true;
    }

    public void HideNow()
    {
        Visible = false;
        _left = 0d;
    }
}
