using System.Linq;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Blind headshot burst — the wall+aim ace signature (first live case: C8, 2026-08-07 seabase:
/// six scout headshots in 12.1 s, running from spawn zoomed, five of six victims never once in
/// the killer's spotted mask; every hard axis missed it because each measures a different
/// archetype — deadaim wants a PARKED crosshair, snap wants a STEP onto the head, track wants
/// LATERAL bearing-following, and an intercept course toward the victim produces none of those).
///
/// The conjunction that survives is kill-anchored and information-theoretic: a HEADSHOT on an
/// enemy the killer has NEVER seen this map is startling once — and a BURST of them on DISTINCT
/// victims is beyond any legitimate information channel (sound places one enemy, a teammate
/// callout maybe two; nobody one-taps four different people they have never had eyes on inside
/// one window). Measured on 321,423 archive kills: bursts of ≥4 occur exactly twice — both
/// confirmed cheaters (C5 spin-silent, C6 psilent) — and never for an honest player. Bursts of
/// exactly 3 add three honest-looking pistol-round cases (round 1: the sight history is still
/// empty for everyone), which is why the default floor is 4, not 3.
///
/// "Never seen" is whole-map memory, same semantics as the offline validation: the plugin feeds
/// every (observer, enemy) spotted-mask hit at the 20 Hz wallhack poll via <see cref="NoteSeen"/>,
/// and the pair set resets only on map change (or when a slot is vacated). The detector itself
/// never reads game state — the plugin owns the wiring, this class owns the memory and the gate.
///
/// Below the edge sits an EARLY-WARNING tier (v0.9.93): 2 and 3 distinct blind-HS victims emit
/// edge-less signals (fusion + admin awareness only, never auto-action). Measured rates say why
/// the line is where it is: a single blind HS is routine (7,627 in the archive — prefire, sound,
/// callouts), 2-in-window happens in 1.8% of sessions (120/6,664 — real suspicion, not proof),
/// 3 is the honest maximum, 4 is machine zone.
///
/// "Blind" has two grades since v0.9.112 (C10, 2026-09-18 blackgold: three headshots in 8.5 s on
/// victims last seen 64 s, 147 s and never before — one "never", so the old rule counted one).
/// A sighting goes STALE: a victim the attacker has not had spotted for <c>blindAfterSeconds</c>
/// (30) is blind again — a minute-old glimpse does not place a moving enemy behind a wall. On
/// 42,342 archive attacker-sessions the stale rule at ≥3 distinct victims hits 19 sessions (2 of
/// them typed cheaters, 0.04% honest) and at ≥4 hits 3 — all three typed cheaters, still zero
/// honest — so the edge keeps its floor under the looser definition. The 2-victim early warning
/// keeps the strict "never seen" grade: at the stale grade it would fire in 1.7% of sessions
/// (710), six times its measured rate, for a whisper whose only job is to double another axis.
/// Per-KILL identification was measured and rejected:
/// a "tracked a moving unseen target tightly" rule (path ≥100u, run-up aim ≤3°) matches 796
/// honest kills — and ZERO of C5/C6's, whose silent aim never pointed the view at the victim at
/// all. The way a single kill happens does not separate; the repetition on distinct victims does.
/// </summary>
public sealed class KillBurstDetector : IDetector
{
    public string Id => "wallhack.killburst";
    public float Weight => 1.6f;
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private readonly int _minKills;          // distinct blind HS victims inside the window to fire the edge
    private readonly float _windowSeconds;   // rolling window the burst is evaluated over
    private readonly float _blindAfterSeconds; // a sighting older than this is stale: the victim is blind again
    private const int EarlyWarnFloor = 2;    // edge-less fusion signals start here (1 blind HS is routine)
    private const int StaleGradeFloor = 3;   // from here stale-sighted victims count; below, only never-seen

    // Whole-map sight memory: when the observer LAST had the enemy in their spotted mask. Fed by
    // the plugin's poll; a fresh entry disqualifies that victim for this attacker, a stale one
    // (older than blindAfterSeconds) does not, and a missing one is "never seen this map".
    private readonly Dictionary<(int Observer, int Enemy), float> _lastSeen = new();
    // Rolling blind-HS kills per attacker. Victim slot kept for distinctness, name for evidence,
    // NeverSeen for the strict grade the 2-victim warning demands.
    private readonly Dictionary<int, List<(float Time, int VictimSlot, string VictimName, bool NeverSeen)>> _kills = new();
    // Highest distinct-count already fired this episode, so a growing burst escalates once per new
    // victim instead of re-firing on every kill (the episode ends when the window drains empty).
    private readonly Dictionary<int, int> _firedAt = new();

    public KillBurstDetector(int minKills = 4, float windowSeconds = 15f, float blindAfterSeconds = 30f)
    {
        _minKills = Math.Max(2, minKills);
        _windowSeconds = windowSeconds;
        _blindAfterSeconds = blindAfterSeconds;
    }

    public void Remove(int slot)
    {
        _kills.Remove(slot);
        _firedAt.Remove(slot);
        // Both directions: a new player in this slot has seen nobody, and nobody has seen them.
        foreach (var pair in _lastSeen.Keys.Where(p => p.Observer == slot || p.Enemy == slot).ToList())
            _lastSeen.Remove(pair);
    }

    public void Reset()
    {
        _kills.Clear();
        _firedAt.Clear();
        _lastSeen.Clear();
    }

    /// <summary>The observer currently has this enemy in their spotted mask — record WHEN (this map).
    /// Called from the plugin's 20 Hz poll, matching the offline validation cadence.</summary>
    public void NoteSeen(int observer, int enemy, float now) => _lastSeen[(observer, enemy)] = now;

    /// <summary>
    /// Called on every enemy bullet headshot kill. Counts only victims the attacker is blind to —
    /// never seen this map, or not seen for <c>blindAfterSeconds</c> — and fires (LogicBreach, edge)
    /// when the rolling window holds ≥ minKills DISTINCT such victims. The 2-victim early warning
    /// demands the strict grade (both never seen); from 3 victims a stale sighting counts as blind.
    /// </summary>
    public Signal? OnKill(int attackerSlot, int victimSlot, string victimName, bool headshot, float now)
    {
        if (!headshot) return null;

        var list = _kills.TryGetValue(attackerSlot, out var l) ? l : _kills[attackerSlot] = new();
        list.RemoveAll(k => now - k.Time > _windowSeconds);
        if (list.Count == 0) _firedAt.Remove(attackerSlot); // window drained — episode over

        // A victim the attacker has legitimately seen RECENTLY carries no information claim — the
        // kill neither counts nor breaks the burst (C8's one sighted kill sat mid-ace). A sighting
        // older than the stale limit does not place a moving enemy behind a wall: blind again.
        bool neverSeen = !_lastSeen.TryGetValue((attackerSlot, victimSlot), out float seenAt);
        if (!neverSeen && now - seenAt < _blindAfterSeconds) return null;

        list.Add((now, victimSlot, victimName, neverSeen));

        var distinctSlots = new HashSet<int>();
        var neverSlots = new HashSet<int>();
        foreach (var k in list)
        {
            distinctSlots.Add(k.VictimSlot);
            if (k.NeverSeen) neverSlots.Add(k.VictimSlot);
        }
        // Burst size under the grade each tier demands: two victims must both be never-seen; at
        // three and up the stale grade applies to every victim in the window.
        int size = distinctSlots.Count >= StaleGradeFloor ? distinctSlots.Count
                 : neverSlots.Count >= EarlyWarnFloor ? neverSlots.Count : 0;
        if (size < EarlyWarnFloor) return null;
        if (size <= _firedAt.GetValueOrDefault(attackerSlot)) return null; // already fired at this size
        _firedAt[attackerSlot] = size;

        float span = now - list[0].Time;
        var names = new List<string>();
        foreach (var k in list)
            if ((size >= StaleGradeFloor || k.NeverSeen) && !names.Contains(k.VictimName)) names.Add(k.VictimName);
        string what = size >= StaleGradeFloor
            ? $"{size} headshot kills in {span:F1}s on {size} different enemies the killer had not seen for " +
              $"{_blindAfterSeconds:F0}s or ever this map ({string.Join(", ", names)})"
            : $"{size} headshot kills in {span:F1}s on {size} different enemies never once seen " +
              $"this map ({string.Join(", ", names)})";

        if (size < _minKills)
            // Early warning: suspicion for the fusion engine, never an action. 2 never-seen in a
            // window occurs in 1.8% of honest sessions (sound + callouts); 3 under the stale grade
            // in 0.04% (17 of 42,342 archive sessions) — a lead worth a human eye, still not proof.
            return new Signal(
                Id, attackerSlot, now, size == 2 ? 0.4f : 0.65f,
                what + (size == 2
                    ? " — early warning (honest sessions reach this ~1.8% of the time; 4 never)"
                    : " — rare (0.04% of honest archive sessions; 4 never)"));

        float confidence = Math.Clamp(0.9f + 0.05f * (size - _minKills), 0.9f, 1f);
        return new Signal(
            Id, attackerSlot, now, confidence,
            what + " — pre-aimed information no human channel provides",
            Edge: "blind-hs-burst"); // ≥4 distinct: 3/42,342 archive sessions, all three typed cheaters
    }
}
