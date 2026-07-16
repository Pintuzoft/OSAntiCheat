namespace OSAntiCheat.Detection;

/// <summary>
/// A single suspicion signal raised by a detector about one player at one moment.
///
/// <see cref="Confidence"/> is graded 0..1 (never a bare on/off) so the fusion engine
/// can weight a blatant event above a marginal one. <see cref="Reason"/> is the
/// human-readable "why" carried into logs so a human reviewer can judge the raw evidence.
/// </summary>
public readonly record struct Signal(
    string Detector,   // stable detector id, e.g. "aimbot.sweep"
    int PlayerSlot,
    float Time,        // server time (seconds) the signal was raised
    float Confidence,  // 0..1
    string Reason);
