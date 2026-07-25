using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Exercises the wallhack tracking state machine, including the live-data fix: the aim must
/// FOLLOW the enemy's bearing, so a held angle an enemy merely crosses does not flag.
/// </summary>
public class WallhackDetectorTests
{
    private const float PollDt = 0.05f;

    // Enemy walks laterally (bearing changes); the view either follows it or is held static.
    private static Signal? Run(WallhackDetector d, int polls, float yStep, bool viewFollows)
    {
        Signal? fired = null;
        float t = 0f, y = 0f;
        for (int i = 0; i < polls; i++)
        {
            float bearing = MathF.Atan2(y, 500f) * (180f / MathF.PI);
            float view = viewFollows ? bearing : 0f;
            var target = new WallhackDetector.WallTarget(
                EnemyId: 2, new Vector3(500f, y, 0f), AimErrDeg: 1f, ViewYaw: view, EnemyBearingYaw: bearing);
            var s = d.Observe(observerSlot: 1, t, target);
            if (s is not null) fired = s;
            y += yStep;
            t += PollDt;
        }
        return fired;
    }

    [Fact]
    public void Flags_when_aim_follows_a_moving_unspotted_enemy()
    {
        // Enemy sweeps ~37 deg of bearing in 1s and the view follows it.
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        var signal = Run(d, polls: 20, yStep: 20f, viewFollows: true);

        Assert.NotNull(signal);
        Assert.Equal("wallhack.track", signal!.Value.Detector);
    }

    [Fact]
    public void Ignores_a_held_angle_an_enemy_crosses()
    {
        // The live-data false positive: static crosshair, enemy walks past behind the wall.
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        var signal = Run(d, polls: 20, yStep: 20f, viewFollows: false);

        Assert.Null(signal);
    }

    [Fact]
    public void Ignores_a_tiny_bearing_arc()
    {
        // ~7 deg of bearing is crosshair micro-jitter territory, not tracking.
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        Assert.Null(Run(d, polls: 20, yStep: 3f, viewFollows: true));
    }

    [Fact]
    public void Ignores_a_slow_drift_over_many_seconds()
    {
        // Sweeps a big arc, but over 20s => ~2 deg/s. A drift, not a traverse (the 49s case).
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        Assert.Null(Run(d, polls: 400, yStep: 1f, viewFollows: true));
    }

    [Fact]
    public void Ignores_a_static_unspotted_enemy()
    {
        // Enemy doesn't move => no bearing to follow.
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        Assert.Null(Run(d, polls: 20, yStep: 0f, viewFollows: true));
    }

    [Fact]
    public void Ignores_a_momentary_sweep()
    {
        var d = new WallhackDetector(minTrackSeconds: 0.6f, minEnemyMoveUnits: 60f);

        Assert.Null(Run(d, polls: 2, yStep: 20f, viewFollows: true));
    }

    [Fact]
    public void No_target_never_flags()
    {
        var d = new WallhackDetector();

        Signal? last = null;
        for (int i = 0; i < 20; i++)
            last = d.Observe(1, i * PollDt, target: null);

        Assert.Null(last);
    }
}
