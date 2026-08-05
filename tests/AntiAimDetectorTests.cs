using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// antiaim is a LOGIC-BREACH axis on the player's OWN angles: pitch past the engine's ±89° clamp
/// (impossible for a real client — the measured honest population parks at exactly 89.00 and never
/// beyond), or sustained sign-alternating yaw jitter (honest maximum measured: ONE alternation; a
/// flick and a spinbot are both monotonic). These tests model each case on synthetic ticks.
/// </summary>
public class AntiAimDetectorTests
{
    private const float TickDt = 1f / 64f;
    private int _seq = 10;

    private float Feed(PlayerTracker tr, float pitch, float yaw, bool alive = true)
    {
        int s = _seq++;
        tr.Add(new TickSample(s, s * TickDt, Vector3.Zero, new ViewAngles(pitch, yaw, 0f), Vector3.Zero, true, alive));
        return s * TickDt;
    }

    [Fact]
    public void Is_a_logic_breach_axis()
    {
        Assert.Equal(DetectorKind.LogicBreach, new AntiAimDetector().Kind);
    }

    [Fact]
    public void Pitch_past_the_clamp_fires_after_three_ticks()
    {
        var d = new AntiAimDetector(pitchDeg: 89.5f);
        var tr = new PlayerTracker(64, slot: 1);
        Feed(tr, 90f, 0f);
        Feed(tr, 92f, 0f);
        float t = Feed(tr, 91f, 0f);
        var s = d.OnPoll(tr, t);
        Assert.NotNull(s);
        Assert.Equal("antiaim", s!.Value.Detector);
        Assert.Contains("pitch", s.Value.Reason);
        Assert.Equal("fake-pitch", s.Value.Edge); // past-the-clamp is an auto-action edge
    }

    [Fact]
    public void Pitch_at_the_clamp_exactly_is_honest()
    {
        // The clamp value itself (89.00) is what an honest client parks at when looking straight down.
        var d = new AntiAimDetector(pitchDeg: 89.5f);
        var tr = new PlayerTracker(64, slot: 1);
        for (int i = 0; i < 20; i++) Feed(tr, 89.0f, 0f);
        Assert.Null(d.OnPoll(tr, _seq * TickDt));
    }

    [Fact]
    public void Alternating_yaw_jitter_fires()
    {
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(64, slot: 1);
        float t = 0f;
        for (int i = 0; i < 10; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);   // ±120° every tick
        var s = d.OnPoll(tr, t);
        Assert.NotNull(s);
        Assert.Contains("jitter", s!.Value.Reason);
        Assert.Null(s.Value.Edge); // jitter fuses but is NOT an auto-action edge (corroboration only)
    }

    [Fact]
    public void Monotonic_spin_is_not_jitter()
    {
        // A spinbot rotates one way — that is the spinbot detector's axis, not this one.
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(128, slot: 1);
        float yaw = 0f;
        float t = 0f;
        for (int i = 0; i < 60; i++) { yaw += 60f; t = Feed(tr, 0f, yaw); }
        Assert.Null(d.OnPoll(tr, t));
    }

    [Fact]
    public void A_single_flick_back_and_forth_is_human()
    {
        // One alternation = the measured honest maximum. Must stay silent.
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(64, slot: 1);
        Feed(tr, 0f, 0f);
        Feed(tr, 0f, 70f);     // flick out
        Feed(tr, 0f, 0f);      // flick back (alternation #1)
        float t = Feed(tr, 0f, 1f);
        Assert.Null(d.OnPoll(tr, t));
    }

    [Fact]
    public void Incremental_polling_processes_each_tick_once()
    {
        // Ten alternating ticks fed across two polls must fire exactly like one poll would —
        // and a third poll with no new ticks must not re-fire from stale data.
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(64, slot: 1);
        float t = 0f;
        for (int i = 0; i < 4; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.Null(d.OnPoll(tr, t));                      // run building, below the gate
        for (int i = 4; i < 12; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.NotNull(d.OnPoll(tr, t));                   // crosses the gate on the fresh ticks
        Assert.Null(d.OnPoll(tr, t));                      // nothing new -> no duplicate
    }

    [Fact]
    public void A_tick_gap_breaks_the_runs()
    {
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(64, slot: 1);
        float t = 0f;
        for (int i = 0; i < 5; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        _seq += 3;                                          // dropped ticks (lag/hibernation)
        for (int i = 5; i < 9; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.Null(d.OnPoll(tr, t));                       // neither fragment reaches 6 alone
    }

    [Fact]
    public void Cooldown_limits_one_signal_per_episode()
    {
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(256, slot: 1);
        float t = 0f;
        for (int i = 0; i < 10; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.NotNull(d.OnPoll(tr, t));
        for (int i = 10; i < 20; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.Null(d.OnPoll(tr, t));                       // still inside the cooldown window
    }

    [Fact]
    public void Remove_clears_state()
    {
        var d = new AntiAimDetector(jitterDeg: 45f, jitterFlips: 6);
        var tr = new PlayerTracker(64, slot: 1);
        float t = 0f;
        for (int i = 0; i < 10; i++) t = Feed(tr, 0f, i % 2 == 0 ? 60f : -60f);
        Assert.NotNull(d.OnPoll(tr, t));
        d.Remove(1);
        // Fresh state: the already-buffered ticks are unseen again, but the cooldown is also gone;
        // it fires anew — proving state (incl. cooldown) was dropped.
        Assert.NotNull(d.OnPoll(tr, t));
    }
}
