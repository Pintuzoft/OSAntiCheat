using OSAntiCheat.Detection;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>Verifies the fusion model: graded scoring, corroboration bonus, decay, tiers.</summary>
public class SuspicionEngineTests
{
    private static SuspicionConfig Config() => new()
    {
        WatchThreshold = 1.0f,
        ReviewThreshold = 2.5f,
        DecayTau = 90f,
        CorroborationWindow = 10f,
        CorroborationBonus = 0.5f,
    };

    [Fact]
    public void Single_weak_signal_stays_below_watch()
    {
        var raised = new List<SuspicionAlert>();
        var engine = new SuspicionEngine(Config());
        engine.TierRaised += raised.Add;

        engine.Report(new Signal("spinbot", 1, 0f, 0.3f, "x"), weight: 1.0f); // 0.3 < 1.0

        Assert.Empty(raised);
    }

    [Fact]
    public void Corroboration_across_distinct_detectors_escalates_to_review()
    {
        var raised = new List<SuspicionAlert>();
        var engine = new SuspicionEngine(Config());
        engine.TierRaised += raised.Add;

        // 1.2 * 1 * 1 = 1.2 => Watch
        engine.Report(new Signal("aimbot.sweep", 1, 0f, 1f, "sweep"), weight: 1.2f);
        Assert.Equal(SuspicionTier.Watch, raised[^1].Tier);

        // A second DISTINCT detector 1s later: corroboration x1.5 => +1.5, total ~2.69 => Review
        engine.Report(new Signal("triggerbot", 1, 1f, 1f, "trigger"), weight: 1.0f);
        Assert.Equal(SuspicionTier.Review, raised[^1].Tier);
    }

    [Fact]
    public void Score_decays_over_time()
    {
        var engine = new SuspicionEngine(Config());
        engine.Report(new Signal("spinbot", 1, 0f, 1f, "x"), weight: 2.0f);

        float immediate = engine.ScoreOf(1, 0f);
        float later = engine.ScoreOf(1, 90f); // one tau later ~ 37%

        Assert.True(later < immediate);
        Assert.InRange(later / immediate, 0.30f, 0.42f);
    }

    private static SuspicionConfig DriftLatent() => Config() with
    {
        CorroborateOnly = new HashSet<string> { "aim.drift" },
    };

    /// <summary>The v0.9.107 FP shape: a regular climbs z bands 3→7 inside one map (0.4…0.8 at
    /// weight 0.5 = 1.5 raw, over Watch) with nothing else speaking. Must stay silent.</summary>
    [Fact]
    public void Corroborate_only_detector_never_carries_a_tier_alone()
    {
        var raised = new List<SuspicionAlert>();
        var engine = new SuspicionEngine(DriftLatent());
        engine.TierRaised += raised.Add;

        float t = 0f;
        foreach (var conf in new[] { 0.4f, 0.5f, 0.6f, 0.7f, 0.8f })
            engine.Report(new Signal("aim.drift", 1, t += 5f, conf, "band"), weight: 0.5f);

        Assert.Empty(raised);
        Assert.Equal(0f, engine.ScoreOf(1, t));
    }

    /// <summary>The C8 shape: the same drift next to a live wall axis still adds — the latent
    /// bucket wakes up the moment a carrying axis speaks, bands included — but never contributes
    /// more than the carrying axis itself did.</summary>
    [Fact]
    public void Corroborate_only_score_counts_once_another_axis_is_alive()
    {
        var raised = new List<SuspicionAlert>();
        var engine = new SuspicionEngine(DriftLatent());
        engine.TierRaised += raised.Add;

        engine.Report(new Signal("aim.drift", 1, 0f, 0.8f, "band"), weight: 0.5f);   // latent 0.4
        engine.Report(new Signal("aim.drift", 1, 5f, 0.9f, "band"), weight: 0.5f);   // latent ~0.85
        Assert.Empty(raised);

        // A weak null test 20 s later: 0.5*0.5 = 0.25 carrying. Drift may at most match it:
        // 0.25 + 0.25 = 0.5 — still silent, even though ~0.7 of latent drift is alive.
        engine.Report(new Signal("wallhack.nulltest", 1, 25f, 0.5f, "z=3"), weight: 0.5f);
        Assert.Empty(raised);
        Assert.InRange(engine.ScoreOf(1, 25f), 0.45f, 0.55f);

        // A track kill 1 s later (1.0*0.6, x1.5 corroboration with the null test in-window = 0.9):
        // carrying 1.15, latent doubles what it can => over Watch.
        engine.Report(new Signal("wallhack.track", 1, 26f, 0.6f, "venn"), weight: 1.0f);
        Assert.Single(raised);
        Assert.Equal(SuspicionTier.Watch, raised[0].Tier);
    }

    /// <summary>Carrying axes are untouched by the bucket: the old behaviour for everyone else.</summary>
    [Fact]
    public void Non_latent_detectors_still_carry_alone()
    {
        var raised = new List<SuspicionAlert>();
        var engine = new SuspicionEngine(DriftLatent());
        engine.TierRaised += raised.Add;

        engine.Report(new Signal("aimbot.bonelock", 1, 0f, 0.8f, "locks"), weight: 1.6f);
        Assert.Single(raised);
    }
}
