using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Exercises the detection logic against synthetic tick data — the only way to observe
/// detector behaviour without a live CS2 server. Each test builds a buffer that models a
/// specific scenario (a spin, a snap-onto-enemy, a lag jump, a fast trigger, a hold) and
/// asserts the detector fires or stays silent as designed.
/// </summary>
public class DetectorTests
{
    private const float TickDt = 1f / 64f;

    private static TickSample Sample(int seq, float time, Vector3 origin, ViewAngles angles) =>
        new(seq, time, origin, angles, Vector3.Zero, OnGround: true, Alive: true);

    // ---- Spinbot ----------------------------------------------------------

    /// <summary>Feed <paramref name="ticks"/> consecutive samples, turning <paramref name="yawPerTick"/> each.</summary>
    private static void Spin(PlayerTracker t, ref int seq, ref float time, ref float yaw, int ticks, float yawPerTick)
    {
        for (int k = 0; k < ticks; k++, seq++)
        {
            t.Add(Sample(seq, time, Vector3.Zero, new ViewAngles(0f, yaw % 360f, 0f)));
            time += TickDt;
            yaw += yawPerTick;
        }
    }

    [Fact]
    public void Spinbot_flags_a_spin_that_keeps_going()
    {
        // ~2000°/s, unbroken, polled every 0.2 s (13 ticks ≈ 406°) as the buffer fills. The first
        // stretch past two turns is credited silently (once is a burst); the NEXT two turns on top of
        // it fire, and every further two turns after that — a spinbot never stops serving.
        var t = new PlayerTracker(128, slot: 3);
        var d = new SpinbotDetector();
        float time = 0f, yaw = 0f; int seq = 0;
        Signal? first = null; int firstPoll = 0, fired = 0;
        for (int poll = 1; poll <= 9; poll++)
        {
            Spin(t, ref seq, ref time, ref yaw, 13, 2000f * TickDt);
            if (d.Inspect(t) is { } s) { fired++; if (first is null) { first = s; firstPoll = poll; } }
        }
        Assert.NotNull(first);
        Assert.Equal("spinbot", first!.Value.Detector);
        Assert.Null(first.Value.Edge); // poll-spin fuses but is NOT an auto-action edge
        Assert.Equal(DetectorKind.LogicBreach, d.Kind);
        Assert.Equal(4, firstPoll);     // 812° at poll 2 (silent), 812° more at poll 4 (fires) — 0.8 s in
        Assert.True(fired >= 3);        // and keeps firing on every fresh two turns
    }

    [Fact]
    public void Spinbot_ignores_a_single_multi_turn_burst_that_stops()
    {
        // The live false positive (2026-09-10): a hand trying a new mouse driver threw 752° of
        // same-direction rotation at spin rate in one swipe, then held still. The old poll re-read the
        // same stretch out of the 2 s buffer nine times (fusion made a Review of the first two). A
        // stretch that stops is a burst; only a stretch that keeps going is a spin.
        var t = new PlayerTracker(128, slot: 3);
        var d = new SpinbotDetector();
        float time = 0f, yaw = 0f; int seq = 0;
        Spin(t, ref seq, ref time, ref yaw, 9, 94f);   // 8 deltas × 94° = 752° (~6000°/s), 2.1 turns
        int signals = 0;
        for (int poll = 0; poll < 9; poll++)
        {
            Spin(t, ref seq, ref time, ref yaw, 13, 0f); // still
            if (d.Inspect(t) is not null) signals++;
        }
        Assert.Equal(0, signals);
    }

    [Fact]
    public void Spinbot_does_not_pair_two_bursts_seconds_apart()
    {
        // Two driver bursts three seconds apart are two flukes, not a sustained spin: the pair window
        // (2 s, the buffer's span) has lapsed, so the second is credited as a first again.
        var t = new PlayerTracker(128, slot: 3);
        var d = new SpinbotDetector();
        float time = 0f, yaw = 0f; int seq = 0;
        int signals = 0;
        Spin(t, ref seq, ref time, ref yaw, 9, 94f);
        for (int poll = 0; poll < 15; poll++) { Spin(t, ref seq, ref time, ref yaw, 13, 0f); if (d.Inspect(t) is not null) signals++; } // ~3 s still
        Spin(t, ref seq, ref time, ref yaw, 9, 94f);
        for (int poll = 0; poll < 5; poll++) { Spin(t, ref seq, ref time, ref yaw, 13, 0f); if (d.Inspect(t) is not null) signals++; }
        Assert.Equal(0, signals);
    }

