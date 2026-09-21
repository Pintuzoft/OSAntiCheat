using System.Linq;
using System.Numerics;
using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags SILENT AIM: a bullet REGISTERED damage while the shooter's server-visible view provably
/// pointed somewhere else. The cheat sends aiming usercmd angles that differ from the rendered/
/// replicated view — so the one thing it cannot fake is the conjunction "hit landed" × "view off
/// the victim". Discovered on the banned-player archive work (2026-07-25, TODO.md): one banned
/// player (C5) landed 6/6 scout headshots with the view 2–37° off the head while spinning
/// (P≈2e-17); another (C6) killed with the view frozen 6–31° away (P≈1.5e-12). The off-floor is READ from the measured population (21-demo
/// --hurts sweep): the honest burst-opener tail ends at exactly 8.0° (n=3486, zero events ≥10°),
/// so the 10° default sits above every honest event measured while catching both archetypes
/// (C5 5 qualifying events, C6 3 — the frozen-view psilent the first 15° import missed).
///
/// Anchored on player_hurt, not kills — a cheater generates many more hurts than kills, so the
/// repetition gate closes in a fraction of the time.
///
/// The two legitimate ways a hit lands off-view, and how each is excluded:
///
///   • LAG COMPENSATION — the server rewinds the victim by the shooter's latency, so a laggy but
///     honest shot matches where the victim WAS. The error here is therefore the MINIMUM over the
///     victim's recent positions (<see cref="_lagCompTicks"/>, ~250 ms): an honest laggy shot is
///     near-zero against one of them; silent aim points away from ALL of them.
///   • RECOIL COMPENSATION — mid-spray the view is pulled well BELOW the victim (up to ~30°) while
///     the bullets land on them. Excluded structurally: only the FIRST bullet of a burst counts
///     (<see cref="NoteFire"/> + <see cref="BurstWindowSeconds"/>), where recoil is ~zero. The
///     archive cases fired singles/burst-openers, so the gate costs them nothing.
///
/// Shotguns are excluded (a pellet cone hits far off-centre honestly); point-blank is excluded
/// (angular metrics degenerate). Repetition-gated: one event can be a jump-spread fluke or an
/// interp artefact; several in a session cannot (0.0019^3 as an upper bound before the burst and
/// distance gates tighten it further).
///
/// FAR-PRECISION GRADE (v0.9.113, C11 2026-09-20 canals): five deagle headshots at 789–1210u with the
/// view 2–6° off the head — two of them with the crosshair CLOSER to another enemy than the one whose
/// head the bullet found — all under the 10° floor. That floor is read from the all-weapon hurts
/// tail, and rifles own that tail (spray + recoil: 8% of honest far ak47 headshot kills sit >4°).
/// A precision weapon at range has no such tail: honest deagle headshot kills ≥700u (archive6,
/// n=5,362) p99 1.94°, p99.9 3.55°, max 5.07°; >4° = 0.07% (usp 0.06%, glock 0.14%, awp 0 of
/// 1,184, ssg08 0.12%). Per attacker-session, TWO or more such hits: 0 honest of 42,342 — the one
/// archive session at ≥2 is C5 (3 of 5). So a head hit with a precision weapon at ≥700u whose
/// view sits ≥<c>farOffDeg</c> (4°) off every lag-comp candidate is a 0.6 whisper alone and, on a
/// second DISTINCT victim the same map, the "silent-far-hs" edge. The archive calibration used
/// the view-to-HEAD error at the kill tick; the measurement here (minimum over lag-comp candidates
/// of the view-to-nearest-body error, head sample included) is never larger, so the honest rate
/// under it can only be lower.
/// </summary>
public sealed class SilentAimDetector : IDetector
{
    public string Id => "aimbot.silent";
    public float Weight => 1.6f;                        // logic-breach axis: a confirmed pattern is near-certain
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private const float EyeHeight = 64f;
    private const float MinRangeUnits = 128f;           // close range inflates honest view-to-body angles
    private const float BurstWindowSeconds = 0.25f;     // fire within this of the previous fire = spraying
    private const float WindowSeconds = 1200f;          // hits are rare events; a long session window
    private const int MinLagCandidates = 4;             // need real victim history to rule the lag story out

    private static readonly string[] ExcludedWeapons =
    {
        "xm1014", "nova", "mag7", "sawedoff",           // pellet cones land honestly far off-centre
        "flashbang",                                    // pop damage lands with the view anywhere — it owned
                                                        // the entire false honest tail before being filtered
    };

