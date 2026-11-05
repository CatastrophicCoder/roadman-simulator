using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Settings;
using Roadman.Debug;
using SettingsWindowMode = Roadman.Core.Settings.WindowMode;

namespace Roadman.Ui;

/// <summary>One row of the controls tab: an action, its label and its keyboard/mouse and gamepad bindings.</summary>
/// <param name="Action">The input action's name (<c>move_forward</c>, ...).</param>
/// <param name="Label">Its plain English label (<see cref="ControlNames.ActionLabel"/>).</param>
/// <param name="Keys">The keyboard and mouse bindings joined with ", "; "—" when there is none.</param>
/// <param name="Gamepad">The gamepad bindings joined with ", "; "—" when there is none.</param>
/// <param name="IsDebug">Whether the row is in the "Debug" group (<see cref="ControlNames.IsDebugAction"/>).</param>
public sealed record ControlRow(string Action, string Label, string Keys, string Gamepad, bool IsDebug);

/// <summary>
/// The settings view (<c>docs/specs/menu-settings-save.md</c>, "Settings view"), built in code on
/// <see cref="UiTheme"/>: the title, four tab buttons (<see cref="TabNames"/>), the selected tab's column of labelled
/// controls, and "Reset to defaults" and "Back". Every change builds a new <see cref="GameSettings"/>, stores it in
/// <see cref="GameState.Settings"/> and raises <see cref="Changed"/> (before, after), which <c>Main</c> applies at once.
/// <para>
/// Confirm or revert: a change to the window mode, window size or render scale
/// (<see cref="DisplayRevert.NeedsConfirm"/>) applies at once and asks <see cref="KeepQuestion"/> in
/// <see cref="Dialog"/> with a countdown (<see cref="DisplayRevert.TimeoutSeconds"/>, counted in real time in
/// <see cref="_Process"/>, also while the tree is paused). Keep keeps it (<see cref="KeepDisplay"/>); Revert, back
/// (the dialog's cancel) and the countdown running out restore those three values (<see cref="RevertDisplay"/>). The
/// display controls are disabled while it is pending. If the question is replaced or closed by something else, the
/// change is reverted.
/// </para>
/// <para>
/// <see cref="Close"/> (Back, or <c>Main</c> routing back to it when no dialog is open) writes the settings
/// (<see cref="GameState.SaveSettings"/>; a failure is in <see cref="LastSaveError"/> and pushed as a warning), hides
/// the view and emits <see cref="SignalName.Closed"/>.
/// </para>
/// </summary>
public partial class SettingsView : Control
{
    /// <summary>The view was closed (after writing the settings).</summary>
    [Signal]
    public delegate void ClosedEventHandler();

    public const int DisplayTab = 0;
    public const int AudioTab = 1;
    public const int ControlsTab = 2;
    public const int GameplayTab = 3;

    public const string TitleText = "Settings";

    public const string KeepQuestion = "Keep these display settings?";

    public const string MinimapNote = "Used by the minimap";

    /// <summary>The text of an empty binding cell.</summary>
    public const string NoBinding = "—";

    /// <summary>The keyboard/mouse cell's entry for mouse look (the <c>camera_*</c> actions).</summary>
    public const string MouseLook = "Mouse";

    public static readonly string[] TabNames = ["Display", "Audio", "Controls", "Gameplay"];

    public static readonly string[] WindowModeNames = ["Windowed", "Borderless fullscreen"];

    public static readonly string[] QualityNames = ["Low", "Medium", "High"];

    public static readonly string[] MinimapCornerNames = ["Top left", "Top right", "Bottom left", "Bottom right"];

    /// <summary>The debug actions in the order the "Debug" group shows them; others follow in name order.</summary>
    public static readonly string[] DebugActionOrder =
    [
        "debug_camera", "debug_orthophoto", "debug_road_graph", "debug_inspect", "debug_forward", "debug_back",
        "debug_left", "debug_right", "debug_up", "debug_down", "debug_fast",
    ];

    private const float LabelColumnWidth = 260f;
    private const float ControlColumnWidth = 360f;
    private const float ValueColumnWidth = 190f;
    private const float ColumnGap = 20f;

    /// <summary>The panel's own horizontal margins (<see cref="UiTheme"/>'s panel style: 28 × 1.6 on each side).</summary>
    private const float PanelMargins = 2f * 28f * 1.6f;
    private const float ContentHeight = 380f;

    private readonly List<Button> _tabButtons = [];
    private readonly List<Control> _tabs = [];
    private readonly List<WindowSize> _sizeItems = [];
    private readonly List<ControlRow> _controlRows = [];
    private bool _updating;
    private bool _revertDialogOpen;
    private GridContainer _controlsGrid = null!;
    private GridContainer _debugGrid = null!;
    private Control _debugGroup = null!;

