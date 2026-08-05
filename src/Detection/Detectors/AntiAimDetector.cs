using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags ANTI-AIM: a client desyncing/contorting its own replicated view angles to be hard to hit.
/// Two independent mechanical impossibilities, measured on the player's OWN angles (no enemy needed):
///
///   • FAKE PITCH — |pitch| beyond the engine's ±89° clamp (the CS:GO "standing on their head"
///     look). Measured 2026-07-25 on local demos: the honest population parks at exactly 89.00
///     (the clamp is visible in the data) and NEVER exceeds it — CS2 clamps server-side. The gate
///     is therefore a free tripwire: it can only fire if something bypassed the normal input path.
///   • YAW JITTER — tick-to-tick yaw deltas that are BOTH large (≥ <see cref="_jitterDeg"/>) AND
///     sign-alternating, sustained. A human flick is monotonic; a spinbot rotates monotonically
///     (that is <see cref="SpinbotDetector"/>'s axis — the two stay independent); only jitter-AA
///     reverses direction every tick. Honest maximum measured across demos: ONE alternation
///     (a single back-and-forth flick). The gate (≥ <see cref="_jitterFlips"/> consecutive
///     alternations ≈ reversing aim direction 6+ times inside ~100 ms) is far beyond any hand.
///
/// Value on a non-HvH server is CORROBORATION: anti-aim ships in the same rage packages as
/// spin/silent aim, and a second independent axis collapses time-to-confirm to seconds.
///
/// Feed via <see cref="OnPoll"/> at any cadence — it processes each tick-pair exactly once using
/// the tracker's sequence numbers, so a 0.2 s poll over a ~2 s ring buffer misses nothing.
/// </summary>
public sealed class AntiAimDetector : IDetector
{
    public string Id => "antiaim";
    public float Weight => 1.5f;                        // logic-breach axis, but a defensive cheat: corroboration value
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private const int MinPitchTicks = 3;                // consecutive ticks past the clamp (one could be a parse glitch)
    private const float CooldownSeconds = 10f;          // one signal per episode, not one per tick

    private readonly float _pitchDeg;                   // beyond this = past the engine clamp
    private readonly float _jitterDeg;                  // a tick-to-tick yaw delta this big counts as a jerk
    private readonly int _jitterFlips;                  // consecutive sign-alternations required

    private sealed class SlotState
    {
        public int LastSeq = int.MinValue;
        public float LastYaw;
        public bool HaveYaw;
        public int JitterSign;
        public int JitterRun;
        public int PitchRun;
        public float LastSignal = float.NegativeInfinity;
    }

    private readonly Dictionary<int, SlotState> _slots = new();

    public AntiAimDetector(float pitchDeg = 89.5f, float jitterDeg = 45f, int jitterFlips = 6)
    {
        _pitchDeg = pitchDeg;
        _jitterDeg = jitterDeg;
        _jitterFlips = Math.Max(2, jitterFlips);
    }

    public void Remove(int slot) => _slots.Remove(slot);

    /// <summary>
    /// Process all samples newer than the last call (any poll cadence). Returns a signal when the
    /// player's own angles do something a real client cannot: pitch past the engine clamp, or a
    /// sustained alternating yaw jitter.
    /// </summary>
    public Signal? OnPoll(PlayerTracker tracker, float now)
    {
        if (tracker.Count == 0) return null;
        if (!_slots.TryGetValue(tracker.Slot, out var st))
            _slots[tracker.Slot] = st = new SlotState();

        Signal? signal = null;
        // Oldest-to-newest over the unprocessed tail, so the run counters see ticks in order.
        for (int age = tracker.Count - 1; age >= 0; age--)
        {
            var s = tracker[age];
            if (s.Sequence <= st.LastSeq) continue;
            bool consecutive = s.Sequence == st.LastSeq + 1;
            st.LastSeq = s.Sequence;

            if (!s.Alive || !consecutive)
            {
                // A gap or death breaks every tick-to-tick claim; restart the runs. The pitch check
                // is per-sample (no delta involved), so an alive run-starter still counts as one.
                st.HaveYaw = false;
                st.JitterSign = 0;
                st.JitterRun = 0;
                st.PitchRun = s.Alive && MathF.Abs(s.Angles.Pitch) > _pitchDeg ? 1 : 0;
                if (s.Alive) { st.LastYaw = s.Angles.Yaw; st.HaveYaw = true; }
                continue;
            }

            // Fake pitch: past the engine clamp on consecutive ticks. The clamp itself (89.00
            // exactly) is honest and common — only BEYOND it is impossible.
            st.PitchRun = MathF.Abs(s.Angles.Pitch) > _pitchDeg ? st.PitchRun + 1 : 0;
            if (st.PitchRun >= MinPitchTicks && now - st.LastSignal > CooldownSeconds)
            {
                st.LastSignal = now;
                signal = new Signal(Id, tracker.Slot, now, 0.95f,
                    $"pitch {s.Angles.Pitch:F1}° past the ±{_pitchDeg:F1}° engine clamp for {st.PitchRun} ticks — not a real client",
                    Edge: "fake-pitch"); // CS2 clamps server-side at 89.00 — past the clamp is auto-action material
                                         // (yaw jitter stays edge-less: strong, but corroboration only for now)
            }

            // Yaw jitter: large delta with alternating sign, sustained.
            if (st.HaveYaw)
            {
                float d = Geometry.YawDelta(st.LastYaw, s.Angles.Yaw);
                if (MathF.Abs(d) >= _jitterDeg)
                {
                    int sign = MathF.Sign(d) >= 0 ? 1 : -1;
                    st.JitterRun = st.JitterSign == -sign ? st.JitterRun + 1 : 0;
                    st.JitterSign = sign;
                    if (st.JitterRun >= _jitterFlips && now - st.LastSignal > CooldownSeconds)
                    {
                        st.LastSignal = now;
                        signal = new Signal(Id, tracker.Slot, now, 0.9f,
                            $"yaw jitter: {st.JitterRun} consecutive ≥{_jitterDeg:F0}° direction reversals " +
                            $"(~{st.JitterRun + 1} ticks) — aim cannot alternate at tick rate");
                    }
                }
                else
                {
                    st.JitterSign = 0;
                    st.JitterRun = 0;
                }
            }
            st.LastYaw = s.Angles.Yaw;
            st.HaveYaw = true;
        }
        return signal;
    }
}
