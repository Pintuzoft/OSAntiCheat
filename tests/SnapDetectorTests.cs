using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// aimbot.snap is a LOGIC-BREACH axis: the crosshair was OFF the head one tick before the shot, then
/// sits on the head CENTRE at machine precision at the shot. Two weak facts (precision, and "acquired
/// this tick, not a held/tracked angle") that are lethal together. These tests model a machine pull
/// (off -> exact in one tick) vs a human flick (off -> lands on the 1-2° motor hump) vs a track (already
/// near the head a tick before), on synthetic tick data. Enemy stands straight ahead; "off" is the
/// shooter's yaw a tick before, "exact" the yaw at the shot.
/// </summary>
public class SnapDetectorTests
{
    private const float TickDt = 1f / 64f;
    private int _seq;

    // One "pull shot": tick N-1 with the aim `prevOffDeg` off the head, then tick N with the aim
    // `fireErrDeg` off it, both to an enemy `dist` units straight ahead. Returns OnFire's verdict at N.
    private Signal? Pull(SnapDetector d, PlayerTracker shooter, PlayerTracker enemy,
        float dist, float prevOffDeg, float fireErrDeg)
    {
        var feet = new Vector3(dist, 0f, 0f);
        int prev = _seq++, fire = _seq++;
        enemy.Add(new TickSample(prev, prev * TickDt, feet, default, Vector3.Zero, true, true));
        shooter.Add(new TickSample(prev, prev * TickDt, Vector3.Zero, new ViewAngles(0f, prevOffDeg, 0f), Vector3.Zero, true, true));
        enemy.Add(new TickSample(fire, fire * TickDt, feet, default, Vector3.Zero, true, true));
        shooter.Add(new TickSample(fire, fire * TickDt, Vector3.Zero, new ViewAngles(0f, fireErrDeg, 0f), Vector3.Zero, true, true));
        return d.OnFire(shooter, new[] { enemy }, fire * TickDt);
    }

    [Fact]
    public void Flags_repeated_pull_to_head()
    {
        var d = new SnapDetector(exactDeg: 0.05f, offFloorDeg: 5f, minSnaps: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        Pull(d, shooter, enemy, dist: 1000f, prevOffDeg: 20f, fireErrDeg: 0f);       // pull 1
        var last = Pull(d, shooter, enemy, dist: 1000f, prevOffDeg: 20f, fireErrDeg: 0f); // pull 2 -> fires

        Assert.NotNull(last);
        Assert.Equal("aimbot.snap", last!.Value.Detector);
    }

    [Fact]
    public void Is_a_logic_breach_axis()
    {
        Assert.Equal(DetectorKind.LogicBreach, new SnapDetector().Kind);
    }

    [Fact]
    public void Stays_silent_on_one_pull()
    {
        var d = new SnapDetector(exactDeg: 0.05f, offFloorDeg: 5f, minSnaps: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // A single off->exact could be tick-aliasing; the axis waits for the pattern.
        var signal = Pull(d, shooter, enemy, dist: 1000f, prevOffDeg: 20f, fireErrDeg: 0f);
        Assert.Null(signal);
    }

    [Fact]
    public void Ignores_a_track_already_on_the_head()
    {
        var d = new SnapDetector(exactDeg: 0.05f, offFloorDeg: 5f, minSnaps: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // Already ~1° off the head a tick before (a settled track / held angle, not a pull) -> never a snap,
        // even landing exact. This is the gate that keeps bone-lock's held-pre-aim story out.
        Signal? last = null;
        for (int i = 0; i < 5; i++)
            last = Pull(d, shooter, enemy, dist: 1000f, prevOffDeg: 1f, fireErrDeg: 0f);
        Assert.Null(last);
    }

    [Fact]
    public void Ignores_human_flick_landing_on_the_motor_hump()
    {
        var d = new SnapDetector(exactDeg: 0.05f, offFloorDeg: 5f, minSnaps: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // A real flick covers the angle but lands at 1.6° (the human motor floor), never sub-quant.
        Signal? last = null;
        for (int i = 0; i < 10; i++)
            last = Pull(d, shooter, enemy, dist: 1000f, prevOffDeg: 20f, fireErrDeg: 1.6f);
        Assert.Null(last);
    }

    [Fact]
    public void Ignores_point_blank_degenerate_range()
    {
        var d = new SnapDetector(exactDeg: 0.05f, offFloorDeg: 5f, minSnaps: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var enemy = new PlayerTracker(64, slot: 2);

        // Stacked / point-blank (<64u): every angular metric collapses to ~0, so no measurement.
        Signal? last = null;
        for (int i = 0; i < 5; i++)
            last = Pull(d, shooter, enemy, dist: 20f, prevOffDeg: 20f, fireErrDeg: 0f);
        Assert.Null(last);
    }
}
