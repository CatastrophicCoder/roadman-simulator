using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Roadman.Ui;

/// <summary>
/// A modal question over the current view: a message, an optional second line and up to <see cref="MaxButtons"/>
/// buttons in a row, on a dimmed full-screen layer that stops the mouse. Focus starts on the first button and stays in
/// the dialog. Back (<c>ui_cancel</c> or <c>pause</c>, routed by <c>Main</c> to <see cref="Cancel"/>) is cancel.
/// A pressed button closes the dialog, then runs its action (which may open the dialog again); closing gives the focus
/// back to the control that had it, when that is still focusable.
/// </summary>
public partial class ConfirmDialog : Control
{
    public const int MaxButtons = 3;

    private readonly List<Button> _buttons = [];
    private readonly Action?[] _actions = new Action?[MaxButtons];
    private Action? _onCancel;
    private Control? _previousFocus;

    public Label Message { get; private set; } = null!;

    public Label Detail { get; private set; } = null!;

    /// <summary>The visible buttons, in order.</summary>
    public IReadOnlyList<Button> Buttons => [.. _buttons.Where(b => b.Visible)];

    public bool IsOpen => Visible;

    public string MessageText => Message.Text;

    /// <summary>The second line; empty when there is none.</summary>
    public string DetailText => Detail.Visible ? Detail.Text : "";

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Dim", UiTheme.Dim);
        var box = UiBuild.CentredPanel(this, "Centre", 560f);
        Message = UiBuild.AddLabel(box, "Message", "", UiTheme.HeadingLabel);
        Message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Message.CustomMinimumSize = new Vector2(520f, 0f);
        Detail = UiBuild.AddLabel(box, "Detail", "");
        Detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Detail.CustomMinimumSize = new Vector2(520f, 0f);
        var row = new HBoxContainer { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.Center };
        box.AddChild(row);
        for (var i = 0; i < MaxButtons; i++)
        {
            var index = i;
            var button = UiBuild.AddButton(row, $"Button{i}", "");
            button.CustomMinimumSize = new Vector2(150f, 0f);
            button.Pressed += () => Press(index);
            _buttons.Add(button);
        }

        Visible = false;
    }

    /// <summary>
    /// Shows the dialog with <paramref name="message"/>, <paramref name="detail"/> (null: no second line) and
    /// <paramref name="buttons"/> (1 to <see cref="MaxButtons"/>); <paramref name="onCancel"/> runs on back.
    /// </summary>
    public void Ask(string message, string? detail, IReadOnlyList<(string Text, Action Pressed)> buttons, Action? onCancel = null)
    {
        if (buttons.Count is < 1 or > MaxButtons)
        {
            throw new ArgumentException($"a dialog has 1 to {MaxButtons} buttons", nameof(buttons));
        }

        // Kept across a dialog that a button of this one opens, so the focus goes back to where it was before both.
        if (!Visible && _previousFocus is null)
        {
            _previousFocus = GetViewport()?.GuiGetFocusOwner();
        }

        Message.Text = message;
        Detail.Text = detail ?? "";
        Detail.Visible = detail is not null;
        for (var i = 0; i < MaxButtons; i++)
        {
            var used = i < buttons.Count;
            _buttons[i].Visible = used;
            _buttons[i].Text = used ? buttons[i].Text : "";
            _actions[i] = used ? buttons[i].Pressed : null;
        }

        _onCancel = onCancel;
        Visible = true;
        UiFocus.LinkRow(_buttons)?.GrabFocus();
    }

    /// <summary>The visible button with <paramref name="text"/>, or null.</summary>
    public Button? ButtonByText(string text) => Buttons.FirstOrDefault(b => b.Text == text);

    /// <summary>Back: closes the dialog and runs its cancel action.</summary>
    public void Cancel()
    {
        if (!Visible)
        {
            return;
        }

        var onCancel = _onCancel;
        HideDialog();
        onCancel?.Invoke();
        RestoreFocus();
    }

    /// <summary>Closes the dialog without running anything and without moving the focus.</summary>
    public void Close()
    {
        HideDialog();
        _previousFocus = null;
    }

    private void HideDialog()
    {
        Visible = false;
        _onCancel = null;
        Array.Clear(_actions);
    }

    private void Press(int index)
    {
        var action = _actions[index];
        HideDialog();
        action?.Invoke();
        RestoreFocus();
    }

    private void RestoreFocus()
    {
        if (!Visible && _previousFocus is { } previous && UiFocus.IsFocusable(previous))
        {
            previous.GrabFocus();
        }

        if (!Visible)
        {
            _previousFocus = null;
        }
    }
}
