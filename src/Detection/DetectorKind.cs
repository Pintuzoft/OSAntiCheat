namespace OSAntiCheat.Detection;

/// <summary>
/// A detector's response class — the owner's two-tier principle (scream only at the impossible,
/// whisper about the improbable). It decides what a firing is ALLOWED to trigger, never on its own
/// but as the ceiling: a <see cref="Behavioural"/> axis can never reach auto-action however high it
/// scores, because a human could always have done it.
/// </summary>
public enum DetectorKind
{
    /// <summary>
    /// Improbable, never impossible — a human COULD have done it (a lucky hold, a great read).
    /// Review only: produces a clip for a human, escalated by repetition, never auto-action.
    /// The information/behaviour axes (deadaim, null-test) live here.
    /// </summary>
    Behavioural = 0,

    /// <summary>
    /// Mechanically impossible for a human — the hand physically cannot (a bone-lock's exact
    /// sub-quant-step aim, an anti-recoil below the motor-noise floor). Beyond-human, so a hit is
    /// near-certain and auto-action-ELIGIBLE (a human still confirms before a ban in v1).
    /// </summary>
    LogicBreach = 1,
}
