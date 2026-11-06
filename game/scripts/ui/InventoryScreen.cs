using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Autoload;
using Roadman.Core;
using Roadman.Core.Items;
using Roadman.Debug;

namespace Roadman.Ui;

/// <summary>One row of the inventory screen's Equipped section.</summary>
public sealed class InventorySlotRow
{
    public required EquipSlot Slot { get; init; }

    public required HBoxContainer Row { get; init; }

    /// <summary>"Weapon" or "Outfit".</summary>
    public required Label SlotLabel { get; init; }

    /// <summary>The equipped item's display name, or "Empty".</summary>
    public required Label ItemLabel { get; init; }

    /// <summary>Unequip; disabled while the slot is empty.</summary>
    public required Button UnequipButton { get; init; }
}

/// <summary>One held item's row of the inventory screen's Items section.</summary>
public sealed class InventoryItemRow
{
    public required ItemType Item { get; init; }

    public required HBoxContainer Row { get; init; }

    /// <summary>The empty square reserved for an icon (<see cref="UiTheme.ItemIconSize"/>).</summary>
    public required Control Icon { get; init; }

    public required Label NameLabel { get; init; }

    /// <summary>"Equipped" after the name; visible only while the item is equipped.</summary>
    public required Label EquippedLabel { get; init; }

    public required Label DescriptionLabel { get; init; }

    /// <summary>Equip, or Unequip while equipped; null for items that can't be equipped (<see cref="ItemCategory.Other"/>).</summary>
    public required Button? EquipButton { get; init; }

    public required Button DiscardButton { get; init; }

    /// <summary>The row's buttons, left to right.</summary>
    public IReadOnlyList<Button> Buttons => EquipButton is { } equip ? [equip, DiscardButton] : [DiscardButton];
}

/// <summary>One catalogue item's row of the inventory screen's Debug section (debug builds only).</summary>
public sealed class InventoryDebugRow
{
    public required ItemType Item { get; init; }

    public required HBoxContainer Row { get; init; }

    public required Label NameLabel { get; init; }

    public required Label CategoryLabel { get; init; }

    /// <summary>Add (<see cref="PlayerState.AddGear"/>); disabled while held.</summary>
    public required Button AddButton { get; init; }

    /// <summary>Remove (<see cref="PlayerState.RemoveGear"/>, no confirmation); disabled while not held.</summary>
    public required Button RemoveButton { get; init; }
}

/// <summary>
/// The inventory screen (<c>docs/specs/inventory.md</c>, "Inventory screen"), built in code on <see cref="UiTheme"/>:
/// the title, the Equipped section (one row per <see cref="EquipSlot"/> with Unequip), the held items under a heading
/// per category (an empty icon square, the name with "Equipped" after it when equipped, the description, Equip or
/// Unequip for weapons and clothing, Discard), "You have no gear." without any, the Debug section in debug builds
/// (<see cref="DebugBuild.IsDebug"/>: Add and Remove for every catalogue item), and Close below the scrolling content.
/// <para>
/// The screen works on <see cref="Player"/> (default <see cref="GameState.Player"/>) and follows its
/// <see cref="PlayerState.GearChanged"/> and <see cref="PlayerState.EquipmentChanged"/> events while it is in the tree,
/// rebuilding the rows at once and keeping the focus on the same control where it still exists (else the first enabled
/// control). Discard asks through <see cref="Dialog"/> first. Focus is explicit and wraps (<see cref="UiFocus"/>); the
/// <see cref="Scroll"/> follows the focus. The owner (<c>Main</c>) opens it (<see cref="Open"/>), routes back to it and
/// closes it on <see cref="SignalName.CloseRequested"/> (the Close button).
/// </para>
/// </summary>
public partial class InventoryScreen : Control
{
    /// <summary>The Close button was pressed.</summary>
    [Signal]
    public delegate void CloseRequestedEventHandler();

    public const string TitleText = "Inventory";

    public const string EmptyText = "You have no gear.";

    public const string EmptySlotText = "Empty";

    public const string EquippedText = "Equipped";

    public const string EquipText = "Equip";

    public const string UnequipText = "Unequip";

    public const string DiscardText = "Discard";

    public const string CancelText = "Cancel";

    public const string AddText = "Add";

