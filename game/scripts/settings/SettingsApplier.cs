using Godot;
using Roadman.Core.Settings;

namespace Roadman.Settings;

/// <summary>
/// Applies <see cref="GameSettings"/> to the engine. For now only the audio buses (the settings view's display,
/// quality and confirm-or-revert parts come with it); the camera values are applied by <c>World</c> at every spawn and
/// load (<c>World.CameraSettings</c>).
/// </summary>
public static class SettingsApplier
{
    public const string MasterBus = "Master";
    public const string MusicBus = "Music";
    public const string EffectsBus = "Effects";

    /// <summary>
    /// Sets each bus's volume (<see cref="VolumeMath.ToDb"/>) and mute flag (<see cref="VolumeMath.IsMuted"/>, 0 %). A
    /// bus missing from the layout is skipped with a warning.
    /// </summary>
    public static void ApplyAudio(AudioSettings audio)
    {
        SetBus(MasterBus, audio.MasterVolume);
        SetBus(MusicBus, audio.MusicVolume);
        SetBus(EffectsBus, audio.EffectsVolume);
    }

    private static void SetBus(string name, int percent)
    {
        var index = AudioServer.GetBusIndex(name);
        if (index < 0)
        {
            GD.PushWarning($"Settings: no audio bus '{name}' (default_bus_layout.tres); its volume isn't applied");
            return;
        }

        var muted = VolumeMath.IsMuted(percent);
        AudioServer.SetBusMute(index, muted);
        if (!muted)
        {
            AudioServer.SetBusVolumeDb(index, (float)VolumeMath.ToDb(percent));
        }
    }
}
