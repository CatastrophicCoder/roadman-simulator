using System.Globalization;

namespace Roadman.Core.Saves;

/// <summary>
/// A save slot: the autosave or manual slot 1..<see cref="SaveSlots.ManualSlotCount"/>. <c>default(SlotId)</c> is
/// the autosave.
/// </summary>
public readonly record struct SlotId
{
    private SlotId(int number) => Number = number;

    /// <summary>The autosave slot (file <c>autosave.json</c>), written when quitting.</summary>
    public static SlotId Autosave => default;

    /// <summary>Every slot in list order: the autosave, then manual slots 1..<see cref="SaveSlots.ManualSlotCount"/>.</summary>
    public static IReadOnlyList<SlotId> All { get; } =
        [Autosave, .. Enumerable.Range(1, SaveSlots.ManualSlotCount).Select(Manual)];

    /// <summary>0 for the autosave, else the manual slot number 1..<see cref="SaveSlots.ManualSlotCount"/>.</summary>
    public int Number { get; }

    public bool IsAutosave => Number == 0;

    /// <summary>The file name in the saves folder: <c>autosave.json</c> or <c>slot_&lt;n&gt;.json</c>.</summary>
    public string FileName => IsAutosave ? "autosave.json" : string.Create(CultureInfo.InvariantCulture, $"slot_{Number}.json");

    /// <summary>The slot list's name: <c>Autosave</c> or <c>Slot &lt;n&gt;</c>.</summary>
    public string DisplayName => IsAutosave ? "Autosave" : string.Create(CultureInfo.InvariantCulture, $"Slot {Number}");

    /// <summary>Manual slot <paramref name="number"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is outside 1..<see cref="SaveSlots.ManualSlotCount"/>.</exception>
    public static SlotId Manual(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, SaveSlots.ManualSlotCount);
        return new SlotId(number);
    }

    public override string ToString() => DisplayName;
}

/// <summary>What a slot holds.</summary>
public enum SlotStatus
{
    /// <summary>No file.</summary>
    Empty,

    /// <summary>A file that reads as a valid save (<see cref="SlotInfo.Save"/>).</summary>
    Valid,

    /// <summary>A file that can't be used (<see cref="SlotInfo.DamageReason"/>).</summary>
    Damaged,
}

/// <summary>One slot in <see cref="SaveSlots.List"/>.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Status">What it holds.</param>
/// <param name="Save">The save when <see cref="SlotStatus.Valid"/>, else null.</param>
/// <param name="DamageReason">
/// When <see cref="SlotStatus.Damaged"/>: <see cref="UnreadableReason"/> or <see cref="NewerVersionReason"/>, the
/// slot list's short text; else null.
/// </param>
/// <param name="DamageDetail">When damaged, the full error message (for the log); else null.</param>
public sealed record SlotInfo(SlotId Slot, SlotStatus Status, SaveGame? Save, string? DamageReason, string? DamageDetail)
{
    /// <summary>The file can't be read, isn't JSON or isn't a valid save.</summary>
    public const string UnreadableReason = "unreadable";

    /// <summary>The file's <c>format_version</c> is newer than this game reads.</summary>
    public const string NewerVersionReason = "saved by a newer version";

    public static SlotInfo Empty(SlotId slot) => new(slot, SlotStatus.Empty, null, null, null);

    public static SlotInfo Valid(SlotId slot, SaveGame save) =>
        new(slot, SlotStatus.Valid, save ?? throw new ArgumentNullException(nameof(save)), null, null);

    public static SlotInfo Damaged(SlotId slot, string reason, string detail) =>
        new(slot, SlotStatus.Damaged, null, reason, detail);
}

/// <summary>
/// The save slots on an <see cref="ISaveStore"/> (the saves folder in the game, <see cref="MemorySaveStore"/> in
/// tests): listing, the most recent save, atomic writes and deletes. No file I/O of its own.
/// </summary>
public sealed class SaveSlots
{
    /// <summary>The number of manual slots.</summary>
    public const int ManualSlotCount = 5;

    /// <summary>The suffix of the temporary file <see cref="Write"/> renames over the slot's file.</summary>
    public const string TempSuffix = ".tmp";

    private readonly ISaveStore _store;
    private readonly SaveMigrations _migrations;

    /// <param name="store">Where the files are.</param>
    /// <param name="migrations">Migrations for older files; <see cref="SaveMigrations.Default"/> when null.</param>
    public SaveSlots(ISaveStore store, SaveMigrations? migrations = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _migrations = migrations ?? SaveMigrations.Default;
    }

    /// <summary>
    /// Every slot, autosave first, then 1..<see cref="ManualSlotCount"/>: <see cref="SlotStatus.Empty"/> without a
    /// file, <see cref="SlotStatus.Valid"/> when it reads, else <see cref="SlotStatus.Damaged"/> with
    /// <see cref="SlotInfo.NewerVersionReason"/> for a newer format version and <see cref="SlotInfo.UnreadableReason"/>
    /// for anything else (a read error from the store, not JSON, invalid).
    /// </summary>
    public IReadOnlyList<SlotInfo> List() => [.. SlotId.All.Select(Get)];

    /// <summary>One slot, as in <see cref="List"/>.</summary>
    public SlotInfo Get(SlotId slot)
    {
        var file = slot.FileName;
        string text;
        try
        {
            if (!_store.Exists(file))
            {
                return SlotInfo.Empty(slot);
            }

            text = _store.ReadText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SlotInfo.Damaged(slot, SlotInfo.UnreadableReason, ex.Message);
        }

        try
        {
            return SlotInfo.Valid(slot, SaveReader.Read(text, _migrations));
        }
        catch (SaveFormatException ex)
        {
            return SlotInfo.Damaged(slot, ex.NewerVersion ? SlotInfo.NewerVersionReason : SlotInfo.UnreadableReason, ex.Message);
        }
    }

    /// <summary>
    /// The valid slot with the latest <see cref="SaveGame.SavedAtUtc"/>, autosave included; on a tie the autosave,
    /// then the lower slot number. Null when no slot is valid.
    /// </summary>
    public SlotInfo? MostRecent()
    {
        SlotInfo? best = null;
        foreach (var info in List())
        {
            if (info.Save is { } save && (best is null || save.SavedAtUtc > best.Save!.SavedAtUtc))
            {
                best = info;
            }
        }

        return best;
    }

    /// <summary>
    /// Writes <paramref name="save"/> to <paramref name="slot"/>: <see cref="SaveWriter.Write"/>, then the text to
    /// <c>&lt;file&gt;.tmp</c>, then renames that over <c>&lt;file&gt;</c>. When writing or renaming fails, the
    /// temporary file is deleted if possible and the exception is rethrown; the old file stays as it was.
    /// </summary>
    /// <exception cref="ArgumentException">The save is invalid (from <see cref="SaveWriter.Write"/>); nothing is written.</exception>
    public void Write(SlotId slot, SaveGame save)
    {
        var text = SaveWriter.Write(save);
        var file = slot.FileName;
        var temp = file + TempSuffix;
        try
        {
            _store.WriteText(temp, text);
            _store.Rename(temp, file);
        }
        catch
        {
            try
            {
                if (_store.Exists(temp))
                {
                    _store.Delete(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the original error; a leftover .tmp is ignored by List().
            }

            throw;
        }
    }

    /// <summary>Removes the slot's file; nothing happens when it is already empty.</summary>
    public void Delete(SlotId slot)
    {
        var file = slot.FileName;
        if (_store.Exists(file))
        {
            _store.Delete(file);
        }
    }
}
