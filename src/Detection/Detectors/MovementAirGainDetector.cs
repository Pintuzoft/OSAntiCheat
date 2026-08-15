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
/// chain shrugs off a single boosted outlier. The burst STARTER is promoted retroactively when
/// the next hop chains onto it — a 4-hop burst counts 4 arcs, so short-burst scripts (C9 ran
/// 3–4-hop chains) cannot slide under the arc minimum by keeping chains short.
///
/// Corpus 43 demos (HopProbe v4 2026-08-15, launch floor 120, retro-chained, the same rolling
/// statistic this detector evaluates): honest max windowed 5-arc median +21.0 u/s across 124
/// sessions (4-arc windows reach 33.5 — why the window minimum is five). C9: 6 chained arcs,
/// windowed median +71.1, every single jump gaining +67…+150. The edge gate (median ≥ +40 AND
/// median peak ≥ 300 u/s over ≥5 chained arcs) sits ~3× above the honest maximum ever measured
/// and additionally demands sustained over-sprint speed — beyond-human on both counts, so the
/// edge is auto-action material (the freeze response). The whisper gate (median ≥ +25 at median
/// peak ≥ 250 over ≥5) feeds fusion: bhop ships in the same rage packages as wall/aim, and this is
/// the stack's only movement axis — fully independent corroboration. The whisper's peak floor is
/// the sprint cap: a script bhops to go FASTER than running, so a chain whose peaks never reach
/// 250 is a hand losing speed to its own landings and strafing some back (R1, 2026-08-15
/// de_vandal: launch ~175, median gain +37, median peak 216 — demo-verified human). A whisper also
/// demands FRESH evidence — at least two chained arcs since the last signal (a burst, not a stale
/// re-read) — because the window holds arcs for 90 s and every landing re-evaluates it: without
/// that gate one burst re-fires on later lone jumps each cooldown and fusion counts a single
/// event three times (how R1 reached Review).
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
    private const float MinLaunchSpeed = 120f;          // the takeoff clamp parks cheaters near ~180 — the floor must sit well below it
    private const float TeleportSpeed = 450f;           // beyond legal move speed = respawn/teleport, void the arc
    private const float WindowSeconds = 90f;
    private const float CooldownSeconds = 20f;
    private const float TickRate = 64f;

    private readonly int _minArcs;                      // whisper: chained arcs needed in the window
    private readonly float _signalMedianGain;           // whisper: median air gain (u/s)
    private readonly float _signalMinPeakSpeed;         // whisper: median per-arc peak (u/s) — sub-sprint chains are hands
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
        public (float Time, float Gain, float Peak)? PendingStarter;
        public readonly List<(float Time, float Gain, float Peak)> Arcs = new();
        public float LastSignal = float.NegativeInfinity;
        public int FreshArcs;                           // chained arcs landed since the last signal
    }

    private readonly Dictionary<int, SlotState> _slots = new();

    public MovementAirGainDetector(
        int minArcs = 5, float signalMedianGain = 25f, float signalMinPeakSpeed = 250f,
        int edgeMinArcs = 5, float edgeMedianGain = 40f, float edgeMinPeakSpeed = 300f)
    {
        _minArcs = Math.Max(2, minArcs);
        _signalMedianGain = signalMedianGain;
        _signalMinPeakSpeed = signalMinPeakSpeed;
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
            bool shapeOk = st.Valid
                && dur >= MinArcSeconds && dur <= MaxArcSeconds
                && st.ZMax - st.ZMin <= MaxArcRise;
            if (shapeOk && st.Chained)
            {
                // The burst STARTER belongs to the chain: hop 1 has no landing behind it, but the
                // moment hop 2 chains onto it, both are the same scripted run — a 4-hop burst must
                // count 4 arcs, or short-burst scripts slide under the arc minimum.
                if (st.PendingStarter is { } starter) { st.Arcs.Add(starter); st.FreshArcs++; }
                st.PendingStarter = null;
                st.Arcs.Add((now, st.PeakSpeed - st.LaunchSpeed, st.PeakSpeed));
                st.FreshArcs++;
            }
            else if (shapeOk && st.LaunchSpeed >= MinLaunchSpeed)
            {
                // A lone jump-shaped arc at speed: kept pending; joins the window only if the
                // next hop chains onto it, otherwise the next starter replaces it.
                st.PendingStarter = (now, st.PeakSpeed - st.LaunchSpeed, st.PeakSpeed);
            }
            else st.PendingStarter = null;
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
                st.FreshArcs = 0;
                signal = new Signal(Id, tracker.Slot, now, 0.95f,
                    $"gains speed mid-air across {n} chained hops: median +{medGain:F0} u/s per hop, " +
                    $"median peak {medPeak:F0} u/s (sprint cap 250; honest corpus max +21 median) — " +
                    "air-strafe sync at tick rate, not a hand",
                    Edge: "airgain-chain");
            }
            // The whisper demands: over-sprint peaks (a chain slower than running is a hand, not a
            // script — R1's verified-human burst peaked at 216) and FRESH evidence, at least two
            // chained arcs since the last signal. Two, because a burst is by definition ≥2 landings:
            // the 90 s window re-evaluates on every landing, so a lone jump — or a single arc
            // trailing the signal that consumed its burst — must never re-fire the same stale
            // window each cooldown and let fusion count one event three times.
            else if (medGain >= _signalMedianGain && medPeak >= _signalMinPeakSpeed
                     && st.FreshArcs >= 2 && now - st.LastSignal > CooldownSeconds)
            {
                st.LastSignal = now;
                st.FreshArcs = 0;
                float span = MathF.Max(1f, _edgeMedianGain - _signalMedianGain);
                float conf = 0.5f + 0.3f * MathF.Min(1f, (medGain - _signalMedianGain) / span);
                signal = new Signal(Id, tracker.Slot, now, conf,
                    $"gains speed mid-air across {n} chained hops: median +{medGain:F0} u/s per hop, " +
                    $"median peak {medPeak:F0} u/s (honest corpus max +21 median) — bunnyhop-script territory");
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