    [Fact]
    public void Spinbot_ignores_a_fast_flick_that_stops()
    {
        // A ~250° flick over 5 ticks (fast!) then STOPS — under two full turns, never fires.
        var t = new PlayerTracker(64, slot: 3);
        float time = 0f, yaw = 0f;
        for (int seq = 0; seq < 5; seq++)
        {
            t.Add(Sample(seq, time, Vector3.Zero, new ViewAngles(0f, yaw, 0f)));
            time += TickDt; yaw += 50f; // ~3200°/s but only 250° total
        }
        for (int seq = 5; seq < 25; seq++) // then holds still
        {
            t.Add(Sample(seq, time, Vector3.Zero, new ViewAngles(0f, yaw, 0f)));
            time += TickDt;
        }
        Assert.Null(new SpinbotDetector().Inspect(t));
    }

    [Fact]
    public void Spinbot_OnKill_flags_repeated_headshots_mid_spin()
    {
        // Continuously spinning ~2000°/s (>360° in the recent buffer, still spinning at the kill).
        static PlayerTracker Spinning()
        {
            var t = new PlayerTracker(64, slot: 3);
            float time = 0f, yaw = 0f;
            for (int seq = 0; seq < 30; seq++)
            {
                t.Add(Sample(seq, time, Vector3.Zero, new ViewAngles(0f, yaw % 360f, 0f)));
                time += TickDt; yaw += 2000f * TickDt;
            }
            return t;
        }

        var d = new SpinbotDetector(minSpinHsKills: 2);
        Assert.Null(d.OnKill(Spinning(), headshot: true, now: 1f));   // 1st HS mid-spin — a fluke, silent
        var signal = d.OnKill(Spinning(), headshot: true, now: 2f);  // 2nd — beyond human, fires
        Assert.NotNull(signal);
        Assert.Equal("spinbot", signal!.Value.Detector);
        Assert.Equal("spin-hs-kill", signal.Value.Edge); // the kill-anchored conjunction IS the auto-action edge
    }

    [Fact]
    public void Spinbot_OnKill_ignores_headshot_that_is_not_a_spin()
    {
        // A normal aimed headshot (no rotation) never counts, however many times.
        var still = new PlayerTracker(64, slot: 3);
        for (int seq = 0; seq < 30; seq++)
            still.Add(Sample(seq, seq * TickDt, Vector3.Zero, new ViewAngles(0f, 10f, 0f)));
        var d = new SpinbotDetector(minSpinHsKills: 2);
        Assert.Null(d.OnKill(still, headshot: true, now: 1f));
        Assert.Null(d.OnKill(still, headshot: true, now: 2f));
    }

    [Fact]
    public void Spinbot_ignores_normal_aiming()
    {
        var t = new PlayerTracker(64, slot: 3);
        float time = 0f, yaw = 0f;
        for (int seq = 0; seq < 20; seq++)
        {
            t.Add(Sample(seq, time, Vector3.Zero, new ViewAngles(0f, yaw, 0f)));
            time += TickDt;
            yaw += 3f; // ~192 deg/s — brisk but human
        }

        Assert.Null(new SpinbotDetector().Inspect(t));
    }

    // ---- Aimbot snap ------------------------------------------------------

    private static (PlayerTracker shooter, PlayerTracker enemy) BuildSnap(
        Vector3 enemyFeet, float baseTime, float yawBefore = -90f)
    {
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // Distinct, monotonic sequence numbers per snap so each buffer looks like a fresh shot.
        int seqBase = (int)MathF.Round(baseTime * 64f);
        float time = baseTime;
        // Looking 90° away for a few ticks, then snapped onto yaw 0 at the fire tick.
        for (int i = 0; i < 5; i++)
        {
            float yaw = i < 4 ? yawBefore : 0f;
            shooter.Add(Sample(seqBase + i, time, Vector3.Zero, new ViewAngles(0f, yaw, 0f)));
            enemy.Add(Sample(seqBase + i, time, enemyFeet, new ViewAngles(0f, 180f, 0f)));
            time += TickDt;
        }
        return (shooter, enemy);
    }

