using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// movement.airgain is a LOGIC-BREACH movement axis: horizontal speed gained WHILE AIRBORNE,
/// sustained across a chain of jumps. Corpus (43 demos, 261 honest sessions with ≥4 chained
/// arcs): honest median gain max +14.3 u/s — downhill hop bursts live there and die by the
/// third hop; C9's bhop script held +67 median at over-sprint peaks. These tests model the
/// shapes on synthetic ticks: a scripted chain fires the edge, downhill bursts and single big
/// jumps stay silent, and a surf ride (one long airborne phase) is structurally invisible.
/// </summary>
public class MovementAirGainDetectorTests
{
    private const float TickDt = 1f / 64f;
    private int _seq = 10;

    private float FeedGround(PlayerTracker tr, int ticks, float speed)
    {
        float t = 0;
        for (int i = 0; i < ticks; i++)
        {
            int s = _seq++;
            t = s * TickDt;
            tr.Add(new TickSample(s, t, new Vector3(0, 0, 0), new ViewAngles(0, 0, 0),
                new Vector3(speed, 0, 0), OnGround: true, Alive: true));
        }
        return t;
    }

    /// <summary>One airborne arc: launches upward at <paramref name="launchSpeed"/>, horizontal
    /// speed ramps linearly to launch+gain, z follows a jump parabola of ~55 u.</summary>
    private float FeedHop(PlayerTracker tr, float launchSpeed, float gain, int airTicks = 40, float rise = 55f)
    {
        float t = 0;
        for (int i = 0; i < airTicks; i++)
        {
            int s = _seq++;
            t = s * TickDt;
            float frac = airTicks <= 1 ? 1f : i / (float)(airTicks - 1);
            float h = launchSpeed + gain * frac;
            float z = rise * (1f - MathF.Abs(2f * frac - 1f));          // up then down
            float vz = i == 0 ? 280f : (frac < 0.5f ? 150f : -150f);     // upward launch
            tr.Add(new TickSample(s, t, new Vector3(0, 0, z), new ViewAngles(0, 0, 0),
                new Vector3(h, 0, vz), OnGround: false, Alive: true));
        }
        return t;
    }

    private static Signal? Drain(MovementAirGainDetector d, PlayerTracker tr, float now)
        => d.OnPoll(tr, now);

    [Fact]
    public void Is_a_logic_breach_axis()
    {
        Assert.Equal(DetectorKind.LogicBreach, new MovementAirGainDetector().Kind);
    }

    [Fact]
    public void Scripted_chain_fires_the_edge()
    {
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 6; hop++)
        {
            t = FeedHop(tr, 300f, 80f);          // clamp-capped launch, +80 in air, peak 380
            t = FeedGround(tr, 4, 300f);         // frame-tight re-jump
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.NotNull(sig);
        Assert.Equal("movement.airgain", sig!.Value.Detector);
        Assert.Equal("airgain-chain", sig.Value.Edge);
        Assert.Contains("mid-air", sig.Value.Reason);
    }

    [Fact]
    public void Downhill_burst_with_modest_gains_stays_silent()
    {
        // The honest shape: a few chained hops, ~+14 u/s each, sprint-range speeds.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 8; hop++)
        {
            t = FeedHop(tr, 245f, 14f);
            t = FeedGround(tr, 6, 250f);
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Two_short_bursts_reach_the_edge()
    {
        // C9's live pattern: 3–4-hop bursts, big gains. Retro-chaining counts the burst starter,
        // so two 3-hop bursts inside the window = 6 arcs — the edge cannot be dodged by keeping
        // chains short.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(8192, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int burst = 0; burst < 2; burst++)
        {
            for (int hop = 0; hop < 3; hop++)
            {
                t = FeedHop(tr, 300f, 80f);
                t = FeedGround(tr, 4, 300f);
                sig = Drain(d, tr, t) ?? sig;
            }
            t = FeedGround(tr, 40, 280f);        // ~0.6 s run between bursts — chain broken, window not
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.NotNull(sig);
        Assert.Equal("airgain-chain", sig!.Value.Edge);
    }

    [Fact]
    public void One_short_burst_stays_silent()
    {
        // A single 4-hop burst = 4 arcs: below the five-arc window minimum (honest 4-arc windows
        // reach +33.5 median — one lucky downhill run — so four arcs alone prove nothing).
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(8192, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 4; hop++)
        {
            t = FeedHop(tr, 300f, 80f);
            t = FeedGround(tr, 4, 300f);
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Hop_spam_at_constant_speed_stays_silent()
    {
        // Jumping constantly is not the crime — gaining speed in the air is. A dozen chained
        // hops at sprint speed with no air gain must not even whisper.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 12; hop++)
        {
            t = FeedHop(tr, 250f, 2f);
            t = FeedGround(tr, 4, 250f);         // frame-tight chain, zero gain
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Unchained_big_jumps_never_build_evidence()
    {
        // Big single gains (an HE boost, a long-jump strafe) separated by normal running.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 8; hop++)
        {
            t = FeedHop(tr, 300f, 90f);
            t = FeedGround(tr, 60, 250f);        // ~1 s on the ground — chain broken
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Surf_ride_is_structurally_invisible()
    {
        // One jump onto the ramp, then seconds airborne gaining huge speed: fails the arc caps.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int ride = 0; ride < 6; ride++)
        {
            t = FeedHop(tr, 250f, 150f, airTicks: 200, rise: 300f);   // 3.1 s airborne, 300 u drop-shape
            t = FeedGround(tr, 4, 400f);
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Sub_sprint_chain_whispers_without_the_edge()
    {
        // Median gain over the whisper bar but peaks under 300 u/s: fusion food, no auto-action.
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 240f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 6; hop++)
        {
            t = FeedHop(tr, 240f, 30f);          // +30 median, peak 270 < 300
            t = FeedGround(tr, 4, 240f);
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.NotNull(sig);
        Assert.Null(sig!.Value.Edge);
        Assert.InRange(sig.Value.Confidence, 0.4f, 0.85f);
    }

    [Fact]
    public void Teleport_speeds_void_the_arc()
    {
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 250f);
        Signal? sig = null;
        float t = 0;
        for (int hop = 0; hop < 8; hop++)
        {
            t = FeedHop(tr, 300f, 600f);         // respawn/teleport spike mid-"arc"
            t = FeedGround(tr, 4, 300f);
            sig = Drain(d, tr, t) ?? sig;
        }
        Assert.Null(sig);
    }

    [Fact]
    public void Death_voids_the_arc_but_not_completed_evidence()
    {
        var d = new MovementAirGainDetector();
        var tr = new PlayerTracker(4096, slot: 1);
        FeedGround(tr, 20, 300f);
        float t = 0;
        for (int hop = 0; hop < 3; hop++)
        {
            t = FeedHop(tr, 300f, 80f);
            t = FeedGround(tr, 4, 300f);
        }
        // dies mid-hop: this arc must not count
        for (int i = 0; i < 10; i++)
        {
            int s = _seq++;
            t = s * TickDt;
            tr.Add(new TickSample(s, t, Vector3.Zero, new ViewAngles(0, 0, 0),
                new Vector3(350f, 0, 100f), OnGround: false, Alive: false));
        }
        Assert.Null(Drain(d, tr, t));
    }
}
