namespace OSAntiCheat.Detection;

/// <summary>
/// Presentation-layer throttle for admin CHAT notices (owner's requirement: admins must not be
/// spammed off the regular chat). The durable JSONL/console log is untouched — this gates only
/// what reaches chat:
///   • one Watch notice and one Review notice per player per map — the engine re-raises a tier
///     every time a decayed score re-crosses the threshold (hover-spam), chat repeats none of it;
///   • a global quiet window between Watch notices (a broadly-firing axis must not flood chat
///     with ten players in one round) — Review notices bypass the window: they are rare, serious,
///     and always delivered.
/// Reset per map; a vacated slot forgets its state so the next occupant gets fresh notices.
/// </summary>
public sealed class AdminChatThrottle
{
    private readonly float _watchQuietSeconds;
    private readonly Dictionary<int, SuspicionTier> _chatted = new();
    private float _lastWatchChat = float.MinValue;

    public AdminChatThrottle(float watchQuietSeconds = 60f)
    {
        _watchQuietSeconds = Math.Max(0f, watchQuietSeconds);
    }

    public void Remove(int slot) => _chatted.Remove(slot);

    public void Reset()
    {
        _chatted.Clear();
        _lastWatchChat = float.MinValue;
    }

    /// <summary>True if this tier escalation should reach admin chat; updates the throttle state
    /// only when it says yes, so a globally-suppressed Watch can still deliver on a later raise.</summary>
    public bool ShouldNotify(int slot, SuspicionTier tier, float now)
    {
        if (tier <= _chatted.GetValueOrDefault(slot)) return false; // this player already chatted at this height
        if (tier == SuspicionTier.Watch && now - _lastWatchChat < _watchQuietSeconds)
            return false; // quiet window — the log has it; chat stays readable

        _chatted[slot] = tier;
        if (tier == SuspicionTier.Watch) _lastWatchChat = now;
        return true;
    }
}