    // Fires the same shot over and over so the detector accumulates a window. Each call builds a
    // fresh tracker, but the detector keys on the slot, so the window carries across them.
    private static Signal? FireRepeatedly(
        AimbotSweepDetector detector, Vector3 enemyFeet, int times, float yawBefore = -90f)
    {
        Signal? last = null;
        for (int k = 0; k < times; k++)
        {
            var (shooter, enemy) = BuildSnap(enemyFeet, baseTime: k * 2f, yawBefore: yawBefore);
            last = detector.OnFire(shooter, new[] { enemy }, now: k * 2f);
        }
        return last;
    }

    private static readonly Vector3 InFront = new(500f, 0f, 0f);   // dead ahead at yaw 0
    private static readonly Vector3 OffToTheSide = new(0f, 500f, 0f); // 90 deg away: shot hits nobody

    [Fact]
    public void Aimbot_stays_quiet_until_a_real_sample_exists()
    {
        // A ratio off a handful of shots is noise. Nineteen perfect mid-sweep hits still say
        // nothing; the detector accumulates in silence until the sample can carry a number.
        var detector = new AimbotSweepDetector();

        Assert.Null(FireRepeatedly(detector, InFront, times: 19));
    }

    [Fact]
    public void Aimbot_flags_landing_on_target_while_still_sweeping()
    {
        // The thing no hand does: open fire while the view is still travelling and be on a body
        // anyway. Once is luck. Twenty-five times out of twenty-five is something steering.
        var detector = new AimbotSweepDetector();

        var last = FireRepeatedly(detector, InFront, times: 25);

        Assert.NotNull(last);
        Assert.Equal("aimbot.sweep", last!.Value.Detector);
        Assert.Equal(1, last.Value.PlayerSlot);
        Assert.Equal(1f, last.Value.Confidence, 3);
    }

    [Fact]
    public void Aimbot_ignores_mid_sweep_shots_that_land_on_nobody()
    {
        // Firing mid-sweep is firing blind, and blind shots miss. That is the normal case and it
        // must never speak, however many of them there are.
        var detector = new AimbotSweepDetector();

        Assert.Null(FireRepeatedly(detector, OffToTheSide, times: 25));
    }

    [Fact]
    public void Aimbot_is_a_ratio_not_a_count()
    {
        // Play long enough and anyone accumulates mid-sweep hits, so counting them would only rank
        // playtime - the mistake that once put the server's most-played regulars at the top of a
        // "most suspicious" list. What matters is the share: 3 hits in 40 shots is 7.5%, below the
        // threshold, and stays quiet no matter that 3 > 20.
        var detector = new AimbotSweepDetector();

        FireRepeatedly(detector, InFront, times: 3);
        var last = FireRepeatedly(detector, OffToTheSide, times: 37);

        Assert.Null(last);
    }

    [Fact]
    public void Aimbot_ignores_a_settled_aim()
    {
        // A view that has stopped moving is an ordinary aimed shot - you found them, you settled,
        // you fired. Only the still-travelling shot is interesting, and 1 deg per tick is 64 deg/s,
        // under the 90 deg/s gate.
        var detector = new AimbotSweepDetector();

        Assert.Null(FireRepeatedly(detector, InFront, times: 25, yawBefore: -1f));
    }

    // ---- Triggerbot -------------------------------------------------------

    private static (PlayerTracker shooter, PlayerTracker enemy) BuildCrossing(int onTargetTicksBeforeFire, float baseTime)
    {
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);
        var enemyFeet = new Vector3(500f, 0f, 0f); // ahead; on-target aim is yaw 0

