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
///
/// v0.9.5 adds a POPULATION-RELATIVE gate on top of the absolute test. Live data (2026-07-25→30)
/// showed the absolute z is map-dependent: on night-variant maps (de_ancient_night median z=10,
/// de_nightfever 7, normal maps ~5) EVERY player's asymmetry inflates — most plausibly because
/// spotted state is unreliable there, so enemies the observer genuinely sees still count as
/// "unspotted" and everyone tracks their present. Whatever the mechanism, the artifact hits the
/// whole population identically, so the fix is the project's standing principle: measure the
/// player AGAINST the concurrent population, not against a fixed constant. A cheater must have
/// a present-bias EXCESS over everyone else on the same map (two-proportion z on the discordant
/// rates); when the rest of the population is too thin to be a baseline, behaviour falls back to
/// the absolute test alone. The relative gate can only ever SUPPRESS emissions, never add them.
/// Evidence is per-map: the plugin resets this detector on map change.
/// </summary>
public sealed class NullTestDetector : IDetector
{
    public string Id => "wallhack.nulltest";
    public float Weight => _weight;

    private readonly float _weight;
    private readonly int _minObservations; // discordant pairs (b+c) required before z is trusted
    private readonly float _minZ;           // z at/above which the detector emits
    private readonly int _minPopObservations; // rest-of-population discordant pairs to trust the baseline
    private readonly Dictionary<int, State> _state = new();
    // Map-wide discordant totals (all players since the last Reset). Contributions from players
    // who since disconnected stay in: they remain valid evidence of THIS MAP's baseline.
    private long _totalB, _totalC;

    public NullTestDetector(
        int minObservations = 30, float minZ = 3.0f, float weight = 1.0f,
        int minPopObservations = 200)
    {
        _minObservations = Math.Max(1, minObservations);
        _minZ = minZ;
        _weight = weight;
        _minPopObservations = Math.Max(1, minPopObservations);
    }

    public void Remove(int slot) => _state.Remove(slot);

    public void Reset()
    {
        _state.Clear();
        _totalB = 0;
        _totalC = 0;
    }

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
        _totalB += nowOnly;
        _totalC += pastOnly;

        int discordant = st.B + st.C;
        if (discordant < _minObservations) return null;

        float z = (st.B - st.C) / MathF.Sqrt(discordant);
        if (z < _minZ) return null;

        // Population-relative gate: is this player's present-bias an EXCESS over the rest of the
        // current map's population? Two-proportion z on the discordant present-rate, player vs
        // everyone-else. A map artifact (night maps, broken occluders) inflates both sides
        // equally and cancels here; only standing out from your peers survives. With a thin
        // baseline the gate abstains (absolute test alone — the pre-v0.9.5 behaviour).
        long restB = _totalB - st.B, restC = _totalC - st.C;
        long restN = restB + restC;
        float zEff = z;
        float popRate = float.NaN;
        if (restN >= _minPopObservations)
        {
            float p1 = (float)st.B / discordant;
            float p0 = (float)restB / restN;
            popRate = p0;
            float pooled = (float)(st.B + restB) / (discordant + restN);
            float se = MathF.Sqrt(pooled * (1f - pooled) * (1f / discordant + 1f / restN));
            float zRel = se > 0f ? (p1 - p0) / se : 0f;
            zEff = MathF.Min(z, zRel);
            if (zEff < _minZ) return null;
        }

        // Escalation gate: emit only when z reaches a new higher integer band, so a persistent
        // cheater escalates a few times instead of firing every poll (which would re-inflate the
        // fused score with exposure — the exact failure of the raw-excess version).
        int band = (int)MathF.Floor(zEff);
        if (band <= st.LastBand) return null;
        st.LastBand = band;

        float confidence = Math.Clamp((zEff - _minZ) / 3f + 0.4f, 0.4f, 1f);
        return new Signal(
            Id, slot, now, confidence,
            $"aim snaps to unspotted enemies' present over past: z={zEff:F1} " +
            $"({st.B} present-only vs {st.C} past-only of {discordant} discordant samples" +
            (float.IsNaN(popRate)
                ? ", population baseline thin — absolute test only"
                : $"; own present-rate {(float)st.B / discordant:P0} vs map population {popRate:P0}") + ")");
    }

    private sealed class State
    {
        public int B;          // present-hit, past-miss
        public int C;          // past-hit, present-miss
        public int LastBand = -1;
    }
}
