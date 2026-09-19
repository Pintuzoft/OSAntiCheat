using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags impossible sustained yaw rotation (spinbot). A human can flick fast for an
/// instant, but cannot *sustain* a very high yaw rate across many consecutive ticks.
///
/// We therefore measure the average yaw rate over a short window and require it to be
/// sustained (consistent direction, not a back-and-forth artefact). Purely self-contained:
/// no enemies, no LOS — just the player's own angle history.
/// </summary>
public sealed class SpinbotDetector : IDetector
{
    public string Id => "spinbot";
    public float Weight => 1.6f;
    public DetectorKind Kind => DetectorKind.LogicBreach;

    // A fast human flick tops out well under the suspect rate sustained; spinbot runs above it.
    private readonly float _suspectRateDegPerSec;
    private const float ContinuousSpinDeg = 720f; // 2 full turns of UNBROKEN rotation — a hand can't
                                                  // (mousepad runs out; a lift-swipe breaks continuity)

    // --- Poll-spin bookkeeping: a stretch counts once, and one stretch is a burst, not a spin ---
    // The first poll-spin signal in 48 live days (2026-09-10) was the owner's own account, trying a
    // freshly installed wireless-mouse driver: one swipe of 752° same-direction at spin rate, then
    // still. The 0.2 s poll re-read that one stretch out of the 2 s ring buffer nine times and fusion
    // summed the first two into a Review. So a hand CAN throw two continuous turns in one go (high
    // sensitivity, one long swipe); what it cannot do is keep going. Two standing rules apply:
    // evidence counts once (airgain v0.9.107, bonelock v0.9.109) — ticks credited to a stretch are
    // spent, the next stretch must be newer rotation — and once is a fluke (the HS-kill edge's rule):
    // a spinbot serves two turns, then two more, within the next second; a burst stops.
    private const float SpinPairWindowSeconds = 2f; // a bot at the 1000°/s floor serves two turns every
                                                    // 0.72 s; the ring buffer spans 2 s
    private sealed class SpinState
    {
        public int ConsumedThroughSeq = int.MinValue; // newest tick already credited to a stretch
        public float LastStretchTime = float.NegativeInfinity;
        public int Stretches;                         // consecutive two-turn stretches inside the pair window
    }
    private readonly Dictionary<int, SpinState> _spin = new();

    // --- Spin+HS kill signature (the validated hard edge, owner's mechanics) ---
    // A spin+triggerbot fires as the whirling crosshair sweeps across a head: a HEADSHOT lands MID-spin,
    // and the yaw NEVER STOPS (a legit trickshot-360 stops to aim, breaking the spin before the shot).
    // Rate alone is a gradient (humans hit 10000°/s in flicks); >360° of CONTINUOUS rotation, still
    // spinning at the kill, is the physical impossibility — you don't rotate a full turn to aim.
    private const float SpinKillFloor = 1200f;  // deg/s to count as "still spinning" at the kill
    private const float FullTurn = 360f;        // continuous rotation a flick never reaches
    private readonly int _minSpinHsKills;       // repetition: one lucky mid-spin HS is a fluke, not a bot
    private readonly Dictionary<int, int> _spinHsKills = new();

    public SpinbotDetector(float suspectRateDegPerSec = 1000f, int minSpinHsKills = 2)
    {
        _suspectRateDegPerSec = suspectRateDegPerSec;
        _minSpinHsKills = Math.Max(1, minSpinHsKills);
    }

    public void Remove(int slot) { _spinHsKills.Remove(slot); _spin.Remove(slot); }

