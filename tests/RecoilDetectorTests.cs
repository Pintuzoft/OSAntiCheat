using System.Numerics;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Anti-recoil is a LOGIC-BREACH axis: recoil compensation too consistent to be human. A script's
/// spray curve is identical every time (spread ~0 → ratio ~0, below the 0.06 human floor); a human's
/// varies. These tests feed synthetic sprays and assert the machine fires, the human stays silent.
/// </summary>
public class RecoilDetectorTests
{
    private int _seq;

    // Feed one spray of 8 shots following `curve` (pitch pulled down = real recoil compensation),
    // shots 0.05s apart (within the 0.13s gap), with `jitterPerShot` added to make sprays differ.
    private Signal? FireSpray(RecoilDetector d, PlayerTracker t, float baseTime, float jitterPerShot)
    {
        // The signal fires on the FIRST shot of a new spray (the flush of the previous one), so keep
        // any non-null return, not just the last shot's (which is a continuation → null).
        Signal? found = null;
        for (int k = 0; k < 8; k++)
        {
            float pitch = -1f * k + jitterPerShot * k;   // pulls down ~7° over the spray (pull > 2°)
            float time = baseTime + k * 0.05f;
            t.Add(new TickSample(_seq++, time, Vector3.Zero, new ViewAngles(pitch, 0f, 0f),
                Vector3.Zero, OnGround: true, Alive: true));
            found ??= d.OnFire(t, "ak47", time);
        }
        return found;
    }

    [Fact]
    public void Flags_identical_machine_sprays()
    {
        var d = new RecoilDetector(maxRatio: 0.04f, minSprays: 4);
        var t = new PlayerTracker(128, slot: 1);

        Signal? fired = null;
        for (int spray = 0; spray < 6; spray++)
        {
            // Gap of 1s between sprays; every curve IDENTICAL (jitter 0) → spread 0 → ratio 0.
            var s = FireSpray(d, t, baseTime: spray * 1f, jitterPerShot: 0f);
            if (s is not null) fired = s;
        }

        Assert.NotNull(fired);
        Assert.Equal("anti-recoil", fired!.Value.Detector);
        Assert.Equal(DetectorKind.LogicBreach, d.Kind);
    }

    [Fact]
    public void Ignores_varying_human_sprays()
    {
        var d = new RecoilDetector(maxRatio: 0.04f, minSprays: 4);
        var t = new PlayerTracker(128, slot: 1);

        Signal? fired = null;
        for (int spray = 0; spray < 6; spray++)
        {
            // Each spray compensates DIFFERENTLY (jitter varies per spray) → large spread → human.
            var s = FireSpray(d, t, baseTime: spray * 1f, jitterPerShot: 0.3f * (spray - 3));
            if (s is not null) fired = s;
        }

        Assert.Null(fired);
    }
}
