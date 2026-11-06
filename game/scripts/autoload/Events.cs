using Godot;

namespace Roadman.Autoload;

/// <summary>
/// Global signal bus. Systems emit and listen here instead of holding references to each other.
/// </summary>
public partial class Events : Node
{
    [Signal]
    public delegate void MoneyChangedEventHandler(int money);

    [Signal]
    public delegate void GearChangedEventHandler();

    /// <summary>
    /// The content of an equipment slot changed; <paramref name="slot"/> is the <c>Roadman.Core.Items.EquipSlot</c>
    /// value as an int (the quest system's "equip gear" step listens to it).
    /// </summary>
    [Signal]
    public delegate void EquipmentChangedEventHandler(int slot);

    [Signal]
    public delegate void WantedLevelChangedEventHandler(int level);

    [Signal]
    public delegate void PlayerDiedEventHandler();

    public static Events Instance { get; private set; } = null!;

    public override void _EnterTree()
    {
        Instance = this;
    }
}
