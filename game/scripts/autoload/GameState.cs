using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Roadman.Core;
using Roadman.Core.Items;
using Roadman.Core.Saves;
using Roadman.Core.Settings;

namespace Roadman.Autoload;

/// <summary>
/// Owns the player's <see cref="PlayerState"/> and forwards its events to <see cref="Events"/>, and holds the
/// player's <see cref="GameSettings"/> (<c>user://settings.json</c>). The rules live in <see cref="PlayerState"/>
/// and <see cref="SettingsFile"/>; this node has none of its own. <c>Main</c> calls
/// <see cref="LoadSettings"/> at startup (not in direct-start tool runs, which keep the defaults).
/// </summary>
public partial class GameState : Node
{
    /// <summary>The settings file in the user data folder.</summary>
    public const string DefaultSettingsPath = "user://settings.json";

    private string? _settingsPath;

    public static GameState Instance { get; private set; } = null!;

    public PlayerState Player { get; } = new(ItemCatalog.Default);

    /// <summary>The settings in use: the defaults until <see cref="LoadSettings"/> runs.</summary>
    public GameSettings Settings { get; set; } = GameSettings.Default;

    /// <summary>The warnings of the last <see cref="LoadSettings"/> (each also pushed once with <c>push_warning</c>).</summary>
    public IReadOnlyList<string> SettingsWarnings { get; private set; } = [];

    /// <summary>
    /// The settings file: an absolute path, or a <c>user://</c> path. Null is <see cref="DefaultSettingsPath"/>. A
    /// <c>res://</c>, other-scheme or relative path (<see cref="PlayerDataPath"/>) pushes an error and is set to null,
    /// so the settings never go into the project. Tests point it at a temporary folder and set it back to null.
    /// </summary>
    public string? SettingsPath
    {
        get => _settingsPath;
        set
        {
            if (!string.IsNullOrEmpty(value) && PlayerDataPath.Problem(value) is { } problem)
            {
                GD.PushError($"GameState: not a settings file: {problem}; using {DefaultSettingsPath}");
                _settingsPath = null;
                return;
            }

            _settingsPath = value;
        }
    }

    /// <summary>The absolute settings file path in use.</summary>
    public string ResolvedSettingsPath
    {
        get
        {
            var path = string.IsNullOrEmpty(SettingsPath) ? DefaultSettingsPath : SettingsPath;
            return path.StartsWith(PlayerDataPath.UserScheme, StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
        }
    }

    public override void _EnterTree()
    {
        Instance = this;
        Player.MoneyChanged += OnMoneyChanged;
        Player.GearChanged += OnGearChanged;
        Player.EquipmentChanged += OnEquipmentChanged;
        Player.WantedLevelChanged += OnWantedLevelChanged;
        Player.Died += OnDied;
    }

    public override void _ExitTree()
    {
        Player.MoneyChanged -= OnMoneyChanged;
        Player.GearChanged -= OnGearChanged;
        Player.EquipmentChanged -= OnEquipmentChanged;
        Player.WantedLevelChanged -= OnWantedLevelChanged;
        Player.Died -= OnDied;
    }

    /// <summary>
    /// Reads <see cref="ResolvedSettingsPath"/> into <see cref="Settings"/>: no file gives the defaults (nothing is
    /// written); an unreadable file gives the defaults and one warning; bad values fall back per key
    /// (<see cref="SettingsFile.Load"/>, <see cref="SettingsReader"/>). Each warning is pushed once.
    /// </summary>
    public void LoadSettings()
    {
        var path = ResolvedSettingsPath;
        var result = SettingsFile.Load(path, UsableScreen());
        Settings = result.Settings;
        SettingsWarnings = result.Warnings;
        foreach (var warning in result.Warnings)
        {
            GD.PushWarning($"Settings: {path}: {warning}");
        }
    }

    /// <summary>
    /// Writes <see cref="Settings"/> to <see cref="ResolvedSettingsPath"/> through a temporary file flushed to disk
    /// (<see cref="SettingsFile.Save"/>).
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder isn't writable.</exception>
    public void SaveSettings() => SettingsFile.Save(ResolvedSettingsPath, Settings);

    /// <summary>The usable area of the window's screen (null when the display server reports none, e.g. headless).</summary>
    private static WindowSize? UsableScreen()
    {
        var size = DisplayServer.ScreenGetUsableRect().Size;
        return size.X > 0 && size.Y > 0 ? new WindowSize(size.X, size.Y) : null;
    }

    private static void OnMoneyChanged(int money) =>
        Events.Instance.EmitSignal(Events.SignalName.MoneyChanged, money);

    private static void OnGearChanged() =>
        Events.Instance.EmitSignal(Events.SignalName.GearChanged);

    private static void OnEquipmentChanged(EquipSlot slot) =>
        Events.Instance.EmitSignal(Events.SignalName.EquipmentChanged, (int)slot);

    private static void OnWantedLevelChanged(int level) =>
        Events.Instance.EmitSignal(Events.SignalName.WantedLevelChanged, level);

    private static void OnDied() =>
        Events.Instance.EmitSignal(Events.SignalName.PlayerDied);
}
