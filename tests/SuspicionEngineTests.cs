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
}