    /// <summary>Raised after every change with the settings before and after it (also for a revert or a reset).</summary>
    public event Action<GameSettings, GameSettings>? Changed;

    /// <summary>The shared confirmation dialog (keep or revert, reset); set by the owner. Null asks nothing.</summary>
    public ConfirmDialog? Dialog { get; set; }

    /// <summary>The usable screen size for the window size list and the defaults; null when unknown.</summary>
    public Func<WindowSize?> UsableScreen { get; set; } = () => null;

    /// <summary>Where <see cref="Open"/> reads the settings from; default <see cref="GameState.Settings"/>.</summary>
    public Func<GameSettings> Source { get; set; } = () => GameState.Instance?.Settings ?? GameSettings.Default;

    /// <summary>The settings shown (and, through <see cref="Changed"/>, applied).</summary>
    public GameSettings Settings { get; private set; } = GameSettings.Default;

    /// <summary>The confirm-or-revert state of the display changes.</summary>
    public DisplayRevert Revert { get; } = new();

    /// <summary>The selected tab: <see cref="DisplayTab"/> .. <see cref="GameplayTab"/>.</summary>
    public int CurrentTab { get; private set; }

    /// <summary>Why the last <see cref="Close"/> couldn't write the settings; null when it could.</summary>
    public string? LastSaveError { get; private set; }

    public Label Title { get; private set; } = null!;

    /// <summary>The four tab buttons in <see cref="TabNames"/> order.</summary>
    public IReadOnlyList<Button> TabButtons => _tabButtons;

    /// <summary>The four tab contents in <see cref="TabNames"/> order; only <see cref="CurrentTab"/>'s is visible.</summary>
    public IReadOnlyList<Control> Tabs => _tabs;

    public OptionButton WindowModeOption { get; private set; } = null!;

    public OptionButton WindowSizeOption { get; private set; } = null!;

    public HSlider RenderScaleSlider { get; private set; } = null!;

    public Label RenderScaleValue { get; private set; } = null!;

    public OptionButton QualityOption { get; private set; } = null!;

    public CheckButton VSyncCheck { get; private set; } = null!;

    public OptionButton FrameCapOption { get; private set; } = null!;

    public HSlider MasterSlider { get; private set; } = null!;

    public Label MasterValue { get; private set; } = null!;

    public HSlider MusicSlider { get; private set; } = null!;

    public Label MusicValue { get; private set; } = null!;

    public HSlider EffectsSlider { get; private set; } = null!;

    public Label EffectsValue { get; private set; } = null!;

    public CheckButton InvertMouseCheck { get; private set; } = null!;

    public CheckButton InvertStickCheck { get; private set; } = null!;

    public HSlider MouseSensitivitySlider { get; private set; } = null!;

    public Label MouseSensitivityValue { get; private set; } = null!;

    public HSlider StickSensitivitySlider { get; private set; } = null!;

    public Label StickSensitivityValue { get; private set; } = null!;

    public OptionButton MinimapCornerOption { get; private set; } = null!;

    public Button ResetButton { get; private set; } = null!;

    public Button BackButton { get; private set; } = null!;

    /// <summary>The window sizes <see cref="WindowSizeOption"/> lists, in its item order.</summary>
    public IReadOnlyList<WindowSize> WindowSizeItems => _sizeItems;

    /// <summary>The controls tab's rows as shown (gameplay, then debug in debug builds); rebuilt by <see cref="Open"/>.</summary>
    public IReadOnlyList<ControlRow> ControlRows => _controlRows;

    /// <summary>Whether the keep-or-revert question is pending (<see cref="DisplayRevert.IsPending"/>).</summary>
    public bool RevertPending => Revert.IsPending;

    /// <summary>The display controls in tab order.</summary>
    public IReadOnlyList<Control> DisplayControls =>
        [WindowModeOption, WindowSizeOption, RenderScaleSlider, QualityOption, VSyncCheck, FrameCapOption];

    /// <summary>The audio sliders in tab order.</summary>
    public IReadOnlyList<Control> AudioControls => [MasterSlider, MusicSlider, EffectsSlider];

    /// <summary>The gameplay controls in tab order.</summary>
    public IReadOnlyList<Control> GameplayControls =>
        [InvertMouseCheck, InvertStickCheck, MouseSensitivitySlider, StickSensitivitySlider, MinimapCornerOption];

    /// <summary>The countdown line of the keep-or-revert question.</summary>
    public static string CountdownText(double secondsLeft) =>
        string.Create(CultureInfo.InvariantCulture, $"Reverting in {Math.Max(0, (int)Math.Ceiling(secondsLeft))} s");

    /// <summary>The value text of a percentage (render scale, volumes): <c>85 %</c>.</summary>
    public static string PercentText(int percent) => string.Create(CultureInfo.InvariantCulture, $"{percent} %");

