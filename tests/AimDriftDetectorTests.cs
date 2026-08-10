using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Aim drift: the per-lobby excess of error-reducing aim steps. Fixtures mirror the corpus read
/// (305 honest sessions: ~51% baseline, max 56.6%/z 2.79) and the C8 profile (59.6% on 977
/// steps, z=+4.40): a drifting observer must cross the gate only against a healthy lobby
/// baseline, only above minSteps, and only once per z band.
/// </summary>
public sealed class AimDriftDetectorTests
{
    // Build an observer whose moving steps reduce the error toward the enemy with probability
    // pToward (deterministic interleave — no RNG). The ERROR series is driven directly: toward-
    // votes shrink it 0.3°, away-votes grow it by 0.3·p/(1−p) so the series is stationary (no
    // wall to bounce off — the flaw that sank the first fixture). The observer's own yaw
    // alternates ±0.5° so every tick is a moving step; the enemy is PLACED each tick at bearing
    // (yaw − err), which realises exactly the wanted error.
    private static (PlayerTracker obs, PlayerTracker enemy) Build(
        int steps, float pToward, int startSeq = 1, int obsSlot = 1, int enemySlot = 2)
    {
        var obs = new PlayerTracker(steps + 2, obsSlot);
        var enemy = new PlayerTracker(steps + 2, enemySlot);
        float err = 8f;
        float away = 0.3f * pToward / (1f - pToward);
        float yaw = 0f;
        int toward = 0;
        for (int i = 0; i < steps + 1; i++)
        {
            int seq = startSeq + i;
            if (i > 0)
            {
                bool stepToward = toward < pToward * i;
                err += stepToward ? -0.3f : away;
                if (stepToward) toward++;
                yaw = (i % 2 == 0) ? 0f : 0.5f;   // guarantees a >=0.1° step every tick
            }
            obs.Add(new TickSample(seq, seq / 64f, Vector3.Zero,
                new ViewAngles(0f, yaw, 0f), Vector3.Zero, true, true));
            float bearing = (yaw - err) * MathF.PI / 180f;
            enemy.Add(new TickSample(seq, seq / 64f,
                new Vector3(500f * MathF.Cos(bearing), 500f * MathF.Sin(bearing), 0f),
                new ViewAngles(0f, 180f, 0f), Vector3.Zero, true, true));
        }
        return (obs, enemy);
    }

    private static void Feed(AimDriftDetector d, PlayerTracker obs, PlayerTracker enemy,
        out Signal? last)
    {
        last = d.Observe(obs, new List<PlayerTracker> { enemy }, now: 100f);
    }

    [Fact]
    public void Drifter_over_healthy_lobby_baseline_fires_with_rate_in_reason()
    {
        var d = new AimDriftDetector(minSteps: 500, minZ: 3f, minPopSteps: 3000);
        // Lobby: three players at the honest ~51% rate build the baseline.
        for (int slot = 0; slot < 3; slot++)
        {
            var (o, e) = Build(1500, 0.51f, obsSlot: 10 + slot, enemySlot: 20 + slot);
            d.Observe(o, new List<PlayerTracker> { e }, 50f);
        }
        // C8 profile: 59.6% of 1000 steps.
        var (obs, enemy) = Build(1000, 0.596f);
        Feed(d, obs, enemy, out var s);
        Assert.NotNull(s);
        Assert.Equal("aim.drift", s!.Value.Detector);
        Assert.Null(s.Value.Edge);                          // fusion only — can never act
        Assert.Equal(DetectorKind.Behavioural, d.Kind);
        Assert.Contains("pull toward enemies", s.Value.Reason);
    }

    [Fact]
    public void Honest_rate_never_fires_and_thin_lobby_abstains()
    {
        var d = new AimDriftDetector(minSteps: 500, minZ: 3f, minPopSteps: 3000);
        // Thin lobby: a drifter with NO baseline must abstain regardless of rate.
        var (obs, enemy) = Build(1000, 0.62f);
        Feed(d, obs, enemy, out var s);
        Assert.Null(s);

        // Healthy baseline, honest player at the population median: silent.
        var d2 = new AimDriftDetector(minSteps: 500, minZ: 3f, minPopSteps: 3000);
        var (bg, bge) = Build(4000, 0.51f, obsSlot: 10, enemySlot: 20);
        d2.Observe(bg, new List<PlayerTracker> { bge }, 50f);
        var (h, he) = Build(1000, 0.52f);
        Feed(d2, h, he, out var s2);
        Assert.Null(s2);
    }

    [Fact]
    public void Below_min_steps_stays_silent_and_bands_emit_once()
    {
        var d = new AimDriftDetector(minSteps: 500, minZ: 3f, minPopSteps: 3000);
        var (bg, bge) = Build(4000, 0.51f, obsSlot: 10, enemySlot: 20);
        d.Observe(bg, new List<PlayerTracker> { bge }, 50f);

        // 300 steps of blatant drift: under the evidence floor — silent.
        var (few, fewE) = Build(300, 0.65f);
        Feed(d, few, fewE, out var s);
        Assert.Null(s);

        // Same drifter with enough steps fires exactly once for its band (re-observe: no new data).
        var (obs, enemy) = Build(1200, 0.62f, startSeq: 5000);
        Feed(d, obs, enemy, out var s1);
        Assert.NotNull(s1);
        Feed(d, obs, enemy, out var s2);
        Assert.Null(s2);   // same band, no new ticks — one whisper per escalation, never spam
    }
}
