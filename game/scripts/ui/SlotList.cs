using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Scenes;

namespace Roadman.Ui;

/// <summary>Whether the slot list loads or saves.</summary>
public enum SlotListMode
{
    Load,
    Save,
}

/// <summary>One row of the <see cref="SlotList"/>.</summary>
public sealed class SlotRow
{
    public required SlotId Slot { get; init; }

    /// <summary>The row's main button (its text is <see cref="SlotRowText.Line"/>).</summary>
    public required Button Button { get; init; }

    /// <summary>"Delete"; visible on every non-empty row.</summary>
    public required Button Delete { get; init; }

    public SlotInfo Info { get; set; } = null!;

    public SlotRowText Text { get; set; } = null!;
}

/// <summary>
/// The slot list (<c>docs/specs/menu-settings-save.md</c>, "Slot list"): six rows (Autosave, Slot 1 to Slot 5), each
/// a button with <see cref="SlotText"/>'s line and a Delete button on non-empty rows, a status line and Back.
/// <para>
/// Load mode: a valid row of an installed map emits <see cref="SignalName.LoadChosen"/> (the caller confirms). Save
/// mode: an empty manual slot is written at once (<see cref="MakeSave"/>), an occupied one after "Overwrite slot
/// &lt;n&gt;?"; then "Saved to slot &lt;n&gt;" or "Could not save: &lt;reason&gt;" shows and the list stays open. Delete asks
/// "Delete &lt;Autosave | slot n&gt;? This can't be undone." first. Back (the button, or <c>Main</c> routing
/// <c>ui_cancel</c>/<c>pause</c> to <see cref="Back"/>) emits <see cref="SignalName.BackRequested"/>.
/// </para>
/// </summary>
public partial class SlotList : Control
{
    [Signal]
    public delegate void LoadChosenEventHandler(int slotNumber);

    [Signal]
    public delegate void BackRequestedEventHandler();

    /// <summary>A slot was written; <paramref name="slotNumber"/> is 1..5.</summary>
    [Signal]
    public delegate void SavedEventHandler(int slotNumber);

    private readonly List<SlotRow> _rows = [];

    public Label Heading { get; private set; } = null!;

    public Label Status { get; private set; } = null!;

    public Button BackButton { get; private set; } = null!;

    public SlotListMode Mode { get; private set; }

    /// <summary>The slots shown; set by the owner before <see cref="Open"/>.</summary>
    public SaveSlots? Slots { get; set; }

    /// <summary>The shared confirmation dialog; set by the owner.</summary>
    public ConfirmDialog? Dialog { get; set; }

    /// <summary>Makes the save to write in save mode; null when the game can't be saved now.</summary>
    public Func<SaveGame?>? MakeSave { get; set; }

    /// <summary>Whether a map id has a pack; default <see cref="IsMapInstalled"/>.</summary>
    public Func<string, bool> MapInstalled { get; set; } = IsMapInstalled;

    /// <summary>The display name of a map id; default <see cref="MapDisplayNameOf"/>.</summary>
    public Func<string, string> MapDisplayName { get; set; } = MapDisplayNameOf;

    /// <summary>The time zone of the saved times; default the local one.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    public IReadOnlyList<SlotRow> Rows => _rows;

    public string StatusText => Status.Text;

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Dim", UiTheme.Dim);
        var box = UiBuild.CentredPanel(this, "Centre", 980f);
        Heading = UiBuild.AddLabel(box, "Heading", "", UiTheme.HeadingLabel);
        var list = new VBoxContainer { Name = "Rows" };
        box.AddChild(list);
        foreach (var slot in SlotId.All)
        {
            var row = new HBoxContainer { Name = slot.IsAutosave ? "Autosave" : $"Slot{slot.Number}" };
            list.AddChild(row);
            var button = UiBuild.AddButton(row, "Choose", slot.DisplayName);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            var delete = UiBuild.AddButton(row, "Delete", "Delete");
            var captured = slot;
            button.Pressed += () => Choose(captured);
            delete.Pressed += () => RequestDelete(captured);
            _rows.Add(new SlotRow { Slot = slot, Button = button, Delete = delete });
        }