    /// <summary>The value text of a sensitivity multiplier: <c>1.00×</c>.</summary>
    public static string MultiplierText(double multiplier) =>
        string.Create(CultureInfo.InvariantCulture, $"{multiplier:0.00}×");

    /// <summary>The frame cap item text: Off, 30, 60, 120.</summary>
    public static string FrameCapText(int cap) =>
        cap == 0 ? "Off" : string.Create(CultureInfo.InvariantCulture, $"{cap}");

    public override void _Ready()
    {
        UiBuild.Fill(this);
        ProcessMode = ProcessModeEnum.Always;
        UiBuild.FullRect(this, "Dim", UiTheme.Dim);
        // As wide as a tab's three columns, so the panel keeps its width on every tab.
        var box = UiBuild.CentredPanel(
            this, "Centre", LabelColumnWidth + ControlColumnWidth + ValueColumnWidth + (2f * ColumnGap) + PanelMargins);
        Title = UiBuild.AddLabel(box, "Title", TitleText, UiTheme.HeadingLabel);

        var tabRow = new HBoxContainer { Name = "TabButtons", Alignment = BoxContainer.AlignmentMode.Center };
        box.AddChild(tabRow);
        var group = new ButtonGroup();
        for (var i = 0; i < TabNames.Length; i++)
        {
            var index = i;
            var button = UiBuild.AddButton(tabRow, $"{TabNames[i]}Tab", TabNames[i]);
            button.ThemeTypeVariation = UiTheme.TabButton;
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.CustomMinimumSize = new Vector2(170f, 0f);
            button.Pressed += () => SelectTab(index);
            _tabButtons.Add(button);
        }

        // A container, so it is as tall as the tallest tab needs (at least ContentHeight, so the panel doesn't jump).
        var content = new MarginContainer { Name = "Content", CustomMinimumSize = new Vector2(0f, ContentHeight) };
        box.AddChild(content);
        _tabs.Add(BuildDisplayTab(content));
        _tabs.Add(BuildAudioTab(content));
        _tabs.Add(BuildControlsTab(content));
        _tabs.Add(BuildGameplayTab(content));

        var bottom = new HBoxContainer { Name = "Bottom", Alignment = BoxContainer.AlignmentMode.Center };
        box.AddChild(bottom);
        ResetButton = UiBuild.AddButton(bottom, "Reset", "Reset to defaults");
        ResetButton.CustomMinimumSize = new Vector2(240f, 0f);
        ResetButton.Pressed += RequestReset;
        BackButton = UiBuild.AddButton(bottom, "Back", "Back");
        BackButton.CustomMinimumSize = new Vector2(180f, 0f);
        BackButton.Pressed += () => Close();

        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!Revert.IsPending)
        {
            return;
        }

        // The question was replaced or closed by something else (a quit question, a view change): revert, the safe side.
        if (Dialog is not null && _revertDialogOpen && !(Dialog.IsOpen && Dialog.MessageText == KeepQuestion))
        {
            _revertDialogOpen = false;
            RevertDisplay();
            return;
        }

        if (Revert.Tick(Math.Max(0d, delta)) == DisplayRevertTick.TimedOut)
        {
            RevertDisplay();
            return;
        }

