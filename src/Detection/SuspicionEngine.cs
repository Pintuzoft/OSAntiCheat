namespace OSAntiCheat.Detection;

/// <summary>Escalation tiers. Response in v1 is log/notify only — never auto-action.</summary>
public enum SuspicionTier
{
    None = 0,
    Watch = 1,   // log quietly
    Review = 2,  // notify admins for human review
}

/// <summary>Tunable parameters for the fusion engine. Overridden by config in a later step.</summary>
public sealed record SuspicionConfig
{
    /// <summary>Exponential decay time constant (seconds); score falls to ~37% after this long.</summary>
    public float DecayTau { get; init; } = 90f;

    /// <summary>Window (seconds) over which distinct detectors count as corroborating.</summary>
    public float CorroborationWindow { get; init; } = 10f;

    /// <summary>Extra weight per additional distinct detector firing within the window (+50% each).</summary>
    public float CorroborationBonus { get; init; } = 0.5f;

    public float WatchThreshold { get; init; } = 1.0f;
    public float ReviewThreshold { get; init; } = 2.5f;
}

/// <summary>Emitted when a player crosses into a higher tier, carrying context for review.</summary>
public readonly record struct SuspicionAlert(
    int PlayerSlot,
    SuspicionTier Tier,
    float Score,
    float Time,
    IReadOnlyList<Signal> RecentSignals);

/// <summary>
/// Fuses independent detector signals into one per-player suspicion score (the
/// "triangulation" model). Key properties:
///   • graded confidence, not on/off;
///   • exponential decay so old flags fade;
///   • a corroboration bonus so several *distinct* detectors in a short window
///     weigh more than the same total from one detector;
///   • tiers gated for human review — never auto-action on a single signal.
/// </summary>
public sealed class SuspicionEngine
{
    private readonly SuspicionConfig _config;
    private readonly Dictionary<int, PlayerState> _state = new();

    /// <summary>Raised only when a player moves to a higher tier than before.</summary>
    public event Action<SuspicionAlert>? TierRaised;

    public SuspicionEngine(SuspicionConfig? config = null)
        => _config = config ?? new SuspicionConfig();

    /// <summary>Feed a detector's signal into the fusion. <paramref name="weight"/> is the detector's fusion weight.</summary>
    public void Report(in Signal signal, float weight)
    {
        if (!_state.TryGetValue(signal.PlayerSlot, out var st))
        {
            st = new PlayerState { LastUpdate = signal.Time };
            _state[signal.PlayerSlot] = st;
        }

        // Decay the accumulated score forward to this signal's time.
        float dt = signal.Time - st.LastUpdate;
        if (dt > 0f)
            st.Score *= MathF.Exp(-dt / _config.DecayTau);
        st.LastUpdate = signal.Time;

        // Keep only signals within the corroboration window, then add this one.
        // (Local copy: an `in` parameter can't be captured by the lambda.)
        float cutoff = signal.Time - _config.CorroborationWindow;
        st.Recent.RemoveAll(s => s.Time < cutoff);
        st.Recent.Add(signal);

        // Corroboration: distinct detectors firing together amplify each other.
        int distinctDetectors = CountDistinctDetectors(st.Recent);
        float corroboration = 1f + _config.CorroborationBonus * (distinctDetectors - 1);

        st.Score += weight * Math.Clamp(signal.Confidence, 0f, 1f) * corroboration;

        var tier = TierFor(st.Score);
        if (tier > st.Tier)
        {
            st.Tier = tier;
            TierRaised?.Invoke(new SuspicionAlert(
                signal.PlayerSlot, tier, st.Score, signal.Time, st.Recent.ToArray()));
        }
        else if (tier < st.Tier)
        {
            // Allow silent de-escalation as the score decays; only escalation alerts.
            st.Tier = tier;
        }
    }

    /// <summary>Current decayed score for a player at time <paramref name="now"/>.</summary>
    public float ScoreOf(int slot, float now)
    {
        if (!_state.TryGetValue(slot, out var st)) return 0f;
        float dt = now - st.LastUpdate;
        return dt > 0f ? st.Score * MathF.Exp(-dt / _config.DecayTau) : st.Score;
    }

    public void Remove(int slot) => _state.Remove(slot);

    public void Reset() => _state.Clear();

    private SuspicionTier TierFor(float score) =>
        score >= _config.ReviewThreshold ? SuspicionTier.Review
        : score >= _config.WatchThreshold ? SuspicionTier.Watch
        : SuspicionTier.None;

    private static int CountDistinctDetectors(List<Signal> signals)
    {
        // Small list (bounded by window); a HashSet keeps it allocation-light and clear.
        var seen = new HashSet<string>();
        foreach (var s in signals) seen.Add(s.Detector);
        return seen.Count;
    }

    private sealed class PlayerState
    {
        public float Score;
        public float LastUpdate;
        public SuspicionTier Tier;
        public readonly List<Signal> Recent = new();
    }
}
