using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Bone-lock is a LOGIC-BREACH axis: repeated head-CENTRE locks tighter than a human hand. These
/// tests model a machine (aim exactly on the head centre every shot) vs a human (scatter around it)
/// vs the degenerate point-blank case that once produced fake spikes, on synthetic tick data.
/// </summary>
public class BoneLockDetectorTests
{
    private const float EyeHeight = 64f;
    private const float TickDt = 1f / 64f;

    // Fire one on-target shot: shooter aimed with `yawOffset` degrees off the enemy head centre,
    // enemy at `dist` units away. Returns the detector's verdict for this shot.
    private static Signal? Fire(BoneLockDetector d, PlayerTracker shooter, PlayerTracker enemy,
        int seq, float time, float dist, float yawOffset)
    {
        var enemyFeet = new Vector3(dist, 0f, 0f);
        enemy.Add(new TickSample(seq, time, enemyFeet, default, Vector3.Zero, true, true));
        // Aim straight at the head centre (yaw 0, pitch 0), plus a scatter offset.
        var angles = new ViewAngles(0f, yawOffset, 0f);
        shooter.Add(new TickSample(seq, time, Vector3.Zero, angles, Vector3.Zero, true, true));
        return d.OnFire(shooter, new[] { enemy }, time);
    }

    [Fact]
    public void Flags_repeated_exact_head_center_locks()
    {
        var d = new BoneLockDetector(spikeDeg: 0.05f, minSpikes: 3);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        Signal? last = null;
        for (int seq = 0; seq < 3; seq++)
            last = Fire(d, shooter, enemy, seq, seq * TickDt, dist: 1000f, yawOffset: 0f); // exact lock

        Assert.NotNull(last);
        Assert.Equal("aimbot.bonelock", last!.Value.Detector);
    }

    [Fact]
    public void Is_a_logic_breach_axis()
    {
        Assert.Equal(DetectorKind.LogicBreach, new BoneLockDetector().Kind);
    }

    [Fact]
    public void Stays_silent_on_one_exact_hit()
    {
        var d = new BoneLockDetector(spikeDeg: 0.05f, minSpikes: 3);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // One perfect hit happens at chance (~0.2%); the axis must never fire on it.
        var signal = Fire(d, shooter, enemy, 0, 0f, dist: 1000f, yawOffset: 0f);
        Assert.Null(signal);
    }

    [Fact]
    public void Ignores_human_scatter_around_the_head()
    {
        var d = new BoneLockDetector(spikeDeg: 0.05f, minSpikes: 3);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        Signal? last = null;
        for (int seq = 0; seq < 10; seq++)
            // ~1° off the head centre — on target, but a human hand's scatter, never a lock.
            last = Fire(d, shooter, enemy, seq, seq * TickDt, dist: 1000f, yawOffset: 1.0f);

        Assert.Null(last);
    }

    [Fact]
    public void Ignores_point_blank_degenerate_range()
    {
        var d = new BoneLockDetector(spikeDeg: 0.05f, minSpikes: 3);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // Stacked players (spawn-stacking): distance < 64u collapses every angular metric to ~0.
        Signal? last = null;
        for (int seq = 0; seq < 5; seq++)
            last = Fire(d, shooter, enemy, seq, seq * TickDt, dist: 20f, yawOffset: 0f);

        Assert.Null(last);
    }
}
