using Godot;
using Roadman.Core;

namespace Roadman.Autoload;

/// <summary>
/// Owns the player's <see cref="PlayerState"/> and forwards its events to <see cref="Events"/>.
/// The rules live in <see cref="PlayerState"/>; this node has none of its own.
/// </summary>
public partial class GameState : Node
{
    public static GameState Instance { get; private set; } = null!;

    public PlayerState Player { get; } = new();

    public override void _EnterTree()
    {
        Instance = this;
        Player.MoneyChanged += OnMoneyChanged;
        Player.GearChanged += OnGearChanged;
        Player.WantedLevelChanged += OnWantedLevelChanged;
        Player.Died += OnDied;
    }

    public override void _ExitTree()
    {
        Player.MoneyChanged -= OnMoneyChanged;
        Player.GearChanged -= OnGearChanged;
        Player.WantedLevelChanged -= OnWantedLevelChanged;
        Player.Died -= OnDied;
    }

    private static void OnMoneyChanged(int money) =>
        Events.Instance.EmitSignal(Events.SignalName.MoneyChanged, money);

    private static void OnGearChanged() =>
        Events.Instance.EmitSignal(Events.SignalName.GearChanged);

    private static void OnWantedLevelChanged(int level) =>
        Events.Instance.EmitSignal(Events.SignalName.WantedLevelChanged, level);

    private static void OnDied() =>
        Events.Instance.EmitSignal(Events.SignalName.PlayerDied);
}
