using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags BUNNYHOP/STRAFE SCRIPTS: horizontal speed GAINED WHILE AIRBORNE, sustained across a
/// chain of jumps. The server's own countermeasures police the ground, not the air:
/// <c>sv_autobunnyhopping 0</c> demands frame-perfect re-jumps (trivial for a script) and
/// <c>sv_enablebunnyhopping 0</c> clamps speed at the moment of takeoff — but air-strafe
/// acceleration AFTER the clamp is plain shared physics, and a bot syncing yaw and strafe key
/// every tick pumps back ~50–100 u/s per hop. C9 (labelled bhop+wall+aim, 2026-08-14) launched
/// clamp-capped at ~300 and landed at ~400, hop after hop, above the 250 u/s sprint cap.
///
/// A qualifying ARC is shaped like a jump and nothing else: upward launch (falls launch
/// downward), 0.3–1.2 s airborne, ≤120 u of z-span — a surf ride is one long airborne phase
/// (ramps are too steep to ground on) and fails both caps, so surfing is structurally invisible
/// to this axis. Arcs count only when CHAINED (re-launch within ~0.2 s of landing at speed):
/// isolated jumps, ledge walk-offs and HE boosts never build a chain, and the median across the
/// chain shrugs off a single boosted outlier.
///
/// Corpus 43 demos / 261 honest sessions with ≥4 chained arcs (HopProbe 2026-08-15): median
/// air gain per session max +14.3 u/s, p99 +13.8 (downhill hop bursts live here — stamina kills
/// them by the third hop). C9: +67.3 median, 8/8 arcs ≥ +60. The edge gate (median ≥ +40 AND
/// median peak ≥ 300 u/s over ≥5 chained arcs) sits ~3× above the honest maximum ever measured
/// and additionally demands sustained over-sprint speed — beyond-human on both counts, so the
/// edge is auto-action material (the freeze response). The whisper gate (median ≥ +25 over ≥4)
/// feeds fusion: bhop ships in the same rage packages as wall/aim, and this is the stack's only
/// movement axis — fully independent corroboration.
///
/// Feed via <see cref="OnPoll"/> at any cadence — it processes each tick exactly once using the
/// tracker's sequence numbers, so a 0.2 s poll over the ~2 s ring buffer misses nothing.
/// </summary>
public sealed class MovementAirGainDetector : IDetector
{
    public string Id => "movement.airgain";
    public float Weight => 1.5f;                        // independent movement axis: high corroboration value
    public DetectorKind Kind => DetectorKind.LogicBreach;

    // Structural shape of a hop (not server-tunable — this is what a jump IS):
    private const float MinArcSeconds = 0.30f;          // shorter = stair/ledge noise
    private const float MaxArcSeconds = 1.20f;          // longer = surf ride or a real fall
    private const float MaxArcRise = 120f;              // z-span beyond a jump arc = ramp/drop
    private const float MinLaunchVz = 50f;              // upward launch = a jump; falls start downward
    private const int ChainMaxTicks = 14;               // re-launch within ~0.22 s of landing = chained
    private const float MinLaunchSpeed = 180f;          // slow-walk chains gain nothing worth measuring
    private const float TeleportSpeed = 450f;           // beyond legal move speed = respawn/teleport, void the arc
    private const float WindowSeconds = 90f;
    private const float CooldownSeconds = 20f;
    private const float TickRate = 64f;

    private readonly int _minArcs;                      // whisper: chained arcs needed in the window
    private readonly float _signalMedianGain;           // whisper: median air gain (u/s)
    private readonly int _edgeMinArcs;                  // edge: chained arcs needed
    private readonly float _edgeMedianGain;             // edge: median air gain (u/s)
    private readonly float _edgeMinPeakSpeed;           // edge: median per-arc peak speed (u/s, sprint = 250)

    private sealed class SlotState
    {
        public int LastSeq = int.MinValue;
        public bool InAir;
        public bool Chained;
        public bool Valid;
        public float LaunchSpeed;
        public float PeakSpeed;
        public float ZMin, ZMax;
        public int AirTicks;
        public int GroundTicksSinceLand = int.MaxValue / 2;
        public readonly List<(float Time, float Gain, float Peak)> Arcs = new();
        public float LastSignal = float.NegativeInfinity;
    }

    private readonly Dictionary<int, SlotState> _slots = new();

    public MovementAirGainDetector(
        int minArcs = 4, float signalMedianGain = 25f,
        int edgeMinArcs = 5, float edgeMedianGain = 40f, float edgeMinPeakSpeed = 300f)
    {
        _minArcs = Math.Max(2, minArcs);
        _signalMedianGain = signalMedianGain;
        _edgeMinArcs = Math.Max(_minArcs, edgeMinArcs);
        _edgeMedianGain = edgeMedianGain;
        _edgeMinPeakSpeed = edgeMinPeakSpeed;
    }

    public void Remove(int slot) => _slots.Remove(slot);

    /// <summary>Whole-map reset (fresh lobby, fresh evidence) — same discipline as the other per-map axes.</summary>
    public void Reset() => _slots.Clear();