        Status = UiBuild.AddLabel(box, "Status", "", UiTheme.HintLabel);
        BackButton = UiBuild.AddButton(box, "Back", "Back");
        BackButton.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        BackButton.CustomMinimumSize = new Vector2(180f, 0f);
        BackButton.Pressed += Back;
        Visible = false;
    }

    /// <summary>Shows the list in <paramref name="mode"/> with fresh rows, no status, the first enabled control focused.</summary>
    public void Open(SlotListMode mode)
    {
        Mode = mode;
        Heading.Text = mode == SlotListMode.Save ? "Save game" : "Load game";
        Status.Text = "";
        Visible = true;
        Refresh();
        LinkFocus()?.GrabFocus();
    }

    /// <summary>Re-reads every slot and updates the rows; the focus stays where it was when it still can.</summary>
    public void Refresh()
    {
        var infos = Slots?.List() ?? [.. SlotId.All.Select(SlotInfo.Empty)];
        foreach (var row in _rows)
        {
            row.Info = infos.First(info => info.Slot == row.Slot);
            row.Text = SlotText.Format(row.Info, Mode == SlotListMode.Save, MapInstalled, MapDisplayName, TimeZone);
            row.Button.Text = row.Text.Line;
            row.Button.TooltipText = row.Text.Line;
            UiFocus.SetEnabled(row.Button, row.Text.Enabled);
            row.Delete.Visible = row.Text.CanDelete;
            UiFocus.SetEnabled(row.Delete, row.Text.CanDelete);
        }

        var focused = GetViewport()?.GuiGetFocusOwner();
        var first = LinkFocus();
        if (Visible && (focused is null || !UiFocus.IsFocusable(focused) || !IsAncestorOf(focused)))
        {
            first?.GrabFocus();
        }
    }

    /// <summary>The row of <paramref name="slot"/>.</summary>
    public SlotRow RowOf(SlotId slot) => _rows.First(row => row.Slot == slot);

    /// <summary>Chooses <paramref name="slot"/> as its button does; does nothing for a disabled row.</summary>
    public void Choose(SlotId slot)
    {
        var row = RowOf(slot);
        if (!row.Text.Enabled)
        {
            return;
        }

        if (Mode == SlotListMode.Load)
        {
            EmitSignal(SignalName.LoadChosen, slot.Number);
            return;
        }

        if (row.Info.Status == SlotStatus.Empty || Dialog is null)
        {
            WriteSave(slot);
            return;
        }

        Dialog.Ask($"Overwrite slot {slot.Number}?", null, [("Overwrite", () => WriteSave(slot)), ("Cancel", () => { })]);
    }

    /// <summary>Asks to delete <paramref name="slot"/>, then deletes it; does nothing for an empty slot.</summary>
    public void RequestDelete(SlotId slot)
    {
        if (RowOf(slot).Info.Status == SlotStatus.Empty)
        {
            return;
        }

        var name = slot.IsAutosave ? slot.DisplayName : $"slot {slot.Number}";
        if (Dialog is null)
        {
            Delete(slot);
            return;
        }

        Dialog.Ask($"Delete {name}? This can't be undone.", null, [("Delete", () => Delete(slot)), ("Cancel", () => { })]);
    }

    /// <summary>Back: hides the list and emits <see cref="SignalName.BackRequested"/>.</summary>
    public void Back()
    {
        Visible = false;
        EmitSignal(SignalName.BackRequested);
    }

    /// <summary>Whether <c>res://maps/&lt;id&gt;/manifest.json</c> (<see cref="MapLoader.PackPathFor"/>) exists.</summary>
    public static bool IsMapInstalled(string mapId) =>
        MapPack.IsValidMapId(mapId) && Godot.FileAccess.FileExists($"{MapLoader.PackPathFor(mapId)}/{MapPack.ManifestFile}");

    /// <summary>The manifest's display name of <paramref name="mapId"/>; the id when the manifest can't be read.</summary>
    public static string MapDisplayNameOf(string mapId)
    {
        if (!IsMapInstalled(mapId))
        {
            return mapId;
        }

        var path = $"{MapLoader.PackPathFor(mapId)}/{MapPack.ManifestFile}";
        try
        {
            return MapManifestReader.Parse(Godot.FileAccess.GetFileAsString(path), path).DisplayName;
        }
        catch (MapPackException)
        {
            return mapId;
        }
    }

    private void WriteSave(SlotId slot)
    {
        var save = MakeSave?.Invoke();
        if (Slots is null || save is null)
        {
            Status.Text = "Could not save: the game can't be saved now";
        }
        else
        {
            try
            {
                Slots.Write(slot, save);
                Status.Text = $"Saved to slot {slot.Number}";
                EmitSignal(SignalName.Saved, slot.Number);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Status.Text = $"Could not save: {ex.Message}";
            }
        }

        Refresh();
        if (UiFocus.IsFocusable(RowOf(slot).Button))
        {
            RowOf(slot).Button.GrabFocus();
        }
    }

    private void Delete(SlotId slot)
    {
        try
        {
            Slots?.Delete(slot);
            Status.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Could not delete: {ex.Message}";
        }

        Refresh();
    }

    /// <summary>Links the rows (choose, delete) and Back as a grid; returns the first focusable control.</summary>
    private Control? LinkFocus() =>
        UiFocus.LinkGrid(_rows.Select(row => new Control[] { row.Button, row.Delete }).Append([BackButton]));
}