    public const string RemoveText = "Remove";

    public const string CloseText = "Close";

    /// <summary>Height of the scrolling content, so the panel fits the project's base 2D size with the title and Close.</summary>
    public const float ContentHeight = 420f;

    public const float PanelWidth = 920f;

    private const float SlotNameWidth = 140f;

    private const float ButtonWidth = 150f;

    private const float DebugButtonWidth = 120f;

    private const int ScrollBarGap = 20;

    private readonly List<InventorySlotRow> _slotRows = [];
    private readonly List<InventoryItemRow> _itemRows = [];
    private readonly List<InventoryDebugRow> _debugRows = [];
    private readonly List<Label> _categoryHeadings = [];
    private PlayerState? _player;
    private bool _subscribed;
    private string? _pendingFocusKey;
    private VBoxContainer _itemsBox = null!;

    /// <summary>The player state shown and changed; null until set or <see cref="_Ready"/> (which takes <see cref="GameState.Player"/>).</summary>
    public PlayerState? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(value, _player))
            {
                return;
            }

            Unsubscribe();
            _player = value;
            if (IsInsideTree())
            {
                Subscribe();
            }

            if (Visible && IsNodeReady())
            {
                Refresh();
            }
        }
    }

    /// <summary>
    /// The confirmation dialog for Discard; set by the owner before the node enters the tree (<c>Main</c>'s shared
    /// one). Without one the screen adds its own.
    /// </summary>
    public ConfirmDialog? Dialog { get; set; }

    /// <summary>Whether the screen was opened from the pause menu (back returns there) rather than from the game.</summary>
    public bool OpenedFromPauseMenu { get; private set; }

    public Label Title { get; private set; } = null!;

    public ScrollContainer Scroll { get; private set; } = null!;

    public IReadOnlyList<InventorySlotRow> SlotRows => _slotRows;

    /// <summary>The held items' rows in display order (category, then <see cref="ItemCatalog.All"/> order).</summary>
    public IReadOnlyList<InventoryItemRow> ItemRows => _itemRows;

    /// <summary>The category headings shown ("Weapons", "Clothing", "Other"; a category without items has none).</summary>
    public IReadOnlyList<Label> CategoryHeadings => _categoryHeadings;

    /// <summary>"You have no gear."; visible only without gear.</summary>
    public Label EmptyLabel { get; private set; } = null!;

    /// <summary>The Debug section; null outside debug builds.</summary>
    public VBoxContainer? DebugSection { get; private set; }

    /// <summary>One row per catalogue item (<see cref="ItemCatalog.All"/> order); empty outside debug builds.</summary>
    public IReadOnlyList<InventoryDebugRow> DebugRows => _debugRows;

    public Button CloseButton { get; private set; } = null!;

    /// <summary>The heading of each category in the Items section.</summary>
    public static string CategoryHeading(ItemCategory category) => category switch
    {
        ItemCategory.Weapon => "Weapons",
        ItemCategory.Clothing => "Clothing",
        ItemCategory.Other => "Other",
        _ => category.ToString(),
    };

    /// <summary>The category name in the Debug section's rows.</summary>
    public static string CategoryName(ItemCategory category) => category switch
    {
        ItemCategory.Weapon => "Weapon",
        ItemCategory.Clothing => "Clothing",
        ItemCategory.Other => "Other",
        _ => category.ToString(),
    };

    /// <summary>The name of an equipment slot.</summary>
    public static string SlotName(EquipSlot slot) => slot switch
    {
        EquipSlot.Weapon => "Weapon",
        EquipSlot.Outfit => "Outfit",
        _ => slot.ToString(),
    };

    /// <summary>The Discard question for an item named <paramref name="displayName"/>.</summary>
    public static string DiscardQuestion(string displayName) => $"Discard {displayName}? It will be gone for good.";

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Dim", UiTheme.Dim);
        var box = UiBuild.CentredPanel(this, "Centre", PanelWidth);
        Title = UiBuild.AddLabel(box, "Title", TitleText, UiTheme.HeadingLabel);

        Scroll = new ScrollContainer
        {
            Name = "Scroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
            CustomMinimumSize = new Vector2(0f, ContentHeight),
        };
        box.AddChild(Scroll);

        // A gap between the rows and the scroll bar.
        var margin = new MarginContainer { Name = "Margin", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", ScrollBarGap);
        Scroll.AddChild(margin);
        var content = new VBoxContainer { Name = "Content", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddChild(content);

        AddSection(content, "EquippedHeading", "Equipped");
        foreach (var slot in ItemCatalog.Slots)
        {
            _slotRows.Add(BuildSlotRow(content, slot));
        }

        AddSection(content, "ItemsHeading", "Items");
        _itemsBox = new VBoxContainer { Name = "Items", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddChild(_itemsBox);
        EmptyLabel = AddLeftLabel(content, "Empty", EmptyText, UiTheme.HintLabel);

        if (DebugBuild.IsDebug)
        {
            DebugSection = new VBoxContainer { Name = "Debug", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            content.AddChild(DebugSection);
            AddSection(DebugSection, "DebugHeading", "Debug");
        }

        CloseButton = UiBuild.AddButton(box, "Close", CloseText);
        CloseButton.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        CloseButton.CustomMinimumSize = new Vector2(180f, 0f);
        CloseButton.Pressed += () => EmitSignal(SignalName.CloseRequested);

        if (Dialog is null)
        {
            Dialog = new ConfirmDialog { Name = "ConfirmDialog" };
            AddChild(Dialog);
        }

        _player ??= GameState.Instance?.Player;
        Subscribe();
        BuildDebugRows();
        Visible = false;
    }

    public override void _EnterTree()
    {
        if (IsNodeReady())
        {
            Subscribe();
        }
    }

    public override void _ExitTree() => Unsubscribe();

    /// <summary>
    /// Shows the screen with fresh rows and the focus on the first enabled control (<see cref="FirstFocusTarget"/>);
    /// <paramref name="fromPauseMenu"/> says where back returns (<see cref="OpenedFromPauseMenu"/>).
    /// </summary>
    public void Open(bool fromPauseMenu)
    {
        OpenedFromPauseMenu = fromPauseMenu;
        Visible = true;
        _pendingFocusKey = null;
        Refresh();
        FocusFirst();
        Scroll.ScrollVertical = 0;
    }

    /// <summary>Hides the screen (and its own dialog, when it has one).</summary>
    public void Close()
    {
        if (Dialog is { } dialog && dialog.GetParent() == this)
        {
            dialog.Close();
        }

        Visible = false;
    }

    /// <summary>
    /// Updates every section from <see cref="Player"/>: the slots, the item rows (rebuilt), the debug rows. The focus
    /// stays on the same control where it still exists and is enabled, else it goes to <see cref="FirstFocusTarget"/>
    /// (not while a dialog is open).
    /// </summary>
    public void Refresh()
    {
        var focusKey = _pendingFocusKey ?? FocusKeyOf(GetViewport()?.GuiGetFocusOwner());
        _pendingFocusKey = null;

        foreach (var row in _slotRows)
        {
            var id = _player?.EquippedIn(row.Slot);
            var name = id is not null ? DisplayNameOf(id) : null;
            row.ItemLabel.Text = name ?? EmptySlotText;
            row.ItemLabel.ThemeTypeVariation = name is null ? UiTheme.HintLabel : "";
            UiFocus.SetEnabled(row.UnequipButton, id is not null);
        }

        RebuildItemRows();

        foreach (var row in _debugRows)
        {
            var held = _player?.Gear.Contains(row.Item.Id) == true;
            UiFocus.SetEnabled(row.AddButton, _player is not null && !held);
            UiFocus.SetEnabled(row.RemoveButton, held);
        }

        LinkFocus();
        if (!IsVisibleInTree() || Dialog?.IsOpen == true)
        {
            return;
        }

        var target = focusKey is not null ? ControlOf(focusKey) : null;
        if (target is null || !UiFocus.IsFocusable(target))
        {
            target = FirstFocusTarget();
        }

        target?.GrabFocus();
    }

    /// <summary>Relinks the focus and focuses the first enabled control (when visible).</summary>
    public void FocusFirst()
    {
        LinkFocus();
        if (IsVisibleInTree())
        {
            FirstFocusTarget()?.GrabFocus();
        }
    }

    /// <summary>
    /// Where the focus starts: the first item row's first button, else the first enabled debug button (debug builds),
    /// else Close.
    /// </summary>
    public Control? FirstFocusTarget()
    {
        foreach (var row in _itemRows)
        {
            if (row.Buttons.FirstOrDefault(UiFocus.IsFocusable) is { } button)
            {
                return button;
            }
        }

        foreach (var row in _debugRows)
        {
            if (new[] { row.AddButton, row.RemoveButton }.FirstOrDefault(UiFocus.IsFocusable) is { } button)
            {
                return button;
            }
        }

        return UiFocus.IsFocusable(CloseButton) ? CloseButton : null;
    }

    /// <summary>The row of held item <paramref name="itemId"/>, or null.</summary>
    public InventoryItemRow? RowOf(string itemId) => _itemRows.FirstOrDefault(row => row.Item.Id == itemId);

    /// <summary>The Equipped row of <paramref name="slot"/>.</summary>
    public InventorySlotRow SlotRowOf(EquipSlot slot) => _slotRows.First(row => row.Slot == slot);

    /// <summary>The debug row of catalogue item <paramref name="itemId"/>, or null (also outside debug builds).</summary>
    public InventoryDebugRow? DebugRowOf(string itemId) => _debugRows.FirstOrDefault(row => row.Item.Id == itemId);

    /// <summary>The Equip/Unequip button of held item <paramref name="itemId"/>; null when not held or not equippable.</summary>
    public Button? EquipButtonOf(string itemId) => RowOf(itemId)?.EquipButton;

    /// <summary>The Discard button of held item <paramref name="itemId"/>; null when not held.</summary>
    public Button? DiscardButtonOf(string itemId) => RowOf(itemId)?.DiscardButton;

    /// <summary>
    /// Discard: asks "Discard &lt;name&gt;? It will be gone for good." (Discard, Cancel); confirming removes the item
    /// (<see cref="PlayerState.RemoveGear"/>, which unequips it first) and moves the focus to the next row's first
    /// button, else the previous row's, else the first enabled control. Does nothing for an item not held.
    /// </summary>
    public void RequestDiscard(string itemId)
    {
        if (_player is null || !_player.Gear.Contains(itemId))
        {
            return;
        }

        var question = DiscardQuestion(DisplayNameOf(itemId));
        if (Dialog is null)
        {
            ConfirmDiscard(itemId);
            return;
        }

        Dialog.Ask(question, null, [(DiscardText, () => ConfirmDiscard(itemId)), (CancelText, () => { })]);
    }

    private void ConfirmDiscard(string itemId)
    {
        if (_player is null)
        {
            return;
        }

        var index = _itemRows.FindIndex(row => row.Item.Id == itemId);
        var focusKey = index < 0 ? null
            : index + 1 < _itemRows.Count ? FirstButtonKey(_itemRows[index + 1])
            : index > 0 ? FirstButtonKey(_itemRows[index - 1])
            : null;

        // RemoveGear raises the change events at once, and the Refresh they run consumes the pending key, so the
        // outcome is decided from the local copy, never from the field after the call.
        _pendingFocusKey = focusKey;
        var removed = _player.RemoveGear(itemId);
        _pendingFocusKey = null;
        if (removed && focusKey is null)
        {
            // No other row: the first enabled control.
            FocusFirst();
        }
    }

    private void ToggleEquip(string itemId)
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            if (_player.IsEquipped(itemId))
            {
                if (_player.Catalog.SlotFor(itemId) is { } slot)
                {
                    _player.Unequip(slot);
                }
            }
            else
            {
                _player.Equip(itemId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            GD.PushWarning($"Inventory: {ex.Message}");
        }
    }

    private void Unequip(EquipSlot slot) => _player?.Unequip(slot);

    private void DebugAdd(string itemId)
    {
        try
        {
            _player?.AddGear(itemId);
        }
        catch (ArgumentException ex)
        {
            GD.PushWarning($"Inventory: {ex.Message}");
        }
    }

    private void DebugRemove(string itemId) => _player?.RemoveGear(itemId);

    private void OnPlayerChanged()
    {
        if (IsNodeReady() && Visible)
        {
            Refresh();
        }
    }

    private void OnEquipmentChanged(EquipSlot slot) => OnPlayerChanged();

    private void Subscribe()
    {
        if (_subscribed || _player is null)
        {
            return;
        }

        _player.GearChanged += OnPlayerChanged;
        _player.EquipmentChanged += OnEquipmentChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _player is null)
        {
            _subscribed = false;
            return;
        }

        _player.GearChanged -= OnPlayerChanged;
        _player.EquipmentChanged -= OnEquipmentChanged;
        _subscribed = false;
    }

    private string DisplayNameOf(string itemId) =>
        _player is not null && _player.Catalog.TryGet(itemId, out var item) ? item.DisplayName : itemId;

    private InventorySlotRow BuildSlotRow(Control parent, EquipSlot slot)
    {
        var row = new HBoxContainer { Name = $"Slot{slot}" };
        parent.AddChild(row);
        var slotLabel = AddLeftLabel(row, "Slot", SlotName(slot));
        slotLabel.CustomMinimumSize = new Vector2(SlotNameWidth, 0f);
        var itemLabel = AddLeftLabel(row, "Item", EmptySlotText, UiTheme.HintLabel);
        itemLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var unequip = UiBuild.AddButton(row, "Unequip", UnequipText);
        unequip.CustomMinimumSize = new Vector2(ButtonWidth, 0f);
        unequip.Pressed += () => Unequip(slot);
        return new InventorySlotRow { Slot = slot, Row = row, SlotLabel = slotLabel, ItemLabel = itemLabel, UnequipButton = unequip };
    }

    private void RebuildItemRows()
    {
        foreach (var child in _itemsBox.GetChildren())
        {
            _itemsBox.RemoveChild(child);
            child.QueueFree();
        }

        _itemRows.Clear();
        _categoryHeadings.Clear();
        if (_player is null)
        {
            EmptyLabel.Visible = true;
            return;
        }

        var held = _player.Catalog.All.Where(item => _player.Gear.Contains(item.Id)).ToList();
        EmptyLabel.Visible = held.Count == 0;
        foreach (var group in held.GroupBy(item => item.Category))
        {
            var heading = AddLeftLabel(_itemsBox, $"{group.Key}Heading", CategoryHeading(group.Key), UiTheme.TagLabel);
            _categoryHeadings.Add(heading);
            foreach (var item in group)
            {
                _itemRows.Add(BuildItemRow(item));
            }
        }
    }

    private InventoryItemRow BuildItemRow(ItemType item)
    {
        var equipped = _player!.IsEquipped(item.Id);
        var row = new HBoxContainer { Name = item.Id.ValidateNodeName() };
        _itemsBox.AddChild(row);
        var icon = new Control
        {
            Name = "Icon",
            CustomMinimumSize = new Vector2(UiTheme.ItemIconSize, UiTheme.ItemIconSize),
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(icon);

        var text = new VBoxContainer { Name = "Text", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        row.AddChild(text);
        var nameLine = new HBoxContainer { Name = "NameLine" };
        text.AddChild(nameLine);
        var name = AddLeftLabel(nameLine, "Name", item.DisplayName);
        var tag = AddLeftLabel(nameLine, "Equipped", EquippedText, UiTheme.TagLabel);
        tag.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        tag.Visible = equipped;
        var description = AddLeftLabel(text, "Description", item.Description, UiTheme.HintLabel);
        description.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        description.TooltipText = item.Description;

        Button? equip = null;
        if (_player.Catalog.SlotFor(item.Id) is not null)
        {
            equip = UiBuild.AddButton(row, "Equip", equipped ? UnequipText : EquipText);
            equip.CustomMinimumSize = new Vector2(ButtonWidth, 0f);
            equip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var id = item.Id;
            equip.Pressed += () => ToggleEquip(id);
        }
        else
        {
            // Keeps Discard in the same column on every row.
            row.AddChild(new Control
            {
                Name = "NoEquip",
                CustomMinimumSize = new Vector2(ButtonWidth, 0f),
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }

        var discard = UiBuild.AddButton(row, "Discard", DiscardText);
        discard.CustomMinimumSize = new Vector2(ButtonWidth, 0f);
        discard.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var discardId = item.Id;
        discard.Pressed += () => RequestDiscard(discardId);

        return new InventoryItemRow
        {
            Item = item,
            Row = row,
            Icon = icon,
            NameLabel = name,
            EquippedLabel = tag,
            DescriptionLabel = description,
            EquipButton = equip,
            DiscardButton = discard,
        };
    }

    private void BuildDebugRows()
    {
        if (DebugSection is null)
        {
            return;
        }

        var catalog = _player?.Catalog ?? ItemCatalog.Default;
        foreach (var item in catalog.All)
        {
            var row = new HBoxContainer { Name = item.Id.ValidateNodeName() };
            DebugSection.AddChild(row);
            var name = AddLeftLabel(row, "Name", item.DisplayName);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var category = AddLeftLabel(row, "Category", CategoryName(item.Category), UiTheme.HintLabel);
            category.CustomMinimumSize = new Vector2(SlotNameWidth, 0f);
            var add = UiBuild.AddButton(row, "Add", AddText);
            add.CustomMinimumSize = new Vector2(DebugButtonWidth, 0f);
            var remove = UiBuild.AddButton(row, "Remove", RemoveText);
            remove.CustomMinimumSize = new Vector2(DebugButtonWidth, 0f);
            var id = item.Id;
            add.Pressed += () => DebugAdd(id);
            remove.Pressed += () => DebugRemove(id);
            _debugRows.Add(new InventoryDebugRow
            {
                Item = item,
                Row = row,
                NameLabel = name,
                CategoryLabel = category,
                AddButton = add,
                RemoveButton = remove,
            });
        }
    }

    /// <summary>Links every control as a grid: slot rows, item rows, debug rows, Close; wrapping.</summary>
    private void LinkFocus()
    {
        var rows = new List<IEnumerable<Control>>();
        rows.AddRange(_slotRows.Select(row => new Control[] { row.UnequipButton }));
        rows.AddRange(_itemRows.Select(row => row.Buttons.Cast<Control>()));
        rows.AddRange(_debugRows.Select(row => new Control[] { row.AddButton, row.RemoveButton }));
        rows.Add([CloseButton]);
        UiFocus.LinkGrid(rows);
    }

    private static string FirstButtonKey(InventoryItemRow row) =>
        row.EquipButton is not null ? $"item:{row.Item.Id}:equip" : $"item:{row.Item.Id}:discard";

    /// <summary>A key naming <paramref name="control"/> that survives a rebuild of the rows; null for anything else.</summary>
    private string? FocusKeyOf(Control? control)
    {
        if (control is null || !IsInstanceValid(control))
        {
            return null;
        }

        if (control == CloseButton)
        {
            return "close";
        }

        foreach (var row in _slotRows)
        {
            if (control == row.UnequipButton)
            {
                return $"slot:{row.Slot}:unequip";
            }
        }

        foreach (var row in _itemRows)
        {
            if (control == row.EquipButton)
            {
                return $"item:{row.Item.Id}:equip";
            }

            if (control == row.DiscardButton)
            {
                return $"item:{row.Item.Id}:discard";
            }
        }

        foreach (var row in _debugRows)
        {
            if (control == row.AddButton)
            {
                return $"debug:{row.Item.Id}:add";
            }

            if (control == row.RemoveButton)
            {
                return $"debug:{row.Item.Id}:remove";
            }
        }

        return null;
    }

    private Control? ControlOf(string key)
    {
        if (key == "close")
        {
            return CloseButton;
        }

        var parts = key.Split(':');
        if (parts.Length != 3)
        {
            return null;
        }

        return parts[0] switch
        {
            "slot" => _slotRows.FirstOrDefault(row => row.Slot.ToString() == parts[1])?.UnequipButton,
            "item" => parts[2] == "equip" ? RowOf(parts[1])?.EquipButton : RowOf(parts[1])?.DiscardButton,
            "debug" => parts[2] == "add" ? DebugRowOf(parts[1])?.AddButton : DebugRowOf(parts[1])?.RemoveButton,
            _ => null,
        };
    }

    private static void AddSection(Control parent, string name, string text) =>
        AddLeftLabel(parent, name, text, UiTheme.SectionLabel);

    private static Label AddLeftLabel(Control parent, string name, string text, string? variation = null)
    {
        var label = UiBuild.AddLabel(parent, name, text, variation);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        return label;
    }
}