    /// <summary>
    /// Process all samples newer than the last call. Returns a signal when the recent chained-hop
    /// window shows sustained airborne speed gain no hand can strafe-sync.
    /// </summary>
    public Signal? OnPoll(PlayerTracker tracker, float now)
    {
        if (tracker.Count == 0) return null;
        if (!_slots.TryGetValue(tracker.Slot, out var st))
            _slots[tracker.Slot] = st = new SlotState();

        Signal? signal = null;
        for (int age = tracker.Count - 1; age >= 0; age--)
        {
            var s = tracker[age];
            if (s.Sequence <= st.LastSeq) continue;
            bool consecutive = s.Sequence == st.LastSeq + 1;
            st.LastSeq = s.Sequence;

            if (!s.Alive || !consecutive)
            {
                // Death or a sampling gap voids the in-flight arc and the chain — but completed
                // arcs stay: the window prune is the only thing that forgets evidence.
                st.InAir = false;
                st.GroundTicksSinceLand = int.MaxValue / 2;
                continue;
            }

            float hSpeed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);

            if (!st.InAir)
            {
                if (!s.OnGround && s.Velocity.Z > MinLaunchVz)
                {
                    // Launch. Chained = quick re-jump at speed; walked-off ledges never launch upward.
                    st.InAir = true;
                    st.Chained = st.GroundTicksSinceLand <= ChainMaxTicks && hSpeed >= MinLaunchSpeed;
                    st.Valid = hSpeed < TeleportSpeed;
                    st.LaunchSpeed = hSpeed;
                    st.PeakSpeed = hSpeed;
                    st.ZMin = st.ZMax = s.Origin.Z;
                    st.AirTicks = 1;
                }
                else if (s.OnGround)
                {
                    st.GroundTicksSinceLand =
                        st.GroundTicksSinceLand >= int.MaxValue / 2 ? int.MaxValue / 2 : st.GroundTicksSinceLand + 1;
                }
                else
                {
                    // Airborne without an upward launch = falling; breaks any chain.
                    st.GroundTicksSinceLand = int.MaxValue / 2;
                }
                continue;
            }

            if (!s.OnGround)
            {
                st.AirTicks++;
                st.PeakSpeed = MathF.Max(st.PeakSpeed, hSpeed);
                st.ZMin = MathF.Min(st.ZMin, s.Origin.Z);
                st.ZMax = MathF.Max(st.ZMax, s.Origin.Z);
                if (hSpeed >= TeleportSpeed) st.Valid = false;
                continue;
            }

            // Landed: close the arc.
            float dur = st.AirTicks / TickRate;
            if (st.Valid && st.Chained
                && dur >= MinArcSeconds && dur <= MaxArcSeconds
                && st.ZMax - st.ZMin <= MaxArcRise)
            {
                st.Arcs.Add((now, st.PeakSpeed - st.LaunchSpeed, st.PeakSpeed));
            }
            st.InAir = false;
            st.GroundTicksSinceLand = 0;

            st.Arcs.RemoveAll(a => now - a.Time > WindowSeconds);
            if (st.Arcs.Count < _minArcs) continue;

            float medGain = Median(st.Arcs, a => a.Gain);
            float medPeak = Median(st.Arcs, a => a.Peak);
            int n = st.Arcs.Count;

            // The edge ignores the whisper cooldown: it is one-shot per evidence set (the arcs are
            // cleared), and a whisper two hops earlier must not delay the freeze on a live script.
            if (n >= _edgeMinArcs && medGain >= _edgeMedianGain && medPeak >= _edgeMinPeakSpeed)
            {
                st.LastSignal = now;
                st.Arcs.Clear();   // an edge consumed the evidence; a repeat must be earned fresh
                signal = new Signal(Id, tracker.Slot, now, 0.95f,
                    $"gains speed mid-air across {n} chained hops: median +{medGain:F0} u/s per hop, " +
                    $"median peak {medPeak:F0} u/s (sprint cap 250; honest corpus max +14 median) — " +
                    "air-strafe sync at tick rate, not a hand",
                    Edge: "airgain-chain");
            }
            else if (medGain >= _signalMedianGain && now - st.LastSignal > CooldownSeconds)
            {
                st.LastSignal = now;
                float span = MathF.Max(1f, _edgeMedianGain - _signalMedianGain);
                float conf = 0.5f + 0.3f * MathF.Min(1f, (medGain - _signalMedianGain) / span);
                signal = new Signal(Id, tracker.Slot, now, conf,
                    $"gains speed mid-air across {n} chained hops: median +{medGain:F0} u/s per hop " +
                    $"(honest corpus max +14 median) — bunnyhop-script territory");
            }
        }
        return signal;
    }

    private static float Median(List<(float Time, float Gain, float Peak)> arcs,
        Func<(float Time, float Gain, float Peak), float> pick)
    {
        var v = arcs.Select(pick).OrderBy(x => x).ToList();
        return v[v.Count / 2];
    }
}