        const int total = 20;
        int seqBase = (int)MathF.Round(baseTime * 64f);
        float time = baseTime;
        for (int i = 0; i < total; i++)
        {
            int ticksBeforeFire = (total - 1) - i;
            // On target (yaw 0) only within the last `onTargetTicksBeforeFire` ticks; off (yaw -30) before.
            float yaw = ticksBeforeFire < onTargetTicksBeforeFire ? 0f : -30f;
            shooter.Add(Sample(seqBase + i, time, Vector3.Zero, new ViewAngles(0f, yaw, 0f)));
            enemy.Add(Sample(seqBase + i, time, enemyFeet, new ViewAngles(0f, 180f, 0f)));
            time += TickDt;
        }
        return (shooter, enemy);
    }

    [Fact]
    public void Triggerbot_stays_quiet_on_a_single_fast_crossing()
    {
        // One fast tap is a pre-aim artefact — the detector must not speak yet.
        var (shooter, enemy) = BuildCrossing(onTargetTicksBeforeFire: 1, baseTime: 0f);

        Assert.Null(new TriggerbotDetector().OnFire(shooter, new[] { enemy }, now: 0f));
    }

    [Fact]
    public void Triggerbot_flags_repeated_fast_crossings()
    {
        // The signal is repetition: fast shot-on-crossing again and again within the window.
        var detector = new TriggerbotDetector();
        Signal? last = null;
        for (int k = 0; k < 6; k++)
        {
            var (shooter, enemy) = BuildCrossing(onTargetTicksBeforeFire: 1, baseTime: k * 2f);
            last = detector.OnFire(shooter, new[] { enemy }, now: k * 2f);
        }

        Assert.NotNull(last);
        Assert.Equal("triggerbot", last!.Value.Detector);
    }

    [Fact]
    public void Triggerbot_ignores_repeated_shots_after_human_delay()
    {
        // On target for ~10 ticks (~156 ms) before firing — normal deliberate shots, even repeated.
        var detector = new TriggerbotDetector();
        Signal? last = null;
        for (int k = 0; k < 6; k++)
        {
            var (shooter, enemy) = BuildCrossing(onTargetTicksBeforeFire: 10, baseTime: k * 2f);
            last = detector.OnFire(shooter, new[] { enemy }, now: k * 2f);
        }

        Assert.Null(last);
    }

    [Fact]
    public void Triggerbot_ignores_a_pure_hold()
    {
        // On target the entire lookback — a held angle, reaction undefined, must not flag.
        var detector = new TriggerbotDetector();
        Signal? last = null;
        for (int k = 0; k < 6; k++)
        {
            var (shooter, enemy) = BuildCrossing(onTargetTicksBeforeFire: 100, baseTime: k * 2f);
            last = detector.OnFire(shooter, new[] { enemy }, now: k * 2f);
        }

        Assert.Null(last);
    }

    [Fact]
    public void Triggerbot_ignores_enemy_walking_into_a_static_aim()
    {
        // Shooter aim never moves; the enemy steps into the crosshair on the fire tick.
        // That's a hold, not a shooter-driven crossing — must not flag even repeated (the "0ms" FP).
        var detector = new TriggerbotDetector();
        Signal? last = null;

        for (int k = 0; k < 6; k++)
        {
            var shooter = new PlayerTracker(64, slot: 1);
            var enemy = new PlayerTracker(64, slot: 2);

            const int total = 20;
            int seqBase = k * 64;
            float time = k * 2f;
            for (int i = 0; i < total; i++)
            {
                int ticksBeforeFire = (total - 1) - i;
                shooter.Add(Sample(seqBase + i, time, Vector3.Zero, new ViewAngles(0f, 0f, 0f))); // static
                var enemyFeet = ticksBeforeFire == 0
                    ? new Vector3(500f, 0f, 0f)     // steps directly ahead into the aim
                    : new Vector3(500f, 400f, 0f);  // off to the side before that
                enemy.Add(Sample(seqBase + i, time, enemyFeet, new ViewAngles(0f, 180f, 0f)));
                time += TickDt;
            }
            last = detector.OnFire(shooter, new[] { enemy }, now: k * 2f);
        }

        Assert.Null(last);
    }
}
