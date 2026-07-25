using System.Numerics;
using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags aim assistance by how often a player opens fire *mid-sweep* and is already on a body.
///
/// This replaces the snap detector, which was the same idea measured on the wrong axis. That one
/// asked how FAST the view moved and looked for an inhuman number — but there isn't one. Good
/// players flick hard constantly, and requiring 25° inside a single tick quietly demanded
/// 1600°/s, so it produced exactly zero signals across 6.1M archived shots.
///
/// What no hand does is *connect while still travelling*. Firing mid-sweep is firing blind: you
/// haven't settled, so you're guessing. A human lands few of those. Something steering the
/// crosshair lands nearly all of them, because it only ever lets the shot go once the aim is
/// correct — and it doesn't care that the view is still moving when that happens.
///
/// Measured against CS2CD's 254 manually-verified cheaters, thresholded above the non-cheaters'
/// p99: it caught 15.4% of cheaters while touching 1.1% of everyone else — a 14x lift, where
/// dwell managed 1.8% (chance) and hit-rate 6.9%. It is the only metric of the six built here
/// that survived contact with labelled data.
///
/// Two gates keep the honest explanations out. Requiring the view to still be travelling excludes
/// the pre-fire — crosshair parked on a corner, enemy walks into it, which is a perfectly legal
/// shot with zero reaction time. Requiring the burst's first shot excludes the spray, where the
/// bullets were already flying before anyone crossed. Those two behaviours put ~24% of *every*
/// player's shots inside 30ms of the crosshair touching an enemy, and they are what made the
/// earlier reaction-time detectors measure noise.
///
/// It is a ratio, not a count: a single lucky mid-sweep hit means nothing, so it stays silent
/// until a real sample exists. It catches ~15% of cheaters, which means it MISSES ~85%. It is a
/// reason for an admin to watch a demo, never a verdict, and never grounds for an automatic ban.
/// </summary>
public sealed class AimbotSweepDetector : IDetector
{
    public string Id => "aimbot.sweep";
    public float Weight => 1.2f;

    private const float OnTargetDeg = 1f;         // "connected": the shot is on a hurtbox
    private const float EyeHeight = 64f;
    private const float WindowSeconds = 1200f;    // ~a map: judge current play, not an old session
    private const float CertainRate = 0.40f;      // full confidence here (CS2CD cheater max: 59%)

    private readonly float _minViewRateDegPerSec; // below this the view has settled — not a sweep
    private readonly int _minSweepShots;          // below this, no ratio is meaningful
    private readonly float _minRate;              // below this, stay silent

    private readonly Dictionary<int, List<SweepShot>> _recent = new();

    private readonly record struct SweepShot(float Time, bool OnTarget);

    /// <param name="minViewRateDegPerSec">
    /// At 64 tick a sample is 15.6ms, so 90°/s is 1.4° of travel between samples: plainly still
    /// moving, without only catching wild flicks. Pairing a rate gate with a large single-tick
    /// crossing requirement is self-defeating — the first attempt did, and silently required
    /// 512°/s, matching nothing but people slinging the crosshair across the map.
    /// </param>
    /// <param name="minRate">
    /// Defaults to CS2CD's p99 of random matchmaking players. Regulars on a long-running server
    /// are not random matchmaking players, so this threshold is NOT calibrated for them yet —
    /// see TODO.md. Until the archive answers that, treat any signal as uncalibrated.
    /// </param>
    public AimbotSweepDetector(
        float minViewRateDegPerSec = 90f, int minSweepShots = 20, float minRate = 0.161f)
    {
        _minViewRateDegPerSec = minViewRateDegPerSec;
        _minSweepShots = Math.Max(1, minSweepShots);
        _minRate = minRate;
    }

    /// <summary>
    /// Called on the FIRST shot of a burst only — the caller does the spray gating, exactly as it
    /// does for the triggerbot detector. Passing every shot of a spray in would count each bullet
    /// of one trigger pull as a fresh decision.
    /// </summary>
    public Signal? OnFire(PlayerTracker shooter, IEnumerable<PlayerTracker> enemies, float now)
    {
        if (shooter.Count < 2) return null;

        var atFire = shooter[0];
        var prev = shooter[1];
        if (!atFire.Alive) return null;
        if (atFire.Sequence - prev.Sequence != 1) return null;   // lag gap: the "sweep" is a guess

        float dt = atFire.Time - prev.Time;
        if (dt <= 0f || dt > 0.1f) return null;
        float viewRate = Geometry.AngleBetween(prev.Angles, atFire.Angles) / dt;
        if (viewRate < _minViewRateDegPerSec) return null;       // settled: an ordinary aimed shot

        // Enemy position at the fire tick, not "latest" — a moving enemy would otherwise be
        // scored against where they ended up.
        var eye = atFire.Origin + new Vector3(0f, 0f, EyeHeight);
        float nearestErr = float.MaxValue;
        foreach (var enemy in enemies)
        {
            if (!enemy.TryGetBySequence(atFire.Sequence, out var e) || !e.Alive) continue;
            float err = Geometry.NearestBodyAimError(eye, atFire.Angles, e.Origin);
            if (err < nearestErr) nearestErr = err;
        }
        if (nearestErr == float.MaxValue) return null;           // nobody alive to be on: no data

        if (!_recent.TryGetValue(shooter.Slot, out var window))
            _recent[shooter.Slot] = window = new List<SweepShot>();
        float cutoff = atFire.Time - WindowSeconds;
        window.RemoveAll(s => s.Time < cutoff);
        window.Add(new SweepShot(atFire.Time, nearestErr <= OnTargetDeg));

        if (window.Count < _minSweepShots) return null;          // accumulating, saying nothing

        int hits = window.Count(s => s.OnTarget);
        float rate = (float)hits / window.Count;
        if (rate <= _minRate) return null;

        float confidence = Math.Clamp((rate - _minRate) / Math.Max(1e-6f, CertainRate - _minRate), 0f, 1f);

        return new Signal(
            Id, shooter.Slot, atFire.Time, confidence,
            $"{hits}/{window.Count} shots opened mid-sweep (>{_minViewRateDegPerSec:F0}°/s) landed on a " +
            $"hurtbox = {rate * 100f:F1}% (threshold {_minRate * 100f:F1}%)");
    }

    /// <summary>Drop a player's window when they leave.</summary>
    public void Remove(int slot) => _recent.Remove(slot);

    public void Reset() => _recent.Clear();
}
