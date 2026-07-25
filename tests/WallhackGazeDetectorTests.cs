using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Exercises the smart gaze-follow wallhack detector. The plugin supplies, per poll, the
/// unspotted enemy the observer's gaze is nearest to; these tests feed synthetic poll streams.
/// </summary>
public class WallhackGazeDetectorTests
{
    private const float PollDt = 0.05f;

    // View yaw sweeps in step with the enemy's bearing (following it through a wall).
    private static Signal? RunFollow(
        WallhackGazeDetector d, int slot, int polls, float yawStep, float angleOff, bool roundStart)
    {
        Signal? fired = null;
        float t = 0f, yaw = 0f, bearing = 0f;
        for (int i = 0; i < polls; i++)
        {
            var sample = new WallhackGazeDetector.GazeSample(
                EnemyId: 2, AngleOffDeg: angleOff, ViewYaw: yaw, EnemyBearingYaw: bearing, RoundStart: roundStart);
            var s = d.Observe(slot, t, sample);
            if (s is not null) fired = s;
            yaw += yawStep;      // view sweeps...
            bearing += yawStep;  // ...in step with the enemy's bearing
            t += PollDt;
        }
        return fired;
    }

    [Fact]
    public void Flags_gaze_that_follows_enemy_movement()
    {
        var d = new WallhackGazeDetector(coneDeg: 25f, triggerScore: 0.6f, roundStartMultiplier: 2f);

        var signal = RunFollow(d, slot: 1, polls: 40, yawStep: 1.0f, angleOff: 3f, roundStart: false);

        Assert.NotNull(signal);
        Assert.Equal("wallhack.gaze", signal!.Value.Detector);
    }

    [Fact]
    public void Round_start_reaches_the_threshold_faster()
    {
        // Same following behaviour; the round-start weighting trips it in fewer polls than normal.
        var normal = new WallhackGazeDetector(coneDeg: 25f, triggerScore: 0.6f, roundStartMultiplier: 3f);
        var atRoundStart = new WallhackGazeDetector(coneDeg: 25f, triggerScore: 0.6f, roundStartMultiplier: 3f);

        var normalHit = RunFollow(normal, 1, polls: 8, yawStep: 1.0f, angleOff: 3f, roundStart: false);
        var roundHit = RunFollow(atRoundStart, 1, polls: 8, yawStep: 1.0f, angleOff: 3f, roundStart: true);

        Assert.Null(normalHit);      // not enough accumulation yet without the bonus
        Assert.NotNull(roundHit);    // the round-start multiplier gets there
    }

    [Fact]
    public void Ignores_gaze_that_does_not_track_movement()
    {
        // The enemy's bearing changes but the view stays put — pointing near it, not following it.
        var d = new WallhackGazeDetector(coneDeg: 25f, triggerScore: 1.0f);

        Signal? fired = null;
        float t = 0f, bearing = 0f;
        for (int i = 0; i < 60; i++)
        {
            var sample = new WallhackGazeDetector.GazeSample(2, AngleOffDeg: 3f, ViewYaw: 0f, EnemyBearingYaw: bearing, RoundStart: false);
            var s = d.Observe(1, t, sample);
            if (s is not null) fired = s;
            bearing += 1.0f; // enemy moves in bearing; view (yaw 0) does not follow
            t += PollDt;
        }

        Assert.Null(fired);
    }

    [Fact]
    public void No_unspotted_enemy_never_flags()
    {
        var d = new WallhackGazeDetector();

        Signal? fired = null;
        for (int i = 0; i < 60; i++)
        {
            var s = d.Observe(1, i * PollDt, sample: null);
            if (s is not null) fired = s;
        }

        Assert.Null(fired);
    }
}
