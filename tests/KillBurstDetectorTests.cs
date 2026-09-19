using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Blind headshot burst (wallhack.killburst): ≥4 HS kills inside 15 s on DISTINCT enemies the
/// killer never once saw this map. Fixtures mirror the measured cases: the C8 ace (2026-08-07
/// seabase — six scout HS in 12.1 s, one victim legitimately sighted mid-ace) and the archive's
/// honest tail (pistol-round triples, which must stay silent at the default floor of 4).
/// </summary>
public sealed class KillBurstDetectorTests
{
    [Fact]
    public void C8_ace_fires_on_fourth_distinct_blind_hs_with_edge()
    {
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        // Live tick spacing of the real ace (ticks 32867→33172 at 64/s).
        Assert.Null(d.OnKill(1, 10, "V1", headshot: true, now: 513.5f));
        var w2 = d.OnKill(1, 11, "V2", headshot: true, now: 514.8f);
        Assert.NotNull(w2);                 // early warning from the 2nd distinct victim...
        Assert.Null(w2!.Value.Edge);        // ...but edge-less: fusion only, never auto-action
        var w3 = d.OnKill(1, 12, "V3", headshot: true, now: 516.3f);
        Assert.NotNull(w3);
        Assert.Null(w3!.Value.Edge);
        Assert.True(w2.Value.Confidence < w3.Value.Confidence);
        var s = d.OnKill(1, 13, "V4", headshot: true, now: 518.3f);
        Assert.NotNull(s);
        Assert.Equal("wallhack.killburst", s!.Value.Detector);
        Assert.Equal("blind-hs-burst", s.Value.Edge);
        Assert.Equal(DetectorKind.LogicBreach, d.Kind);
        Assert.Contains("4 headshot kills", s.Value.Reason);
        Assert.Contains("V4", s.Value.Reason);
    }

    [Fact]
    public void Sighted_victim_neither_counts_nor_breaks_the_burst()
    {
        // C8's fifth kill was on a victim he had just seen — the ace must still fire on
        // the remaining blind kills, and the sighted one must not inflate the count.
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        d.NoteSeen(observer: 1, enemy: 14, now: 0f);
        Assert.Null(d.OnKill(1, 10, "A", headshot: true, now: 0f));
        Assert.Null(d.OnKill(1, 14, "Seen", headshot: true, now: 1f));  // sighted: no count, no break
        d.OnKill(1, 11, "B", headshot: true, now: 2f);                  // early warnings (edge-less)
        d.OnKill(1, 12, "C", headshot: true, now: 3f);
        var s = d.OnKill(1, 13, "D", headshot: true, now: 4f);
        Assert.NotNull(s);
        Assert.Equal("blind-hs-burst", s!.Value.Edge);
        Assert.DoesNotContain("Seen", s.Value.Reason);
    }

    [Fact]
    public void Pistol_round_triple_stays_silent_and_window_prunes()
    {
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        // The archive's honest maximum: three blind HS in a pistol-round opening — early warnings
        // are allowed (fusion suspicion), the EDGE must never fire.
        Assert.Null(d.OnKill(2, 10, "A", headshot: true, now: 10f));
        Assert.Null(d.OnKill(2, 11, "B", headshot: true, now: 12f)?.Edge);
        Assert.Null(d.OnKill(2, 12, "C", headshot: true, now: 14f)?.Edge);
        // A fourth blind HS OUTSIDE the window must not complete the burst (window drained →
        // count restarts at 1 → not even an early warning).
        Assert.Null(d.OnKill(2, 13, "D", headshot: true, now: 40f));
    }

