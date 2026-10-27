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