    /// <summary>
    /// Called on a headshot kill. Fires (LogicBreach) once the killer has landed <c>minSpinHsKills</c>
    /// headshots mid-spin — a HS while >360° of continuous rotation is still whirling at the kill tick.
    /// </summary>
    public Signal? OnKill(PlayerTracker shooter, bool headshot, float now)
    {
        if (!headshot || shooter.Count < 3) return null;

        // Must still be spinning AT the kill (a trickshot stops to aim → this fails).
        var k0 = shooter[0];
        var k1 = shooter[1];
        if (k0.Sequence - k1.Sequence != 1 || !k0.Alive || !k1.Alive) return null;
        float dt0 = k0.Time - k1.Time;
        if (dt0 <= 0f) return null;
        float step0 = Geometry.YawDelta(k1.Angles.Yaw, k0.Angles.Yaw);
        if (MathF.Abs(step0) / dt0 < SpinKillFloor) return null; // not spinning at the kill
        int dir = MathF.Sign(step0);

        // Walk back accumulating CONTINUOUS same-direction rotation; stop where the spin breaks.
        float swept = MathF.Abs(step0);
        int limit = Math.Min(96, shooter.Count - 1);
        for (int i = 1; i < limit; i++)
        {
            var a = shooter[i];
            var b = shooter[i + 1];
            if (a.Sequence - b.Sequence != 1 || !a.Alive || !b.Alive) break;
            float dt = a.Time - b.Time;
            if (dt <= 0f) break;
            float d = Geometry.YawDelta(b.Angles.Yaw, a.Angles.Yaw);
            if (MathF.Sign(d) != dir || MathF.Abs(d) / dt < SpinKillFloor) break; // spin broke here
            swept += MathF.Abs(d);
        }
        if (swept <= FullTurn) return null; // not a full continuous turn — a flick, not a spin

        int n = _spinHsKills.GetValueOrDefault(shooter.Slot) + 1;
        _spinHsKills[shooter.Slot] = n;
        if (n < _minSpinHsKills) return null; // one is a fluke; a spinbot does it every engagement

        float confidence = Math.Clamp(0.85f + 0.05f * (n - _minSpinHsKills), 0.85f, 1f);
        return new Signal(
            Id, shooter.Slot, now, confidence,
            $"{n} headshot kills mid-spin (>{FullTurn:F0}° continuous rotation, still spinning at the kill) — beyond human",
            Edge: "spin-hs-kill"); // the kill-anchored conjunction is the auto-action edge (0/321k archive kills)
    }

    /// <summary>
    /// Poll-based continuous-spin check: the longest run of UNBROKEN same-direction rotation among
    /// the buffered ticks not yet credited to an earlier stretch. A hand can't rotate >2 full turns
    /// continuously (the mousepad runs out; a lift-swipe breaks the run) — but its input pipeline
    /// can (a driver dumping a burst), so one such stretch is credited silently and only the NEXT
    /// one, arriving within <see cref="SpinPairWindowSeconds"/>, fires: a spinbot whirls on
    /// (~10 turns in 2 s), a burst stops. Catches the blatant spinner BEFORE any kill, straight from
    /// the angle history. No enemies, no LOS.
    /// </summary>
    public Signal? Inspect(PlayerTracker tracker)
    {
        if (tracker.Count < 8) return null;
        _spin.TryGetValue(tracker.Slot, out var st);
        int consumed = st?.ConsumedThroughSeq ?? int.MinValue;

        float bestSwept = 0f, runSwept = 0f;
        int dir = 0, runEndSeq = 0, bestEndSeq = 0;
        // Walk newest -> older; a break (gap, direction flip, or rate below the spin floor) resets.
        for (int i = 0; i < tracker.Count - 1; i++)
        {
            var a = tracker[i];
            var b = tracker[i + 1];
            if (a.Sequence <= consumed) break; // everything from here back was credited already
            if (!a.Alive || !b.Alive || a.Sequence - b.Sequence != 1) { runSwept = 0f; dir = 0; continue; }
            float dt = a.Time - b.Time;
            if (dt <= 0f) { runSwept = 0f; dir = 0; continue; }

            float d = Geometry.YawDelta(b.Angles.Yaw, a.Angles.Yaw);
            int sdir = MathF.Sign(d);
            if (sdir != 0 && MathF.Abs(d) / dt >= _suspectRateDegPerSec && (dir == 0 || sdir == dir))
            {
                if (dir == 0) runEndSeq = a.Sequence; // the run's newest tick (the walk goes backwards)
                dir = sdir;
                runSwept += MathF.Abs(d);
                if (runSwept > bestSwept) { bestSwept = runSwept; bestEndSeq = runEndSeq; }
            }
            else { runSwept = 0f; dir = 0; }
        }

        if (bestSwept <= ContinuousSpinDeg) return null;

        // This stretch is spent: the next one must be rotation newer than its last tick. A stretch
        // alone is a burst; the same stretch again within the window is a spin that keeps going.
        var latest = tracker[0];
        st ??= _spin[tracker.Slot] = new SpinState();
        st.ConsumedThroughSeq = bestEndSeq;
        st.Stretches = latest.Time - st.LastStretchTime <= SpinPairWindowSeconds ? st.Stretches + 1 : 1;
        st.LastStretchTime = latest.Time;
        if (st.Stretches < 2) return null;

        // Confidence ramps with the number of full turns in this stretch: 2 turns ~0.85, 4+ saturates.
        float confidence = Math.Clamp(0.85f + 0.05f * (bestSwept - ContinuousSpinDeg) / 360f, 0.85f, 1f);
        return new Signal(
            Id, tracker.Slot, latest.Time, confidence,
            $"{bestSwept:F0}° of continuous rotation ({bestSwept / 360f:F1} full turns, no stop), " +
            $"stretch {st.Stretches} back-to-back — a spin that keeps going, beyond human");
    }
}