    private readonly float _offDeg;                     // view at least this far off EVERY recent victim position
    private readonly int _minHits;                      // repeated off-view hits required before speaking
    private readonly int _lagCompTicks;                 // how far back a lag-comp rewind could reach

    private readonly Dictionary<int, (float prev, float last)> _fires = new(); // per-slot fire times
    private readonly Dictionary<int, List<float>> _hits = new();               // off-view hit times per slot

    // Far-precision grade. Head hits (player_hurt hitgroup 1) only: the calibration is on headshot
    // kills, and a body hit at range has a natural 3–5° head offset that would flood the gate.
    private const int HitgroupHead = 1;
    private const float FarMinRangeUnits = 700f;        // the archive bucket the tails were read from
    // player_hurt carries the BASE item name ("hkp2000" for USP-S and P2000); player_death the skin
    // name — list both. The spray pistols (p250, five-seven, tec-9, cz, dual berettas) and every
    // rifle/SMG/LMG stay out: their honest far-headshot tails run 4–15% past 4°.
    private static readonly string[] PrecisionWeapons =
        { "deagle", "revolver", "hkp2000", "usp_silencer", "glock", "awp", "ssg08", "scar20", "g3sg1" };
    private readonly float _farOffDeg;                  // view-off floor for the far grade
    private readonly int _farMinVictims;                // distinct victims on one map before the edge
    // Per attacker: every qualifying far hit this map, and the distinct-victim count already spoken
    // at (escalate once per new victim, never re-fire on a repeat of the same size).
    private readonly Dictionary<int, List<(float Time, int VictimSlot, string Weapon, float Err, float DistU)>> _farHits = new();
    private readonly Dictionary<int, int> _farFiredAt = new();

    public SilentAimDetector(float offDeg = 10f, int minHits = 3, int lagCompTicks = 16,
        float farOffDeg = 4f, int farMinVictims = 2)
    {
        _offDeg = offDeg;
        _minHits = Math.Max(1, minHits);
        _lagCompTicks = Math.Max(1, lagCompTicks);
        _farOffDeg = farOffDeg;
        _farMinVictims = Math.Max(1, farMinVictims);
    }

    public void Remove(int slot)
    {
        _fires.Remove(slot);
        _hits.Remove(slot);
        _farHits.Remove(slot);
        _farFiredAt.Remove(slot);
    }

    /// <summary>Map change: the far grade's per-map victim memory restarts (one demo = one map
    /// session in the calibration); the rolling windows go with it.</summary>
    public void Reset()
    {
        _fires.Clear();
        _hits.Clear();
        _farHits.Clear();
        _farFiredAt.Clear();
    }

    /// <summary>True for the pistols and snipers whose honest far-headshot tail ends near 4°.</summary>
    public static bool IsPrecisionWeapon(string weapon)
    {
        string w = weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon[7..] : weapon;
        return Array.IndexOf(PrecisionWeapons, w) >= 0;
    }

    /// <summary>
    /// Feed EVERY weapon_fire here (all shots, sprays included). Keeps the previous fire time so a
    /// hurt event — which arrives on the same tick as the fire that caused it — can tell whether
    /// that fire opened a burst or continued one.
    /// </summary>
    public void NoteFire(int slot, float now)
    {
        var t = _fires.TryGetValue(slot, out var f) ? f : (prev: float.NegativeInfinity, last: float.NegativeInfinity);
        _fires[slot] = (t.last, now);
    }

    /// <summary>True for pellet weapons, whose cone hits far off-centre honestly.</summary>
    public static bool IsPelletWeapon(string weapon)
    {
        foreach (var ex in ExcludedWeapons)
            if (weapon.Contains(ex)) return true;
        return false;
    }

    /// <summary>
    /// The detector's raw measurement, exposed so offline tooling can export the population
    /// distribution the gate is read from: minimum view-to-victim body error over every position
    /// the victim held inside the lag-comp window (an honest laggy shot matches ONE of them —
    /// the rewound position; silent aim matches none). Returns -1 when not computable: no
    /// attacker sample, dead, point-blank, or fewer than <see cref="MinLagCandidates"/> victim
    /// positions to rule the lag story out.
    /// </summary>
    public static float MeasureMinError(PlayerTracker attacker, PlayerTracker victim, int lagCompTicks,
        out int candidates, out float distU)
    {
        candidates = 0;
        distU = -1f;
        if (attacker.Count == 0) return -1f;
        var a0 = attacker[0];
        if (!a0.Alive) return -1f;
        var eye = a0.Origin + new Vector3(0f, 0f, EyeHeight);

        float minErr = float.MaxValue;
        for (int k = 0; k <= lagCompTicks; k++)
        {
            if (!victim.TryGetBySequence(a0.Sequence - k, out var v) || !v.Alive) continue;
            float d = Vector3.Distance(a0.Origin, v.Origin);
            if (d < MinRangeUnits) return -1f;           // degenerate range
            if (k == 0) distU = d;
            candidates++;
            float err = Geometry.NearestBodyAimError(eye, a0.Angles, v.Origin);
            if (err < minErr) minErr = err;
        }
        return candidates < MinLagCandidates ? -1f : minErr;
    }

