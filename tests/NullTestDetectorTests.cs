using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Exercises the null-test detector's McNemar z-logic. The plugin feeds, per poll, the DISCORDANT
/// tally over unspotted enemies: present-hit-not-past (b) vs past-hit-not-present (c). The signal
/// is the standardised asymmetry z = (b - c) / sqrt(b + c) — skill (concordant hits) cancels.
/// </summary>
public class NullTestDetectorTests
{
    [Fact]
    public void Flags_a_present_over_past_asymmetry()
    {
        // 25 present-only vs 5 past-only over 30 discordant → z = 20/sqrt(30) ≈ 3.65 ≥ 3.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f);

        var signal = d.Accumulate(slot: 1, now: 0f, nowOnly: 25, pastOnly: 5);

        Assert.NotNull(signal);
        Assert.Equal("wallhack.nulltest", signal!.Value.Detector);
    }

    [Fact]
    public void Ignores_a_symmetric_player_however_long_they_play()
    {
        // Present and past hit equally often — game sense, not a wallhack. z stays ≈ 0 forever.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f);

        Signal? fired = null;
        for (int i = 0; i < 2000; i++)
        {
            var s = d.Accumulate(1, i * 0.05f, nowOnly: 3, pastOnly: 3);
            if (s is not null) fired = s;
        }

        Assert.Null(fired);
    }

    [Fact]
    public void Does_not_fire_on_thin_evidence()
    {
        // A perfect asymmetry but only 20 discordant samples — below the observation floor.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f);

        Assert.Null(d.Accumulate(1, 0f, nowOnly: 10, pastOnly: 0));
        Assert.Null(d.Accumulate(1, 0f, nowOnly: 10, pastOnly: 0)); // 20 discordant, still < 30
    }

    [Fact]
    public void Map_artifact_inflating_everyone_is_suppressed_by_the_population_baseline()
    {
        // Night-map scenario (live 2026-07-30): spotted state is unreliable, so EVERY player's
        // present-bias inflates identically (rate 0.75, absolute z crosses 3 for all of them).
        // Round-robin feeding builds the peer baseline alongside each player's own counts; once
        // the rest-of-population evidence is in, nobody stands out from it → nobody emits.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f, minPopObservations: 200);

        Signal? fired = null;
        for (int round = 0; round < 30; round++)
            for (int slot = 0; slot < 10; slot++)
            {
                var s = d.Accumulate(slot, round * 0.05f, nowOnly: 3, pastOnly: 1);
                if (s is not null) fired = s;
            }

        Assert.Null(fired);
    }

    [Fact]
    public void A_cheater_standing_out_from_an_inflated_population_still_fires()
    {
        // Same inflated map, but one player's present-bias EXCEEDS the population's (rate 1.0
        // vs peers' 0.75). The two-proportion z sees the excess and the signal survives the gate.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f, minPopObservations: 200);

        Signal? cheaterSignal = null;
        for (int round = 0; round < 30; round++)
        {
            for (int slot = 1; slot < 10; slot++)
                Assert.Null(d.Accumulate(slot, round * 0.05f, nowOnly: 3, pastOnly: 1));
            var s = d.Accumulate(0, round * 0.05f, nowOnly: 10, pastOnly: 0);
            if (s is not null) cheaterSignal = s;
        }

        Assert.NotNull(cheaterSignal);
        Assert.Equal(0, cheaterSignal!.Value.PlayerSlot);
    }

    [Fact]
    public void Reset_clears_the_population_baseline_with_the_players()
    {
        // Build a big peer baseline, then Reset (map change). A lone asymmetric player on the
        // new map must fall back to the absolute test (thin population) and fire — the old
        // map's population must not gate the new map.
        var d = new NullTestDetector(minObservations: 30, minZ: 3f, minPopObservations: 200);
        for (int round = 0; round < 10; round++)
            for (int slot = 1; slot < 10; slot++)
                d.Accumulate(slot, round * 0.05f, nowOnly: 3, pastOnly: 1);

        d.Reset();

        Assert.NotNull(d.Accumulate(0, 0f, nowOnly: 25, pastOnly: 5));
    }

    [Fact]
    public void Escalates_once_per_z_band_rather_than_every_poll()
    {
        var d = new NullTestDetector(minObservations: 30, minZ: 3f);

        // First crossing into z≈3.65 (band 3) emits.
        Assert.NotNull(d.Accumulate(1, 0f, nowOnly: 25, pastOnly: 5));
        // More asymmetry pushes z to ≈4.2 (band 4) — a new band, so it emits again.
        Assert.NotNull(d.Accumulate(1, 0f, nowOnly: 5, pastOnly: 0));
        // Staying within band 4 does NOT re-emit (no exposure-driven score inflation).
        Assert.Null(d.Accumulate(1, 0f, nowOnly: 1, pastOnly: 0));
    }
}
