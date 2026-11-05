using System.Globalization;

namespace Roadman.Core.Saves;

/// <summary>
/// What one row of the slot list shows (<c>docs/specs/menu-settings-save.md</c>, "Slot list").
/// </summary>
/// <param name="Name">"Autosave" or "Slot n".</param>
/// <param name="Detail">
/// A valid save: the map's display name, the local time saved (<c>yyyy-MM-dd HH:mm</c>), the money and the play time,
/// joined by " · "; "Empty"; "Damaged (reason)"; or, in load mode, "Map &lt;id&gt; not installed".
/// </param>
/// <param name="Hint">"Saved automatically when you quit" for the autosave row in save mode, else null.</param>
/// <param name="Enabled">Whether the row can be chosen.</param>
/// <param name="CanDelete">Whether the row has a Delete button (every non-empty row).</param>
public sealed record SlotRowText(string Name, string Detail, string? Hint, bool Enabled, bool CanDelete)
{
    public const string EmptyText = "Empty";

    public const string AutosaveHint = "Saved automatically when you quit";

    /// <summary>The row's one-line text: name, detail and hint.</summary>
    public string Line => Hint is null ? $"{Name}   {Detail}" : $"{Name}   {Detail}   ({Hint})";
}

/// <summary>Builds <see cref="SlotRowText"/> for the slot list.</summary>
public static class SlotText
{
    /// <summary>The local date and time format of a save.</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <param name="info">The slot.</param>
    /// <param name="saveMode">True in save mode, false in load mode.</param>
    /// <param name="mapInstalled">Whether a map id has a pack (<c>res://maps/&lt;id&gt;/manifest.json</c>).</param>
    /// <param name="mapDisplayName">The display name of a map id (its manifest's, or the id).</param>
    /// <param name="zone">The local time zone.</param>
    public static SlotRowText Format(
        SlotInfo info,
        bool saveMode,
        Func<string, bool> mapInstalled,
        Func<string, string> mapDisplayName,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(mapInstalled);
        ArgumentNullException.ThrowIfNull(mapDisplayName);
        ArgumentNullException.ThrowIfNull(zone);

        var autosave = info.Slot.IsAutosave;
        var hint = saveMode && autosave ? SlotRowText.AutosaveHint : null;
        var chooseable = saveMode ? !autosave : info.Status == SlotStatus.Valid;
        var canDelete = info.Status != SlotStatus.Empty;
        string detail;
        switch (info.Status)
        {
            case SlotStatus.Valid when info.Save is { } save:
                if (!saveMode && !mapInstalled(save.MapId))
                {
                    detail = $"Map {save.MapId} not installed";
                    chooseable = false;
                }
                else
                {
                    detail = string.Join(
                        " · ",
                        mapDisplayName(save.MapId),
                        LocalTime(save.SavedAtUtc, zone),
                        string.Create(CultureInfo.InvariantCulture, $"Money {save.Player.Money}"),
                        PlayTimeText.Format(save.PlaySeconds));
                }

                break;
            case SlotStatus.Damaged:
                detail = $"Damaged ({info.DamageReason ?? SlotInfo.UnreadableReason})";
                break;
            default:
                detail = SlotRowText.EmptyText;
                break;
        }

        return new SlotRowText(info.Slot.DisplayName, detail, hint, chooseable, canDelete);
    }

    /// <summary><paramref name="savedAtUtc"/> in <paramref name="zone"/>, as <see cref="TimeFormat"/> (24-hour).</summary>
    public static string LocalTime(DateTimeOffset savedAtUtc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(savedAtUtc, zone).ToString(TimeFormat, CultureInfo.InvariantCulture);
}