        if (Dialog is not null && _revertDialogOpen)
        {
            Dialog.Detail.Text = CountdownText(Revert.SecondsLeft);
        }
    }

    /// <summary>
    /// Shows the view with <see cref="Source"/>'s settings on the Display tab, the controls table rebuilt from
    /// <see cref="InputMap"/>, and the focus on the Display tab button.
    /// </summary>
    public void Open()
    {
        Settings = Source();
        LastSaveError = null;
        BuildControlRows();
        Visible = true;
        SelectTab(DisplayTab);
        _tabButtons[DisplayTab].GrabFocus();
    }

    /// <summary>
    /// Selects tab <paramref name="tab"/>: its content shows, its button is pressed, the focus links are rebuilt for
    /// it and the focus stays on the control that had it (else the tab's button).
    /// </summary>
    public void SelectTab(int tab)
    {
        if (tab < 0 || tab >= _tabs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(tab), tab, "not a tab");
        }

        CurrentTab = tab;
        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Visible = i == tab;
            _tabButtons[i].SetPressedNoSignal(i == tab);
        }

        RefreshControls();
        var focused = GetViewport()?.GuiGetFocusOwner();
        if (Visible && (focused is null || !UiFocus.IsFocusable(focused) || !IsAncestorOf(focused)))
        {
            _tabButtons[tab].GrabFocus();
        }
    }

    /// <summary>
    /// Closes the view: reverts a pending display change, writes the settings (<see cref="GameState.SaveSettings"/>),
    /// hides the view and, with <paramref name="notify"/>, emits <see cref="SignalName.Closed"/>.
    /// </summary>
    /// <returns>False when the view wasn't open.</returns>
    public bool Close(bool notify = true)
    {
        if (!Visible)
        {
            return false;
        }

        if (Revert.IsPending)
        {
            RevertDisplay();
        }

        LastSaveError = null;
        try
        {
            GameState.Instance?.SaveSettings();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            LastSaveError = ex.Message;
            GD.PushWarning($"Settings: could not write {GameState.Instance?.ResolvedSettingsPath}: {ex.Message}");
        }

        Visible = false;
        if (notify)
        {
            EmitSignal(SignalName.Closed);
        }

        return true;
    }

    /// <summary>
    /// Changes the display group to <paramref name="next"/> and applies it; a change of window mode, size or render
    /// scale starts the keep-or-revert question. Ignored (the controls are reset to the current values) while a
    /// question is pending.
    /// </summary>
    public void ChangeDisplay(DisplaySettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Revert.IsPending)
        {
            RefreshControls();
            return;
        }

        var before = Settings.Display;
        if (next == before)
        {
            return;
        }

        Apply(Settings with { Display = next });
        if (DisplayRevert.NeedsConfirm(before, next))
        {
            Revert.Begin(before);
            if (Dialog is not null)
            {
                Dialog.Ask(
                    KeepQuestion,
                    CountdownText(Revert.SecondsLeft),
                    [("Keep", () => KeepDisplay()), ("Revert", () => RevertDisplay())],
                    onCancel: () => RevertDisplay());
                _revertDialogOpen = true;
            }
        }

        RefreshControls();
    }

    /// <summary>Changes the audio group and applies it.</summary>
    public void ChangeAudio(AudioSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next != Settings.Audio)
        {
            Apply(Settings with { Audio = next });
        }

        RefreshControls();
    }

    /// <summary>Changes the gameplay group and applies it.</summary>
    public void ChangeGameplay(GameplaySettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next != Settings.Gameplay)
        {
            Apply(Settings with { Gameplay = next });
        }

        RefreshControls();
    }

    /// <summary>Keep: the pending display change stays. False when nothing was pending.</summary>
    public bool KeepDisplay()
    {
        if (!Revert.Keep())
        {
            return false;
        }

        CloseRevertDialog();
        RefreshControls();
        return true;
    }

    /// <summary>
    /// Revert (also back and the countdown): restores the window mode, size and render scale from before the change
    /// (<see cref="DisplayRevert.Revert"/>) and applies them. False when nothing was pending.
    /// </summary>
    public bool RevertDisplay()
    {
        if (Revert.Revert(Settings.Display) is not { } restored)
        {
            return false;
        }

        if (restored != Settings.Display)
        {
            Apply(Settings with { Display = restored });
        }

        RefreshControls();
        CloseRevertDialog();
        return true;
    }

    /// <summary>
    /// "Reset to defaults" for the current tab: asks "Reset &lt;tab&gt; settings to defaults?" (Reset, Cancel); Reset
    /// sets that group to <see cref="GameSettings.Defaults"/> of <see cref="UsableScreen"/> (<see cref="ResetTab"/>).
    /// Nothing on the controls tab.
    /// </summary>
    public void RequestReset()
    {
        if (CurrentTab == ControlsTab)
        {
            return;
        }

        var tab = CurrentTab;
        if (Dialog is null)
        {
            ResetTab(tab);
            return;
        }

        Dialog.Ask(
            $"Reset {TabNames[tab].ToLowerInvariant()} settings to defaults?",
            null,
            [("Reset", () => ResetTab(tab)), ("Cancel", () => { })]);
    }

    /// <summary>
    /// Sets the group of <paramref name="tab"/> to its defaults and applies it, without asking; a display reset that
    /// changes the window mode, size or render scale goes through the keep-or-revert question.
    /// </summary>
    public void ResetTab(int tab)
    {
        var defaults = GameSettings.Defaults(UsableScreen());
        switch (tab)
        {
            case DisplayTab:
                ChangeDisplay(defaults.Display);
                break;
            case AudioTab:
                ChangeAudio(defaults.Audio);
                break;
            case GameplayTab:
                ChangeGameplay(defaults.Gameplay);
                break;
        }
    }

    /// <summary>
    /// The controls table's rows from <see cref="InputMap"/>: the actions of <see cref="ControlNames.GameplayActionOrder"/>
    /// that exist, then every other action from <c>project.godot</c> that isn't a <c>ui_*</c> or debug action (in name
    /// order), then, with <paramref name="includeDebug"/>, the debug actions (<see cref="DebugActionOrder"/>, then the
    /// rest in name order).
    /// </summary>
    public static IReadOnlyList<ControlRow> ReadControlRows(bool includeDebug)
    {
        var projectActions = InputMap.GetActions()
            .Select(name => name.ToString())
            .Where(name => ProjectSettings.HasSetting($"input/{name}") && !name.StartsWith("ui_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var gameplay = ControlNames.GameplayActionOrder.Where(projectActions.Contains).ToList();
        gameplay.AddRange(projectActions
            .Where(name => !ControlNames.IsDebugAction(name) && !gameplay.Contains(name))
            .Order(StringComparer.Ordinal));

        var rows = gameplay.Select(name => RowOf(name, false)).ToList();
        if (includeDebug)
        {
            var debug = DebugActionOrder.Where(projectActions.Contains).ToList();
            debug.AddRange(projectActions
                .Where(name => ControlNames.IsDebugAction(name) && !debug.Contains(name))
                .Order(StringComparer.Ordinal));
            rows.AddRange(debug.Select(name => RowOf(name, true)));
        }

        return rows;
    }

    /// <summary>
    /// The keyboard/mouse text of <paramref name="event"/>, or null when it isn't a key or mouse button: a physical key
    /// shows the label of that key on the current layout (<see cref="DisplayServer.KeyboardGetLabelFromPhysical"/>), or
    /// the physical key's own name when the layout gives none.
    /// </summary>
    public static string? KeyText(InputEvent @event) =>
        @event switch
        {
            InputEventKey { PhysicalKeycode: not Key.None } key => PhysicalKeyText(key.PhysicalKeycode),
            InputEventKey { Keycode: not Key.None } key => OS.GetKeycodeString(key.Keycode),
            InputEventMouseButton mouse => ControlNames.MouseButtonName((int)mouse.ButtonIndex),
            _ => null,
        };

    /// <summary>The gamepad text of <paramref name="event"/>, or null when it isn't a gamepad button or axis.</summary>
    public static string? GamepadText(InputEvent @event) =>
        @event switch
        {
            InputEventJoypadButton button => ControlNames.GamepadButtonName((int)button.ButtonIndex),
            InputEventJoypadMotion motion => ControlNames.GamepadAxisName((int)motion.Axis, motion.AxisValue),
            _ => null,
        };

    /// <summary>
    /// The label of a physical key on the current layout: a printable character as itself, upper case (<c>W</c>,
    /// <c>&lt;</c>, <c>§</c>), any other key by Godot's name (<c>Space</c>, <c>Shift</c>, <c>Escape</c>, <c>F2</c>);
    /// the physical key's own name when the layout gives no label.
    /// </summary>
    public static string PhysicalKeyText(Key physical)
    {
        var label = DisplayServer.KeyboardGetLabelFromPhysical(physical);
        var key = label == Key.None ? physical : label;
        var code = (long)key;
        // Special keys (Escape, Shift, F2, ...) have Key.Special set; a printable character below the surrogates shows as itself.
        if (code > ' ' && code < 0xD800 && !char.IsControl((char)code))
        {
            return ((char)code).ToString().ToUpperInvariant();
        }

        var text = OS.GetKeycodeString(key);
        return string.IsNullOrEmpty(text) ? OS.GetKeycodeString(physical) : text;
    }

    private static ControlRow RowOf(string action, bool debug)
    {
        var events = InputMap.ActionGetEvents(action);
        var keys = events.Select(KeyText).OfType<string>().Distinct().ToList();
        if (action.StartsWith("camera_", StringComparison.Ordinal))
        {
            keys.Add(MouseLook);
        }

        var pad = events.Select(GamepadText).OfType<string>().Distinct().ToList();
        return new ControlRow(
            action,
            ControlNames.ActionLabel(action),
            keys.Count > 0 ? string.Join(", ", keys) : NoBinding,
            pad.Count > 0 ? string.Join(", ", pad) : NoBinding,
            debug);
    }

    private void Apply(GameSettings next)
    {
        var before = Settings;
        Settings = next;
        if (GameState.Instance is { } gameState)
        {
            gameState.Settings = next;
        }

        Changed?.Invoke(before, next);
    }

    private void CloseRevertDialog()
    {
        if (!_revertDialogOpen)
        {
            return;
        }

        _revertDialogOpen = false;
        if (Dialog is not null && Dialog.IsOpen && Dialog.MessageText == KeepQuestion)
        {
            // Back to the control that changed (its onCancel runs RevertDisplay again, which finds nothing pending).
            Dialog.Cancel();
        }
    }

    /// <summary>Shows <see cref="Settings"/> in every control, enables what applies and relinks the focus.</summary>
    private void RefreshControls()
    {
        if (WindowModeOption is null)
        {
            return;
        }

        _updating = true;
        try
        {
            var display = Settings.Display;
            var windowed = display.WindowMode == SettingsWindowMode.Windowed;
            var pending = Revert.IsPending;
            WindowModeOption.Select((int)display.WindowMode);
            FillWindowSizes(display.WindowSize);
            RenderScaleSlider.SetValueNoSignal(display.RenderScale);
            RenderScaleValue.Text = PercentText(display.RenderScale);
            QualityOption.Select((int)display.Quality);
            SetCheck(VSyncCheck, display.VSync);
            FrameCapOption.Select(Math.Max(0, SettingsRules.FrameCaps.ToList().IndexOf(display.FrameCap)));

            UiFocus.SetEnabled(WindowModeOption, !pending);
            UiFocus.SetEnabled(WindowSizeOption, windowed && !pending);
            SetEnabled(RenderScaleSlider, !windowed && !pending);
            RenderScaleValue.ThemeTypeVariation = !windowed ? "" : UiTheme.HintLabel;
            UiFocus.SetEnabled(QualityOption, !pending);
            UiFocus.SetEnabled(VSyncCheck, !pending);
            UiFocus.SetEnabled(FrameCapOption, !pending);

            var audio = Settings.Audio;
            MasterSlider.SetValueNoSignal(audio.MasterVolume);
            MasterValue.Text = PercentText(audio.MasterVolume);
            MusicSlider.SetValueNoSignal(audio.MusicVolume);
            MusicValue.Text = PercentText(audio.MusicVolume);
            EffectsSlider.SetValueNoSignal(audio.EffectsVolume);
            EffectsValue.Text = PercentText(audio.EffectsVolume);

            var gameplay = Settings.Gameplay;
            SetCheck(InvertMouseCheck, gameplay.InvertMouseY);
            SetCheck(InvertStickCheck, gameplay.InvertStickY);
            MouseSensitivitySlider.SetValueNoSignal(gameplay.MouseSensitivity);
            MouseSensitivityValue.Text = MultiplierText(gameplay.MouseSensitivity);
            StickSensitivitySlider.SetValueNoSignal(gameplay.StickSensitivity);
            StickSensitivityValue.Text = MultiplierText(gameplay.StickSensitivity);
            MinimapCornerOption.Select((int)gameplay.MinimapCorner);

            UiFocus.SetEnabled(ResetButton, CurrentTab != ControlsTab);
        }
        finally
        {
            _updating = false;
        }

        LinkFocus();
    }

    /// <summary>Links the tab buttons, the current tab's controls (one per row) and the bottom row as a wrapping grid.</summary>
    private Control? LinkFocus()
    {
        var rows = new List<IEnumerable<Control>> { _tabButtons };
        var tabControls = CurrentTab switch
        {
            DisplayTab => DisplayControls,
            AudioTab => AudioControls,
            GameplayTab => GameplayControls,
            _ => [],
        };
        rows.AddRange(tabControls.Select(control => new[] { control }));
        rows.Add([ResetButton, BackButton]);
        return UiFocus.LinkGrid(rows);
    }

    private void FillWindowSizes(WindowSize current)
    {
        var sizes = WindowSizes.ListFor(UsableScreen(), current);
        if (!sizes.SequenceEqual(_sizeItems))
        {
            _sizeItems.Clear();
            _sizeItems.AddRange(sizes);
            WindowSizeOption.Clear();
            foreach (var size in sizes)
            {
                WindowSizeOption.AddItem(size.ToString());
            }
        }

        WindowSizeOption.Select(_sizeItems.IndexOf(current));
    }

    /// <summary>Sets a toggle without its signal, with "On" or "Off" as its text.</summary>
    private static void SetCheck(CheckButton check, bool on)
    {
        check.SetPressedNoSignal(on);
        check.Text = on ? "On" : "Off";
    }

    private static void SetEnabled(Slider slider, bool enabled)
    {
        slider.Editable = enabled;
        slider.FocusMode = enabled ? FocusModeEnum.All : FocusModeEnum.None;
        slider.Modulate = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.3f);
        if (!enabled && slider.HasFocus())
        {
            slider.ReleaseFocus();
        }
    }

    private Control BuildDisplayTab(Control parent)
    {
        var grid = NewTab(parent, "Display");
        WindowModeOption = AddOption(grid, "WindowMode", "Window mode", WindowModeNames);
        WindowModeOption.ItemSelected += index =>
        {
            if (!_updating)
            {
                ChangeDisplay(Settings.Display with { WindowMode = (SettingsWindowMode)(int)index });
            }
        };

        WindowSizeOption = AddOption(grid, "WindowSize", "Window size", []);
        WindowSizeOption.ItemSelected += index =>
        {
            if (!_updating && index >= 0 && index < _sizeItems.Count)
            {
                ChangeDisplay(Settings.Display with { WindowSize = _sizeItems[(int)index] });
            }
        };

        (RenderScaleSlider, RenderScaleValue) = AddSlider(
            grid, "RenderScale", "Render scale", SettingsRules.MinRenderScale, SettingsRules.MaxRenderScale, SettingsRules.RenderScaleStep);
        RenderScaleSlider.ValueChanged += value =>
        {
            if (!_updating)
            {
                ChangeDisplay(Settings.Display with { RenderScale = (int)Math.Round(value) });
            }
        };

        QualityOption = AddOption(grid, "Quality", "Quality", QualityNames);
        QualityOption.ItemSelected += index =>
        {
            if (!_updating)
            {
                ChangeDisplay(Settings.Display with { Quality = (QualityLevel)(int)index });
            }
        };

        VSyncCheck = AddCheck(grid, "VSync", "VSync");
        VSyncCheck.Toggled += on =>
        {
            if (!_updating)
            {
                ChangeDisplay(Settings.Display with { VSync = on });
            }
        };

        FrameCapOption = AddOption(grid, "FrameCap", "Frame cap", [.. SettingsRules.FrameCaps.Select(FrameCapText)]);
        FrameCapOption.ItemSelected += index =>
        {
            if (!_updating && index >= 0 && index < SettingsRules.FrameCaps.Count)
            {
                ChangeDisplay(Settings.Display with { FrameCap = SettingsRules.FrameCaps[(int)index] });
            }
        };

        return (Control)grid.GetParent();
    }

    private Control BuildAudioTab(Control parent)
    {
        var grid = NewTab(parent, "Audio");
        (MasterSlider, MasterValue) = AddVolume(grid, "Master", "Master volume", (audio, v) => audio with { MasterVolume = v });
        (MusicSlider, MusicValue) = AddVolume(grid, "Music", "Music volume", (audio, v) => audio with { MusicVolume = v });
        (EffectsSlider, EffectsValue) = AddVolume(grid, "Effects", "Effects volume", (audio, v) => audio with { EffectsVolume = v });
        return (Control)grid.GetParent();
    }

    private (HSlider, Label) AddVolume(GridContainer grid, string name, string text, Func<AudioSettings, int, AudioSettings> with)
    {
        var (slider, value) = AddSlider(grid, name, text, SettingsRules.MinVolume, SettingsRules.MaxVolume, SettingsRules.VolumeStep);
        slider.ValueChanged += v =>
        {
            if (!_updating)
            {
                ChangeAudio(with(Settings.Audio, (int)Math.Round(v)));
            }
        };

        return (slider, value);
    }

    private Control BuildControlsTab(Control parent)
    {
        var scroll = new ScrollContainer
        {
            Name = "Controls",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        parent.AddChild(scroll);
        var columns = new HBoxContainer { Name = "Columns", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 40);
        scroll.AddChild(columns);

        _controlsGrid = NewTable("Gameplay");
        columns.AddChild(_controlsGrid);

        // The "Debug" group: its own table beside the gameplay one, headed "Debug".
        _debugGrid = NewTable("Debug");
        columns.AddChild(_debugGrid);
        _debugGroup = _debugGrid;
        return scroll;
    }

    private static GridContainer NewTable(string name)
    {
        var grid = new GridContainer { Name = name, Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 4);
        return grid;
    }

    /// <summary>Rebuilds <see cref="ControlRows"/> and the two tables (the debug group only in debug builds).</summary>
    private void BuildControlRows()
    {
        _controlRows.Clear();
        _controlRows.AddRange(ReadControlRows(DebugBuild.IsDebug));
        FillTable(_controlsGrid, [.. _controlRows.Where(row => !row.IsDebug)], "Action", "Keyboard / mouse");
        var debugRows = _controlRows.Where(row => row.IsDebug).ToList();
        FillTable(_debugGrid, debugRows, "Debug", "Keyboard");
        _debugGroup.Visible = debugRows.Count > 0;
    }

    /// <summary>
    /// Fills a table: a heading row, then one row per action. The gamepad column is left out when no row has a gamepad
    /// binding (the debug group), so the two tables fit side by side.
    /// </summary>
    private static void FillTable(GridContainer grid, IReadOnlyList<ControlRow> rows, string actionHeading, string keysHeading)
    {
        foreach (var child in grid.GetChildren())
        {
            grid.RemoveChild(child);
            child.QueueFree();
        }

        var withGamepad = rows.Count == 0 || rows.Any(row => row.Gamepad != NoBinding);
        grid.Columns = withGamepad ? 3 : 2;
        AddCell(grid, actionHeading, UiTheme.TableHeading);
        AddCell(grid, keysHeading, UiTheme.TableHeading);
        if (withGamepad)
        {
            AddCell(grid, "Gamepad", UiTheme.TableHeading);
        }

        foreach (var row in rows)
        {
            AddCell(grid, row.Label, UiTheme.TableLabel);
            AddCell(grid, row.Keys, UiTheme.TableLabel);
            if (withGamepad)
            {
                AddCell(grid, row.Gamepad, UiTheme.TableLabel);
            }
        }
    }

    private static void AddCell(GridContainer grid, string text, string variation) =>
        grid.AddChild(new Label { Text = text, ThemeTypeVariation = variation });

    private Control BuildGameplayTab(Control parent)
    {
        var grid = NewTab(parent, "Gameplay");
        InvertMouseCheck = AddCheck(grid, "InvertMouseY", "Invert mouse Y");
        InvertMouseCheck.Toggled += on =>
        {
            if (!_updating)
            {
                ChangeGameplay(Settings.Gameplay with { InvertMouseY = on });
            }
        };

        InvertStickCheck = AddCheck(grid, "InvertStickY", "Invert stick Y");
        InvertStickCheck.Toggled += on =>
        {
            if (!_updating)
            {
                ChangeGameplay(Settings.Gameplay with { InvertStickY = on });
            }
        };

        (MouseSensitivitySlider, MouseSensitivityValue) = AddSlider(
            grid, "MouseSensitivity", "Mouse sensitivity", SettingsRules.MinSensitivity, SettingsRules.MaxSensitivity, SettingsRules.SensitivityStep);
        MouseSensitivitySlider.ValueChanged += value =>
        {
            if (!_updating)
            {
                ChangeGameplay(Settings.Gameplay with { MouseSensitivity = SettingsRules.SnapSensitivity(value) });
            }
        };

        (StickSensitivitySlider, StickSensitivityValue) = AddSlider(
            grid, "StickSensitivity", "Stick sensitivity", SettingsRules.MinSensitivity, SettingsRules.MaxSensitivity, SettingsRules.SensitivityStep);
        StickSensitivitySlider.ValueChanged += value =>
        {
            if (!_updating)
            {
                ChangeGameplay(Settings.Gameplay with { StickSensitivity = SettingsRules.SnapSensitivity(value) });
            }
        };

        MinimapCornerOption = AddOption(grid, "MinimapCorner", "Minimap corner", MinimapCornerNames, MinimapNote);
        MinimapCornerOption.ItemSelected += index =>
        {
            if (!_updating)
            {
                ChangeGameplay(Settings.Gameplay with { MinimapCorner = (MinimapCorner)(int)index });
            }
        };

        return (Control)grid.GetParent();
    }

    /// <summary>A tab: a full-size margin with a three-column grid (label, control, value or note); returns the grid.</summary>
    private static GridContainer NewTab(Control parent, string name)
    {
        var margin = new MarginContainer { Name = name };
        margin.AddThemeConstantOverride("margin_top", 8);
        parent.AddChild(margin);
        var grid = new GridContainer { Name = "Rows", Columns = 3, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        grid.AddThemeConstantOverride("h_separation", (int)ColumnGap);
        grid.AddThemeConstantOverride("v_separation", 12);
        margin.AddChild(grid);
        return grid;
    }

    private static void AddRowLabel(GridContainer grid, string name, string text) =>
        grid.AddChild(new Label
        {
            Name = $"{name}Label",
            Text = text,
            CustomMinimumSize = new Vector2(LabelColumnWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        });

    private static Label AddRowValue(GridContainer grid, string name, string text, string? variation = null)
    {
        var label = new Label
        {
            Name = $"{name}Value",
            Text = text,
            CustomMinimumSize = new Vector2(ValueColumnWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (variation is not null)
        {
            label.ThemeTypeVariation = variation;
        }

        grid.AddChild(label);
        return label;
    }

    private static OptionButton AddOption(GridContainer grid, string name, string text, IEnumerable<string> items, string? note = null)
    {
        AddRowLabel(grid, name, text);
        var option = new OptionButton
        {
            Name = name,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(ControlColumnWidth, 0f),
            FitToLongestItem = false,
        };
        foreach (var item in items)
        {
            option.AddItem(item);
        }

        grid.AddChild(option);
        AddRowValue(grid, name, note ?? "", UiTheme.HintLabel);
        return option;
    }

    private static CheckButton AddCheck(GridContainer grid, string name, string text)
    {
        AddRowLabel(grid, name, text);
        var check = new CheckButton
        {
            Name = name,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(ControlColumnWidth, 0f),
        };
        grid.AddChild(check);
        AddRowValue(grid, name, "");
        return check;
    }

    private static (HSlider, Label) AddSlider(GridContainer grid, string name, string text, double min, double max, double step)
    {
        AddRowLabel(grid, name, text);
        var slider = new HSlider
        {
            Name = name,
            MinValue = min,
            MaxValue = max,
            Step = step,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(ControlColumnWidth, 0f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        grid.AddChild(slider);
        var value = AddRowValue(grid, name, "");
        return (slider, value);
    }
}
