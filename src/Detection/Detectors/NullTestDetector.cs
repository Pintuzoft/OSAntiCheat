namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// The null test as a live detector — the one signal that separated verified cheaters from the
/// regulars in offline replay. It measures an INFORMATION channel, not skill.
///
/// For every enemy the observer cannot see (not in their SpottedByMask), it asks whether the
/// crosshair sits on that enemy's CURRENT position or on where the same enemy was ~1.5s ago.
/// Game sense — common angles, footsteps, remembering where someone ran — correlates a player's
/// aim with the enemy's PAST just as well as their present; only tracking the *present* of an
/// unseen enemy is what no amount of skill buys.
///
/// v0.6.1 replaces the raw excess (present-rate − past-rate) with a **McNemar test**, because raw
/// excess is noisy at low sample counts and, worse, its accumulated score just re-measured
/// playtime. McNemar looks only at the DISCORDANT samples — present-hit-but-not-past (b) versus
/// past-hit-but-not-present (c) — and forms z = (b − c) / sqrt(b + c). Properties that make it the
/// right instrument:
///   • Skill cancels: a generally-accurate player hits both present and past (concordant), which
///     McNemar ignores. Only the ASYMMETRY toward the present counts.
///   • Sample-size-aware: z is a standardised score, so low evidence stays near 0 and never fires;
///     it takes real, sustained asymmetry to climb.
///   • No playtime confound: a regular with a true-zero effect keeps z ≈ 0 no matter how long they
///     play, so they never emit and never accumulate score — unlike the raw-excess version, which
///     drove the whole population to Review purely on exposure.
///
/// Emission is escalation-gated (only when z crosses a new integer band) so a real cheater
/// escalates a handful of times rather than spamming a signal every N samples. Log-only in v1.
/// </summary>
public sealed class NullTestDetector : IDetector
{
    public string Id => "wallhack.nulltest";
    public float Weight => _weight;

    private readonly float _weight;
    private readonly int _minObservations; // discordant pairs (b+c) required before z is trusted
    private readonly float _minZ;           // z at/above which the detector emits
    private readonly Dictionary<int, State> _state = new();

    public NullTestDetector(int minObservations = 30, float minZ = 3.0f, float weight = 1.0f)
    {
        _minObservations = Math.Max(1, minObservations);
        _minZ = minZ;
        _weight = weight;
    }

    public void Remove(int slot) => _state.Remove(slot);
    public void Reset() => _state.Clear();

    /// <summary>
    /// Fold one poll's discordant tally over the observer's unspotted enemies into the running
    /// McNemar counts. <paramref name="nowOnly"/> = samples on the enemy's PRESENT position but not
    /// its past (b); <paramref name="pastOnly"/> = on the past but not the present (c). Concordant
    /// samples (both or neither) are not passed in — McNemar ignores them. Emits at most once per
    /// integer z-band crossed, and only once z has enough discordant evidence.
    /// </summary>
    public Signal? Accumulate(int slot, float now, int nowOnly, int pastOnly)
    {
        if (nowOnly == 0 && pastOnly == 0) return null;
        if (!_state.TryGetValue(slot, out var st))
            _state[slot] = st = new State();

        st.B += nowOnly;
        st.C += pastOnly;

        int discordant = st.B + st.C;
        if (discordant < _minObservations) return null;

        float z = (st.B - st.C) / MathF.Sqrt(discordant);
        if (z < _minZ) return null;

        // Escalation gate: emit only when z reaches a new higher integer band, so a persistent
        // cheater escalates a few times instead of firing every poll (which would re-inflate the
        // fused score with exposure — the exact failure of the raw-excess version).
        int band = (int)MathF.Floor(z);
        if (band <= st.LastBand) return null;
        st.LastBand = band;

        float confidence = Math.Clamp((z - _minZ) / 3f + 0.4f, 0.4f, 1f);
        return new Signal(
            Id, slot, now, confidence,
            $"aim snaps to unspotted enemies' present over past: z={z:F1} " +
            $"({st.B} present-only vs {st.C} past-only of {discordant} discordant samples)");
    }

    private sealed class State
    {
        public int B;          // present-hit, past-miss
        public int C;          // past-hit, present-miss
        public int LastBand = -1;
    }
}
