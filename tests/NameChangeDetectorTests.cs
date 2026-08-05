using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Nick-changer churn: the gate (3 renames / 20 s by default) is RATE-based — the demo-measured
/// cheat marquee ran ~1.3 renames/s (614 in 8.5 min) and crosses it in ~2 s, while honest
/// mid-match renames are isolated events that never cluster three inside 20 s. Only the churn
/// signal carries the name-churn edge.
/// </summary>
public sealed class NameChangeDetectorTests
{
    [Fact]
    public void Marquee_rate_fires_within_seconds_with_edge()
    {
        var d = new NameChangeDetector(minChanges: 3, windowSeconds: 20f);
        Assert.Null(d.OnNameChange(slot: 4, now: 10.0f));
        Assert.Null(d.OnNameChange(slot: 4, now: 10.8f));  // ~1.3/s, the measured cheat cadence
        var s = d.OnNameChange(slot: 4, now: 11.5f);
        Assert.NotNull(s);
        Assert.Equal("namechanger", s!.Value.Detector);
        Assert.Equal("name-churn", s.Value.Edge);
        Assert.Equal(DetectorKind.Behavioural, d.Kind);    // improbable, not beyond-human
    }

    [Fact]
    public void Isolated_manual_renames_never_fire()
    {
        // The owner's own population renames mid-match sometimes — as isolated events.
        var d = new NameChangeDetector(minChanges: 3, windowSeconds: 20f);
        Assert.Null(d.OnNameChange(slot: 4, now: 0f));
        Assert.Null(d.OnNameChange(slot: 4, now: 60f));    // a minute later — window long empty
        Assert.Null(d.OnNameChange(slot: 4, now: 300f));   // and again: three renames, no cluster
    }

    [Fact]
    public void Cooldown_yields_one_signal_per_episode_and_slots_are_independent()
    {
        var d = new NameChangeDetector(minChanges: 3, windowSeconds: 20f);
        d.OnNameChange(slot: 4, now: 0f);
        d.OnNameChange(slot: 4, now: 1f);
        Assert.NotNull(d.OnNameChange(slot: 4, now: 2f));
        Assert.Null(d.OnNameChange(slot: 4, now: 3f));     // inside the 30 s cooldown
        d.OnNameChange(slot: 4, now: 31f);                 // marquee keeps churning...
        d.OnNameChange(slot: 4, now: 32f);
        Assert.NotNull(d.OnNameChange(slot: 4, now: 33f)); // ...cooldown over → next episode fires
        Assert.Null(d.OnNameChange(slot: 9, now: 34f));    // another player's first rename: clean
    }
}
