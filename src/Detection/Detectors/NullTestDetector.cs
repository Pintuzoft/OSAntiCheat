namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// The null test as a live detector — the one signal that actually separated verified cheaters
/// from the regulars in offline replay (they ranked 1/2/8 of 70; the tracking detector had them
/// mid-pack). It measures an INFORMATION channel, not skill.
///
/// For every enemy the observer cannot see (not in their SpottedByMask), compare how often the
/// crosshair sits on that enemy's CURRENT position versus where the same enemy was ~1.5s ago.
/// Game sense — common angles, footsteps, remembering where someone ran — correlates a player's
/// aim with the enemy's PAST just as well as their present, because both are only places on a map.
/// Tracking the *present* of an unseen enemy is what no amount of skill buys, and it is the same
/// number for a five-year regular as for anyone else. So the excess (present-rate − past-rate) is
/// the wallhack signal; skill cancels out in the subtraction.
///
/// Calibration method (server owner, live): start with ExcessThreshold 0 so it fires on the whole
/// population and LogAllSignals records the distribution, then raise the threshold until the
/// regulars fall out of the detections — that crossover is the baseline, and anyone above it is
/// the anomaly. Offline reference (pooled per player): legit excess p50/p90/p99 = 0.0007/0.0020/
/// 0.0037; verified cheaters 0.005–0.012. So expect the baseline around 0.004–0.008.
/// </summary>
public sealed class NullTestDetector : IDetector
{
    public string Id => "wallhack.nulltest";
    public float Weight => _weight;

    private const float ConfidenceRefExcess = 0.02f; // excess that maps to full confidence

    private readonly float _weight;
    private readonly int _minSamples;
    private readonly float _excessThreshold;
    private readonly Dictionary<int, State> _state = new();

    public NullTestDetector(int minSamples = 2000, float excessThreshold = 0f, float weight = 1.0f)
    {
        _minSamples = Math.Max(1, minSamples);
        _excessThreshold = excessThreshold;
        _weight = weight;
    }

    public void Remove(int slot) => _state.Remove(slot);
    public void Reset() => _state.Clear();

    /// <summary>
    /// Fold one poll's tally over the observer's unspotted enemies into the running totals.
    /// <paramref name="samples"/> is how many unspotted-enemy comparisons had a valid ~1.5s-old
    /// past sample this poll; <paramref name="onNow"/>/<paramref name="onPast"/> how many of those
    /// had the crosshair on the enemy's present / past position. Emits at most once per
    /// <see cref="_minSamples"/> accumulated samples, and only when the excess clears the threshold.
    /// </summary>
    public Signal? Accumulate(int slot, float now, int samples, int onNow, int onPast)
    {
        if (samples <= 0) return null;
        if (!_state.TryGetValue(slot, out var st))
            _state[slot] = st = new State();

        st.Samples += samples;
        st.OnNow += onNow;
        st.OnPast += onPast;

        // Evaluate over the whole session so far, but only once per MinSamples block so the fusion
        // engine sees a controlled emission rate proportional to exposure rather than one per poll.
        if (st.Samples - st.LastEvalSamples < _minSamples) return null;
        st.LastEvalSamples = st.Samples;

        float nowRate = (float)st.OnNow / st.Samples;
        float pastRate = (float)st.OnPast / st.Samples;
        float excess = nowRate - pastRate;
        if (excess < _excessThreshold) return null;

        float confidence = Math.Clamp(excess / ConfidenceRefExcess, 0.2f, 1f);
        return new Signal(
            Id, slot, now, confidence,
            $"aim on unspotted enemies — present {nowRate * 100f:F1}% vs 1.5s-past {pastRate * 100f:F1}% " +
            $"(excess {excess * 100f:+0.00;-0.00}pp over {st.Samples} samples)");
    }

    private sealed class State
    {
        public int Samples;
        public int OnNow;
        public int OnPast;
        public int LastEvalSamples;
    }
}