    /// <summary>
    /// Called on player_hurt for bullet damage (caller filters grenades/knife/taser/world). Returns
    /// a signal once the shooter has landed <see cref="_minHits"/> first-of-burst bullets while the
    /// view was ≥ the off-floor away from every position the victim held in the lag-comp window —
    /// or, for a HEAD hit (<paramref name="hitgroup"/> 1) with a precision weapon at ≥700u, when the
    /// view sat ≥ the far floor off: one such hit is a whisper, a second distinct victim the edge.
    /// </summary>
    public Signal? OnHurt(PlayerTracker attacker, PlayerTracker victim, string weapon, int dmg, float now,
        int hitgroup = -1)
    {
        if (IsPelletWeapon(weapon)) return null;

        // The hurt arrives on the same tick as its fire, so "last" is THIS shot; burst-continuation
        // is measured against the fire before it. Mid-spray hits are recoil-confounded — skip.
        if (_fires.TryGetValue(attacker.Slot, out var f) && now - f.prev < BurstWindowSeconds) return null;

        float minErr = MeasureMinError(attacker, victim, _lagCompTicks, out _, out float distU);
        if (minErr < 0f) return null;                    // not computable: no sample, dead, point-blank, thin history

        Signal? far = null;
        if (hitgroup == HitgroupHead && distU >= FarMinRangeUnits && minErr >= _farOffDeg && IsPrecisionWeapon(weapon))
            far = NoteFarHit(attacker.Slot, victim.Slot, weapon, minErr, distU, now);
        if (far is { } fs && fs.Edge is not null) return far;   // the edge outranks everything below

        if (minErr < _offDeg) return far;                // view was on the victim (for the flat grade)

        if (!_hits.TryGetValue(attacker.Slot, out var window))
            _hits[attacker.Slot] = window = new List<float>();
        window.RemoveAll(t => now - t > WindowSeconds);
        window.Add(now);
        if (window.Count < _minHits) return far;         // one could be jump-spread luck; wait for the pattern

        float confidence = Math.Clamp(0.85f + 0.05f * (window.Count - _minHits), 0.85f, 1f);
        return new Signal(
            Id, attacker.Slot, now, confidence,
            $"{window.Count} bullet hits with view ≥{_offDeg:F0}° off the victim (this one {minErr:F1}°, " +
            $"{weapon} dmg {dmg}) — shot direction ≠ view direction");
    }

    /// <summary>
    /// Record a far-precision head hit and speak once per new distinct victim: below the floor a
    /// 0.6 whisper (doubles another axis, never carries a tier alone — 6 of 42,342 honest archive
    /// sessions hold one), at the floor the "silent-far-hs" edge (0 honest sessions hold two).
    /// </summary>
    private Signal? NoteFarHit(int attackerSlot, int victimSlot, string weapon, float err, float distU, float now)
    {
        var list = _farHits.TryGetValue(attackerSlot, out var l) ? l : _farHits[attackerSlot] = new();
        list.Add((now, victimSlot, weapon, err, distU));
        int distinct = list.Select(h => h.VictimSlot).Distinct().Count();
        if (distinct <= _farFiredAt.GetValueOrDefault(attackerSlot)) return null; // already spoke at this size
        _farFiredAt[attackerSlot] = distinct;

        if (distinct < _farMinVictims)
            return new Signal(
                Id, attackerSlot, now, 0.6f,
                $"head hit at {distU:F0}u with the view {err:F1}° off every recent victim position ({weapon}) — " +
                $"precision weapons land within 2° of the head at this range (honest 99.9th pct 3.6°; " +
                $"one such hit in 0.014% of sessions)");

        string detail = string.Join(", ", list.Select(h => $"{h.Weapon} {h.Err:F1}° off at {h.DistU:F0}u"));
        return new Signal(
            Id, attackerSlot, now, 1f,
            $"{distinct} head hits at ≥{FarMinRangeUnits:F0}u on {distinct} different enemies with the view " +
            $"≥{_farOffDeg:F0}° off every recent victim position ({detail}) — bullets land where the view " +
            $"is not (0 of 42,342 honest archive sessions)",
            Edge: "silent-far-hs");
    }
}
