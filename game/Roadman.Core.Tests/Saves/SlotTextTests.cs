using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary><see cref="SlotText"/>: the slot list's row texts (<c>docs/specs/menu-settings-save.md</c>, "Slot list").</summary>
public class SlotTextTests
{
    private static readonly TimeZoneInfo UtcPlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private static readonly DateTimeOffset SavedAt = new(2026, 11, 5, 22, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ValidRow_ShowsMapNameLocalTimeMoneyAndPlayTime()
    {
        var row = Format(SlotInfo.Valid(SlotId.Manual(2), Save()), saveMode: false);

        Assert.Equal("Slot 2", row.Name);
        Assert.Equal("Somero-City · 2026-11-06 00:30 · Money 120 · 1:23", row.Detail);
        Assert.Null(row.Hint);
        Assert.True(row.Enabled);
        Assert.True(row.CanDelete);
    }

    [Fact]
    public void EmptyRow_IsDisabledInLoadModeAndEnabledInSaveMode_WithoutDelete()
    {
        var load = Format(SlotInfo.Empty(SlotId.Manual(1)), saveMode: false);
        var save = Format(SlotInfo.Empty(SlotId.Manual(1)), saveMode: true);

        Assert.Equal("Empty", load.Detail);
        Assert.False(load.Enabled);
        Assert.True(save.Enabled);
        Assert.False(load.CanDelete);
        Assert.False(save.CanDelete);
    }

    [Fact]
    public void DamagedRow_ShowsTheReason_IsDisabledInLoadMode_AndCanBeDeleted()
    {
        var row = Format(SlotInfo.Damaged(SlotId.Manual(3), SlotInfo.NewerVersionReason, "x"), saveMode: false);

        Assert.Equal("Damaged (saved by a newer version)", row.Detail);
        Assert.False(row.Enabled);
        Assert.True(row.CanDelete);
        Assert.True(Format(SlotInfo.Damaged(SlotId.Manual(3), SlotInfo.UnreadableReason, "x"), saveMode: true).Enabled);
    }

    [Fact]
    public void AutosaveRow_InSaveMode_IsDisabledWithTheHint_ButCanBeDeleted()
    {
        var row = Format(SlotInfo.Valid(SlotId.Autosave, Save()), saveMode: true);

        Assert.Equal("Autosave", row.Name);
        Assert.Equal(SlotRowText.AutosaveHint, row.Hint);
        Assert.False(row.Enabled);
        Assert.True(row.CanDelete);
        Assert.True(Format(SlotInfo.Valid(SlotId.Autosave, Save()), saveMode: false).Enabled);
    }

    [Fact]
    public void ValidRowOfAMapWithNoPack_InLoadMode_SaysNotInstalledAndIsDisabled()
    {
        var row = SlotText.Format(
            SlotInfo.Valid(SlotId.Manual(1), Save() with { MapId = "elsewhere" }),
            saveMode: false,
            id => id == "somero",
            id => id,
            UtcPlusTwo);

        Assert.Equal("Map elsewhere not installed", row.Detail);
        Assert.False(row.Enabled);
        Assert.True(row.CanDelete);
    }

    [Fact]
    public void Line_JoinsNameDetailAndHint()
    {
        Assert.Equal("Slot 1   Empty", Format(SlotInfo.Empty(SlotId.Manual(1)), saveMode: false).Line);
        Assert.Equal(
            "Autosave   Empty   (Saved automatically when you quit)",
            Format(SlotInfo.Empty(SlotId.Autosave), saveMode: true).Line);
    }

    private static SlotRowText Format(SlotInfo info, bool saveMode) =>
        SlotText.Format(info, saveMode, _ => true, id => id == "somero" ? "Somero-City" : id, UtcPlusTwo);

    private static SaveGame Save() =>
        new(
            SaveFormat.CurrentVersion,
            "dev",
            SavedAt,
            4980,
            "somero",
            "2026-11-01T10:00:00Z",
            new SavePlayer(new LocalPoint(1, 2, 3), 0.5, 120, ["a", "b"], SaveEquipped.Empty, 2),
            SaveQuests.Empty);
}
