using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// aimbot.silent is a LOGIC-BREACH axis: a bullet registered damage while the shooter's replicated
/// view pointed ≥ the off-floor away from EVERY position the victim held in the lag-comp window.
/// The two honest ways a hit lands off-view are modelled and must stay silent: a laggy shot (view
/// matches the victim's PAST position) and a mid-spray hit (recoil-compensated view — excluded by
/// the first-of-burst gate). The cheat case — repeated first-bullet hits with the view parked far
/// off (the archived frozen-view psilent case) — must fire.
/// </summary>
public class SilentAimDetectorTests
{
    private const float TickDt = 1f / 64f;
    private int _seq = 100;

    private float Now() => _seq * TickDt;

    // Advance one tick: shooter aims `viewOffDeg` off the victim's CURRENT position (yaw), victim
    // stands at `victimPos`. Returns the tick's time.
    private float Tick(PlayerTracker shooter, PlayerTracker victim, Vector3 victimPos, float viewOffDeg)
    {
        int s = _seq++;
        float t = s * TickDt;
        victim.Add(new TickSample(s, t, victimPos, default, Vector3.Zero, true, true));
        // Yaw 0 points at +X; the victim sits along +X, so yaw = viewOffDeg is that far off.
        shooter.Add(new TickSample(s, t, Vector3.Zero, new ViewAngles(0f, viewOffDeg, 0f), Vector3.Zero, true, true));
        return t;
    }

    // A first-of-burst bullet hit: history ticks first (so lag-comp has candidates), then the hurt.
    private Signal? Hit(SilentAimDetector d, PlayerTracker shooter, PlayerTracker victim,
        float viewOffDeg, string weapon = "ak47", float victimX = 800f, float pauseSec = 1f)
    {
        _seq += (int)(pauseSec / TickDt);           // pause so this fire OPENS a burst
        var pos = new Vector3(victimX, 0f, 0f);
        for (int i = 0; i < 6; i++) Tick(shooter, victim, pos, viewOffDeg);
        float t = Now() - TickDt;
        d.NoteFire(shooter.Slot, t);
        return d.OnHurt(shooter, victim, weapon, 30, t);
    }

    [Fact]
    public void Is_a_logic_breach_axis()
    {
        Assert.Equal(DetectorKind.LogicBreach, new SilentAimDetector().Kind);
    }

    [Fact]
    public void Flags_repeated_first_bullet_hits_with_view_parked_off()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 3);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 22f));   // 1: silent (repetition gate)
        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 22f));   // 2: silent
        var s = Hit(d, shooter, victim, viewOffDeg: 22f);        // 3: the pattern
        Assert.NotNull(s);
        Assert.Equal("aimbot.silent", s!.Value.Detector);
    }

    [Fact]
    public void Stays_silent_when_view_is_on_the_victim()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 0.5f));
    }

    [Fact]
    public void Lag_compensated_shot_matching_a_past_position_stays_silent()
    {
        // Victim strafes: the shot's view is 20° off the CURRENT position but exactly on where the
        // victim stood a few ticks ago — an honest laggy hit. Must not flag.
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        // Past positions at yaw 0 (straight ahead), then the victim moves to ~20° while the view
        // stays at yaw 0: minimum over the window finds the old position -> near-zero error.
        var oldPos = new Vector3(800f, 0f, 0f);
        var newPos = new Vector3(800f, 290f, 0f);   // ~20° left of +X
        for (int i = 0; i < 8; i++) Tick(shooter, victim, oldPos, 0f);
        for (int i = 0; i < 2; i++) Tick(shooter, victim, newPos, 0f);
        float t = Now() - TickDt;
        d.NoteFire(shooter.Slot, t);
        Assert.Null(d.OnHurt(shooter, victim, "ak47", 30, t));
    }

    [Fact]
    public void Mid_spray_hit_is_recoil_confounded_and_skipped()
    {
        // Second bullet 100 ms after the first: recoil compensation legitimately parks the view well
        // off the victim while bullets land on them. The burst gate must skip it.
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        var pos = new Vector3(800f, 0f, 0f);
        for (int i = 0; i < 8; i++) Tick(shooter, victim, pos, 25f);
        float t1 = Now() - TickDt;
        d.NoteFire(shooter.Slot, t1);               // burst opener (its hurt isn't fed here)
        Tick(shooter, victim, pos, 25f);
        float t2 = Now() - TickDt;                  // ~1 tick later = spraying
        d.NoteFire(shooter.Slot, t2);
        Assert.Null(d.OnHurt(shooter, victim, "ak47", 30, t2));
    }

    [Fact]
    public void Shotgun_pellets_are_excluded()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 25f, weapon: "xm1014"));
    }

    [Fact]
    public void Point_blank_is_excluded()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 25f, victimX: 60f));
    }

    [Fact]
    public void Too_little_victim_history_cannot_exclude_lag_and_stays_silent()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 1);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        var pos = new Vector3(800f, 0f, 0f);
        for (int i = 0; i < 2; i++) Tick(shooter, victim, pos, 25f);   // only 2 victim samples
        float t = Now() - TickDt;
        d.NoteFire(shooter.Slot, t);
        Assert.Null(d.OnHurt(shooter, victim, "ak47", 30, t));
    }

    [Fact]
    public void Remove_clears_the_repetition_window()
    {
        var d = new SilentAimDetector(offDeg: 15f, minHits: 2);
        var shooter = new PlayerTracker(64, slot: 1);
        var victim = new PlayerTracker(64, slot: 2);

        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 22f));
        d.Remove(shooter.Slot);
        Assert.Null(Hit(d, shooter, victim, viewOffDeg: 22f));   // window restarted -> still 1 of 2
    }
}
