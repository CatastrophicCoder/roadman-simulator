using Roadman.Core.Maps;
using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary>
/// <see cref="SaveSlots"/> on the in-memory <see cref="MemorySaveStore"/>: <c>docs/specs/menu-settings-save.md</c>,
/// acceptance criterion 3.
/// </summary>
public class SaveSlotsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void List_EmptyStore_GivesSixEmptySlotsAutosaveFirst()
    {
        var slots = new SaveSlots(new MemorySaveStore());

        var list = slots.List();

        Assert.Equal(6, list.Count);
        Assert.True(list[0].Slot.IsAutosave);
        Assert.Equal([1, 2, 3, 4, 5], list.Skip(1).Select(info => info.Slot.Number));
        Assert.All(list, info => Assert.Equal(SlotStatus.Empty, info.Status));
        Assert.All(list, info => Assert.Null(info.Save));
    }

    [Fact]
    public void SlotIds_FileNames_AreAutosaveAndSlotN()
    {
        Assert.Equal("autosave.json", SlotId.Autosave.FileName);
        Assert.Equal(
            ["autosave.json", "slot_1.json", "slot_2.json", "slot_3.json", "slot_4.json", "slot_5.json"],
            SlotId.All.Select(slot => slot.FileName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Manual_NumberOutside1To5_Throws(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SlotId.Manual(number));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Manual_NumberIn1To5_IsAManualSlot(int number)
    {
        var slot = SlotId.Manual(number);

        Assert.False(slot.IsAutosave);
        Assert.Equal(number, slot.Number);
    }

    [Fact]
    public void List_ValidAndDamagedFiles_AreClassifiedPerSlot()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        var save = Save(Noon);
        slots.Write(SlotId.Manual(1), save);
        store.WriteText("slot_2.json", "this is not JSON");
        store.WriteText("slot_3.json", SaveReaderWriterTests.ValidSave.Replace("\"format_version\": 1", "\"format_version\": 2", StringComparison.Ordinal));
        store.WriteText("slot_4.json", SaveReaderWriterTests.ValidSave.Replace("\"money\": 120", "\"money\": -1", StringComparison.Ordinal));

        var list = slots.List();

        Assert.Equal(SlotStatus.Empty, list[0].Status);
        Assert.Equal(SlotStatus.Valid, list[1].Status);
        Assert.Equal(save, list[1].Save);
        Assert.Equal(SlotStatus.Damaged, list[2].Status);
        Assert.Equal(SlotInfo.UnreadableReason, list[2].DamageReason);
        Assert.Equal("unreadable", list[2].DamageReason);
        Assert.Equal(SlotStatus.Damaged, list[3].Status);
        Assert.Equal("saved by a newer version", list[3].DamageReason);
        Assert.Equal(SlotStatus.Damaged, list[4].Status);
        Assert.Equal(SlotInfo.UnreadableReason, list[4].DamageReason);
        Assert.Contains("player.money", list[4].DamageDetail);
        Assert.Equal(SlotStatus.Empty, list[5].Status);
        Assert.All(list.Where(info => info.Status == SlotStatus.Damaged), info => Assert.Null(info.Save));
    }

    [Fact]
    public void List_StoreReadFailure_IsDamagedUnreadable()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Autosave, Save(Noon));
        store.FailRead = true;

        var info = slots.List()[0];

        Assert.Equal(SlotStatus.Damaged, info.Status);
        Assert.Equal(SlotInfo.UnreadableReason, info.DamageReason);
    }

    [Fact]
    public void MostRecent_PicksTheLatestAcrossAutosaveAndManualSlots()
    {
        var slots = new SaveSlots(new MemorySaveStore());
        slots.Write(SlotId.Autosave, Save(Noon));
        slots.Write(SlotId.Manual(2), Save(Noon.AddMinutes(5)));
        slots.Write(SlotId.Manual(4), Save(Noon.AddMinutes(1)));

        Assert.Equal(SlotId.Manual(2), slots.MostRecent()?.Slot);

        slots.Write(SlotId.Autosave, Save(Noon.AddMinutes(10)));

        Assert.Equal(SlotId.Autosave, slots.MostRecent()?.Slot);
    }

    [Fact]
    public void MostRecent_TieWithAutosave_PicksTheAutosave()
    {
        var slots = new SaveSlots(new MemorySaveStore());
        slots.Write(SlotId.Manual(1), Save(Noon));
        slots.Write(SlotId.Autosave, Save(Noon));

        Assert.Equal(SlotId.Autosave, slots.MostRecent()?.Slot);
    }

    [Fact]
    public void MostRecent_TieBetweenManualSlots_PicksTheLowerSlot()
    {
        var slots = new SaveSlots(new MemorySaveStore());
        slots.Write(SlotId.Manual(5), Save(Noon));
        slots.Write(SlotId.Manual(3), Save(Noon));
        slots.Write(SlotId.Autosave, Save(Noon.AddMinutes(-1)));

        Assert.Equal(SlotId.Manual(3), slots.MostRecent()?.Slot);
    }

    [Fact]
    public void MostRecent_IgnoresDamagedSlots()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Manual(2), Save(Noon));
        // A newer save made unreadable, and one from a newer game version, both with later timestamps.
        store.WriteText("slot_1.json", SaveWriter.Write(Save(Noon.AddHours(1))).Replace("\"money\": 10", "\"money\": -10", StringComparison.Ordinal));
        store.WriteText("autosave.json", SaveWriter.Write(Save(Noon.AddHours(2))).Replace("\"format_version\": 1", "\"format_version\": 2", StringComparison.Ordinal));

        Assert.Equal(SlotStatus.Damaged, slots.Get(SlotId.Manual(1)).Status);
        Assert.Equal(SlotStatus.Damaged, slots.Get(SlotId.Autosave).Status);
        Assert.Equal(SlotId.Manual(2), slots.MostRecent()?.Slot);
    }

    [Fact]
    public void MostRecent_NoValidSlot_IsNull()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);

        Assert.Null(slots.MostRecent());

        store.WriteText("autosave.json", "{}");
        store.WriteText("slot_1.json", "garbage");

        Assert.Null(slots.MostRecent());
    }

    [Fact]
    public void Write_Success_LeavesNoTmpFile()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        var save = Save(Noon);

        slots.Write(SlotId.Manual(3), save);

        Assert.Equal(["slot_3.json"], store.List());
        Assert.DoesNotContain(store.List(), name => name.EndsWith(SaveSlots.TempSuffix, StringComparison.Ordinal));
        Assert.Equal(SaveWriter.Write(save), store.Files["slot_3.json"]);
    }

    [Fact]
    public void Write_OverExistingFile_ReplacesIt()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Manual(1), Save(Noon, money: 10));

        slots.Write(SlotId.Manual(1), Save(Noon.AddMinutes(1), money: 99));

        Assert.Equal(99, slots.Get(SlotId.Manual(1)).Save?.Player.Money);
        Assert.Equal(["slot_1.json"], store.List());
    }

    [Fact]
    public void Write_FailedRename_KeepsTheOldFileAndThrows()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Manual(2), Save(Noon, money: 10));
        var oldText = store.Files["slot_2.json"];
        store.FailRename = true;

        Assert.Throws<IOException>(() => slots.Write(SlotId.Manual(2), Save(Noon.AddMinutes(1), money: 99)));

        Assert.Equal(oldText, store.Files["slot_2.json"]);
        Assert.Equal(10, slots.Get(SlotId.Manual(2)).Save?.Player.Money);
        Assert.False(store.Exists("slot_2.json" + SaveSlots.TempSuffix));
    }

    [Fact]
    public void Write_FailedWrite_KeepsTheOldFileAndThrows()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Autosave, Save(Noon, money: 10));
        var oldText = store.Files["autosave.json"];
        store.FailWrite = true;

        Assert.Throws<IOException>(() => slots.Write(SlotId.Autosave, Save(Noon.AddMinutes(1), money: 99)));

        Assert.Equal(oldText, store.Files["autosave.json"]);
        Assert.Equal(["autosave.json"], store.List());
    }

    [Fact]
    public void Write_InvalidSave_ThrowsAndWritesNothing()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);

        Assert.Throws<ArgumentException>(() => slots.Write(SlotId.Manual(1), Save(Noon, money: -1)));

        Assert.Empty(store.List());
    }

    [Fact]
    public void List_LeftoverTmpFile_IsIgnored()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        store.WriteText("slot_1.json.tmp", SaveWriter.Write(Save(Noon)));

        Assert.Equal(SlotStatus.Empty, slots.Get(SlotId.Manual(1)).Status);
    }

    [Fact]
    public void Delete_EmptiesTheSlot()
    {
        var store = new MemorySaveStore();
        var slots = new SaveSlots(store);
        slots.Write(SlotId.Manual(4), Save(Noon));
        slots.Write(SlotId.Autosave, Save(Noon));
        store.WriteText("slot_5.json", "damaged");

        slots.Delete(SlotId.Manual(4));
        slots.Delete(SlotId.Autosave);
        slots.Delete(SlotId.Manual(5));
        slots.Delete(SlotId.Manual(1));

        Assert.All(slots.List(), info => Assert.Equal(SlotStatus.Empty, info.Status));
        Assert.Empty(store.List());
    }

    private static SaveGame Save(DateTimeOffset savedAt, int money = 10) =>
        new(
            SaveFormat.CurrentVersion,
            "dev",
            savedAt,
            60,
            "somero",
            "2026-11-01T10:00:00Z",
            new SavePlayer(new LocalPoint(1, 2, 3), 0.5, money, ["a"], 1),
            SaveQuests.Empty);
}
