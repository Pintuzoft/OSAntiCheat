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
/// </summary>
public sealed class KillBurstDetector : IDetector
{
    public string Id => "wallhack.killburst";
    public float Weight => 1.6f;
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private readonly int _minKills;        // distinct never-seen HS victims inside the window to fire
    private readonly float _windowSeconds; // rolling window the burst is evaluated over

    // Whole-map sight memory: (observer, enemy) pairs that have EVER been spotted. Fed by the
    // plugin's poll; a pair present here permanently disqualifies that victim for this attacker.
    private readonly HashSet<(int Observer, int Enemy)> _everSeen = new();
    // Rolling blind-HS kills per attacker. Victim slot kept for distinctness, name for evidence.
    private readonly Dictionary<int, List<(float Time, int VictimSlot, string VictimName)>> _kills = new();
    // Highest distinct-count already fired this episode, so a growing burst escalates once per new
    // victim instead of re-firing on every kill (the episode ends when the window drains empty).
    private readonly Dictionary<int, int> _firedAt = new();

    public KillBurstDetector(int minKills = 4, float windowSeconds = 15f)
    {
        _minKills = Math.Max(2, minKills);
        _windowSeconds = windowSeconds;
    }

    public void Remove(int slot)
    {
        _kills.Remove(slot);
        _firedAt.Remove(slot);
        // Both directions: a new player in this slot has seen nobody, and nobody has seen them.
        _everSeen.RemoveWhere(p => p.Observer == slot || p.Enemy == slot);
    }

    public void Reset()
    {
        _kills.Clear();
        _firedAt.Clear();
        _everSeen.Clear();
    }

    /// <summary>The observer currently has this enemy in their spotted mask — record it forever
    /// (this map). Called from the plugin's 20 Hz poll, matching the offline validation cadence.</summary>
    public void NoteSeen(int observer, int enemy) => _everSeen.Add((observer, enemy));

    /// <summary>
    /// Called on every enemy bullet headshot kill. Counts only victims the attacker has never once
    /// seen; fires (LogicBreach, edge) when the rolling window holds ≥ minKills DISTINCT such victims.
    /// </summary>
    public Signal? OnKill(int attackerSlot, int victimSlot, string victimName, bool headshot, float now)
    {
        if (!headshot) return null;

        var list = _kills.TryGetValue(attackerSlot, out var l) ? l : _kills[attackerSlot] = new();
        list.RemoveAll(k => now - k.Time > _windowSeconds);
        if (list.Count == 0) _firedAt.Remove(attackerSlot); // window drained — episode over

        // A victim the attacker has legitimately seen at any point this map carries no information
        // claim — the kill neither counts nor breaks the burst (C8's one sighted kill sat mid-ace).
        if (_everSeen.Contains((attackerSlot, victimSlot))) return null;

        list.Add((now, victimSlot, victimName));

        var distinctSlots = new HashSet<int>();
        foreach (var k in list) distinctSlots.Add(k.VictimSlot);
        int distinct = distinctSlots.Count;
        if (distinct < _minKills) return null;
        if (distinct <= _firedAt.GetValueOrDefault(attackerSlot)) return null; // already fired at this size
        _firedAt[attackerSlot] = distinct;

        float span = now - list[0].Time;
        var names = new List<string>();
        foreach (var k in list) if (!names.Contains(k.VictimName)) names.Add(k.VictimName);

        float confidence = Math.Clamp(0.9f + 0.05f * (distinct - _minKills), 0.9f, 1f);
        return new Signal(
            Id, attackerSlot, now, confidence,
            $"{distinct} headshot kills in {span:F1}s on {distinct} different enemies never once seen " +
            $"this map ({string.Join(", ", names)}) — pre-aimed information no human channel provides",
            Edge: "blind-hs-burst"); // ≥4 distinct: 2/321k archive kills, both confirmed cheaters (C5, C6)
    }
}
