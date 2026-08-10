using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Aim drift — does the crosshair's tick-to-tick motion PULL toward the nearest enemy more than
/// the lobby's does? The per-tick sibling of the null test's information principle: legit aim
/// moves toward enemies about half the time it moves at all (measured 305 sessions / 23 demos:
/// median 51.1%, p99 55.3%, absolute max 56.6%; per-lobby z p99 2.32, max 2.79). Any assist that
/// nudges the view toward targets — smooth soft aim (C8: 59.6%, z=+4.40, binomial z=+5.3 on 977
/// steps from ONE minute alive) or aggressive multihack (C3: 56.0%, z=+4.03) — pushes the rate
/// above every honest session measured. Structurally blind to silent aim (C5: 50.1% — the view
/// never moves), which the silent-aim axis owns.
///
/// The evidence unit is the aim STEP, not the kill: every tick where the view moved ≥0.1° while
/// engaged (nearest enemy within 15°) casts one vote — did the move reduce the error or not?
/// A player in active combat casts ~15–30 votes/second, so the minimum-evidence floor
/// (`minSteps`, default 500) is ~30–60 s of engaged play and needs no kills at all; at N=500 the
/// binomial standard error is ±2.2%, tight enough that a true-59% player separates while a
/// lucky-53% streak cannot reach the gate. Fluke tolerance is layered, nulltest-style:
///   • per-lobby TWO-PROPORTION z (vs everyone else this map) — a map/mode artifact that lifts
///     everyone cancels; with a thin lobby baseline the detector ABSTAINS entirely;
///   • emission only at z ≥ minZ (3.0 — above the 2.79 honest corpus max) and only once per new
///     integer band, so a borderline session whispers once instead of spamming;
///   • Behavioural, no edge: it can NEVER act alone — worst case a fluke adds one decaying
///     0.4-confidence signal to fusion. The owner's intended surface is the Watch-tier admin
///     notice ("reached a threshold — keep an eye on this player"): a lone borderline session
///     stays under Watch; sustained drift (rising bands) or corroboration from another axis
///     inside the fusion window crosses it, and THAT is what pings admin chat.
/// Evidence is per-map (Reset on map change) like the null test; steps are reconstructed at full
/// tick rate from the ring buffers, so the 20 Hz poll cadence does not coarsen the measurement.
/// </summary>
public sealed class AimDriftDetector : IDetector
{
    public string Id => "aim.drift";
    public float Weight => _weight;
    public DetectorKind Kind => DetectorKind.Behavioural;

    private const float MaxEngageErrDeg = 15f; // beyond this the nearest enemy isn't being fought
    private const float MinStepDeg = 0.1f;     // below this the mouse didn't meaningfully move
    private const float MinDistUnits = 64f;    // degenerate range guard (same as the kill rows)
    private const float EyeHeight = 64f;

    private readonly int _minSteps;
    private readonly float _minZ;
    private readonly int _minPopSteps;
    private readonly float _weight;
    private readonly Dictionary<int, State> _state = new();
    // Map-wide totals (all players since Reset). Disconnected players' contributions stay in:
    // they remain valid evidence of THIS map's baseline — same contract as the null test.
    private long _totalB, _totalN;

    public AimDriftDetector(
        int minSteps = 500, float minZ = 3.0f, int minPopSteps = 3000, float weight = 0.5f)
    {
        _minSteps = Math.Max(50, minSteps);
        _minZ = minZ;
        _minPopSteps = Math.Max(1, minPopSteps);
        _weight = weight;
    }

    public void Remove(int slot) => _state.Remove(slot);

    public void Reset()
    {
        _state.Clear();
        _totalB = 0;
        _totalN = 0;
    }

    /// <summary>
    /// Fold the observer's unprocessed buffered ticks into the vote counts and emit when the
    /// per-lobby excess crosses a new z band. Steps are tick-exact: consecutive buffered samples
    /// matched by sequence against every enemy's buffer, nearest enemy required identical on both
    /// ticks so target-switch ticks never vote.
    /// </summary>
    public Signal? Observe(PlayerTracker observer, List<PlayerTracker> enemies, float now)
    {
        if (observer.Count < 2 || enemies.Count == 0) return null;
        if (!_state.TryGetValue(observer.Slot, out var st))
            _state[observer.Slot] = st = new State();

        for (int ago = observer.Count - 2; ago >= 0; ago--)
        {
            var cur = observer[ago];
            var prev = observer[ago + 1];
            if (cur.Sequence <= st.LastSeq) continue;
            st.LastSeq = cur.Sequence;
            if (cur.Sequence - prev.Sequence != 1 || !cur.Alive || !prev.Alive) continue;

            float step = Geometry.AngleBetween(prev.Angles, cur.Angles);
            if (step < MinStepDeg) continue;

            int nearPrev = NearestEnemy(prev.Sequence, prev, enemies, out float errPrev);
            if (nearPrev < 0 || errPrev >= MaxEngageErrDeg) continue;
            int nearCur = NearestEnemy(cur.Sequence, cur, enemies, out float errCurNearest);
            if (nearCur != nearPrev) continue; // switch tick — no vote

            st.N++;
            _totalN++;
            if (errCurNearest < errPrev) { st.B++; _totalB++; }
        }

        if (st.N < _minSteps) return null;
        long restB = _totalB - st.B, restN = _totalN - st.N;
        if (restN < _minPopSteps) return null; // lobby too thin to be a null — abstain

        float p1 = (float)st.B / st.N;
        float p0 = (float)restB / restN;
        float pooled = (float)(st.B + restB) / (st.N + restN);
        float se = MathF.Sqrt(pooled * (1f - pooled) * (1f / st.N + 1f / restN));
        if (se <= 0f) return null;
        float z = (p1 - p0) / se;
        if (z < _minZ) return null;

        int band = (int)MathF.Floor(z);
        if (band <= st.LastBand) return null;
        st.LastBand = band;

        float confidence = Math.Clamp(0.4f + (z - _minZ) / 3f * 0.3f, 0.4f, 1f);
        return new Signal(
            Id, observer.Slot, now, confidence,
            $"aim steps pull toward enemies beyond the lobby: z={z:F1} " +
            $"({st.B} of {st.N} moving steps reduce the error = {p1:P1} vs lobby {p0:P1} over {restN})");
    }

    /// <summary>Nearest enemy by head-point aim error at one tick sequence; -1 = none usable.</summary>
    private static int NearestEnemy(int seq, in Model.TickSample obs, List<PlayerTracker> enemies, out float err)
    {
        err = float.MaxValue;
        int best = -1;
        var eye = obs.Origin with { Z = obs.Origin.Z + EyeHeight };
        foreach (var enemy in enemies)
        {
            if (!enemy.TryGetBySequence(seq, out var es) || !es.Alive) continue;
            var head = es.Origin with { Z = es.Origin.Z + EyeHeight };
            if (System.Numerics.Vector3.Distance(eye, head) < MinDistUnits) continue;
            float e = Geometry.AimErrorTo(eye, obs.Angles, head);
            if (e < err) { err = e; best = enemy.Slot; }
        }
        return best;
    }

    private sealed class State
    {
        public long B;              // moving steps that reduced the error
        public long N;              // moving steps while engaged
        public int LastSeq = -1;    // newest processed sample sequence
        public int LastBand = -1;   // escalation band already emitted
    }
}
