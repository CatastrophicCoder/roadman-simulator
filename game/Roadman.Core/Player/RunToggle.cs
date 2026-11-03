namespace Roadman.Core.Player;

/// <summary>
/// The gamepad run toggle of <c>docs/specs/player-on-foot.md</c> ("Input map"): the left-stick click
/// (<c>run_toggle</c>) turns running on or off, and returning the stick inside its deadzone turns it off, so the next
/// movement starts walking. Holding Shift (<c>run</c>) is separate and never changes it.
/// </summary>
public sealed class RunToggle
{
    private bool _wasMoving;

    /// <summary>Whether the toggle has running on.</summary>
    public bool On { get; private set; }

    /// <summary>
    /// Advances one physics frame. A press flips <see cref="On"/>. While on, a frame that isn't moving after one that
    /// was (the stick went back inside its deadzone) turns it off. A press while standing still keeps it on until
    /// movement starts and stops again.
    /// </summary>
    /// <param name="togglePressed">The toggle was just pressed this frame.</param>
    /// <param name="moving">The move input is outside its deadzone (a non-zero move vector).</param>
    /// <returns><see cref="On"/> after the update.</returns>
    public bool Update(bool togglePressed, bool moving)
    {
        if (On && _wasMoving && !moving)
        {
            On = false;
        }

        if (togglePressed)
        {
            On = !On;
        }

        _wasMoving = moving;
        return On;
    }

    /// <summary>
    /// Forgets whether the last frame was moving, so the next update can't turn the toggle off by a stop it didn't see
    /// (used when the window loses focus: the actions release, but the toggle stays as it was).
    /// </summary>
    public void ResetMovement() => _wasMoving = false;

    /// <summary>Turns the toggle off and forgets the movement state.</summary>
    public void Reset()
    {
        On = false;
        _wasMoving = false;
    }
}
