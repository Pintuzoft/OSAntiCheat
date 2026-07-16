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