    [Fact]
    public void Stale_sighting_counts_as_blind_from_three_victims_but_never_for_two()
    {
        // C10 (2026-09-18 blackgold, r3): three headshots in 8.5 s on victims last seen 64 s, 147 s
        // and never before. Under the strict rule only the third counted; a sighting older than
        // 30 s is stale and the burst is three. But two stale-sighted victims alone are no warning:
        // the 2-victim tier keeps the strict "never seen" grade (six times noisier otherwise).
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f, blindAfterSeconds: 30f);
        d.NoteSeen(observer: 1, enemy: 10, now: 100f);
        d.NoteSeen(observer: 1, enemy: 11, now: 20f);
        Assert.Null(d.OnKill(1, 10, "A", headshot: true, now: 164f));   // seen 64 s ago: blind, 1 victim
        Assert.Null(d.OnKill(1, 11, "B", headshot: true, now: 170f));    // seen 150 s ago: blind, but 2 stale ≠ warning
        var s3 = d.OnKill(1, 12, "C", headshot: true, now: 172.5f); // never seen: three blind victims
        Assert.NotNull(s3);
        Assert.Null(s3!.Value.Edge);
        Assert.Equal(0.65f, s3.Value.Confidence, 2);
        Assert.Contains("3 headshot kills", s3.Value.Reason);
        Assert.Contains("not seen for 30s", s3.Value.Reason);
        Assert.Contains("A", s3.Value.Reason);
    }

    [Fact]
    public void Fresh_sighting_still_neither_counts_nor_breaks()
    {
        // The peek kill: the victim became spotted at the kill tick (C10's second pistol-round
        // headshot, sighted 0.0 s before). A fresh sighting is not blind at any tier.
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f, blindAfterSeconds: 30f);
        Assert.Null(d.OnKill(1, 10, "A", headshot: true, now: 30f));      // never seen
        d.NoteSeen(observer: 1, enemy: 11, now: 31.3f);
        Assert.Null(d.OnKill(1, 11, "Seen", headshot: true, now: 31.3f)); // just sighted: no count, no break
        var s2 = d.OnKill(1, 12, "B", headshot: true, now: 36f);          // second never-seen: warning
        Assert.NotNull(s2);
        Assert.Equal(0.4f, s2!.Value.Confidence, 2);
        Assert.Contains("never once seen", s2.Value.Reason);
        Assert.DoesNotContain("Seen", s2.Value.Reason);
    }

    [Fact]
    public void Repeat_kills_on_same_victim_do_not_reach_distinct_floor()
    {
        // Respawn modes: farming one never-seen player is a rate artifact, not information.
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        Assert.Null(d.OnKill(3, 10, "A", headshot: true, now: 0f));
        Assert.Null(d.OnKill(3, 10, "A", headshot: true, now: 1f));
        Assert.Null(d.OnKill(3, 10, "A", headshot: true, now: 2f));
        Assert.Null(d.OnKill(3, 10, "A", headshot: true, now: 3f));
        Assert.Null(d.OnKill(3, 11, "B", headshot: true, now: 4f)?.Edge); // 2 distinct: warning at most
    }

    [Fact]
    public void Growing_burst_escalates_once_per_new_victim_not_per_kill()
    {
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        d.OnKill(1, 10, "A", headshot: true, now: 0f);
        d.OnKill(1, 11, "B", headshot: true, now: 1f);
        d.OnKill(1, 12, "C", headshot: true, now: 2f);
        var s4 = d.OnKill(1, 13, "D", headshot: true, now: 3f);
        Assert.Equal("blind-hs-burst", s4!.Value.Edge);                // 4 distinct → the edge fires
        Assert.Null(d.OnKill(1, 13, "D", headshot: true, now: 4f));    // same size → silent
        Assert.NotNull(d.OnKill(1, 14, "E", headshot: true, now: 5f)); // 5th distinct → escalates
    }

    [Fact]
    public void Non_headshots_never_count_and_reset_clears_sight_memory()
    {
        var d = new KillBurstDetector(minKills: 4, windowSeconds: 15f);
        Assert.Null(d.OnKill(1, 10, "A", headshot: false, now: 0f));
        Assert.Null(d.OnKill(1, 11, "B", headshot: false, now: 1f));
        Assert.Null(d.OnKill(1, 12, "C", headshot: false, now: 2f));
        Assert.Null(d.OnKill(1, 13, "D", headshot: false, now: 3f));

        // Map change: sight memory and burst state restart with the map.
        d.NoteSeen(observer: 5, enemy: 10, now: 99f);
        d.Reset();
        d.OnKill(5, 10, "A", headshot: true, now: 100f);
        d.OnKill(5, 11, "B", headshot: true, now: 101f);
        d.OnKill(5, 12, "C", headshot: true, now: 102f);
        var s2 = d.OnKill(5, 13, "D", headshot: true, now: 103f);
        Assert.Equal("blind-hs-burst", s2!.Value.Edge); // 10 is blind again post-reset
    }
}
