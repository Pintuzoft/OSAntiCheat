using System.Numerics;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags a triggerbot: a shot fired the instant the crosshair *crosses onto* an enemy,
/// faster than a human could react.
///
/// The tell is a fresh crossing, not a hold. We find the tick where the aim transitioned
/// from off-target to on-target and measure the gap to the shot. If the aim was already on
/// target for the whole lookback it's a hold / pre-aim (reaction undefined) — we skip it,
/// because holding an angle and firing is legitimate. Enemy positions are read *at the same
/// tick* as each historical shooter sample, so a moving enemy doesn't skew the crossing.
/// </summary>
public sealed class TriggerbotDetector : IDetector
{
    public string Id => "triggerbot";
    public float Weight => 1.0f;

    private const float OnTargetErrorDeg = 3f;  // crosshair considered "on" the hurtbox
    private const float CertainMs = 20f;
    private const int MaxLookbackSamples = 32;  // ~0.5s at 64 tick
    private const float EyeHeight = 64f;
    private const int PreFireMoveTicks = 3;         // window to measure the shooter's own aim motion
    private const float MinShooterAimMoveDeg = 5f;  // below this the shooter's aim was ~static

    // Repetition gating: a single fast tap is a pre-aim artefact; a triggerbot fires fast on
    // essentially every engagement. Require several within the window before speaking.
    private const float WindowSeconds = 60f;
    private const int CertainShotsInWindow = 12;

    private readonly float _humanFloorMs;       // fastest plausible human reaction
    private readonly int _minShotsInWindow;

    private readonly Dictionary<int, List<TriggerEvent>> _recent = new();

    private readonly record struct TriggerEvent(float Time, float ReactionMs, float Severity);

    public TriggerbotDetector(float humanFloorMs = 90f, int minShotsInWindow = 4)
    {
        _humanFloorMs = MathF.Max(humanFloorMs, CertainMs + 1f);
        _minShotsInWindow = Math.Max(1, minShotsInWindow);
    }

    /// <summary>Drop a player's window when they leave.</summary>
    public void Remove(int slot) => _recent.Remove(slot);

    /// <summary>Called on a player's shot. Returns a signal if the shot followed a too-fast crossing onto an enemy.</summary>
    public Signal? OnFire(PlayerTracker shooter, IEnumerable<PlayerTracker> enemies, float now)
    {
        if (shooter.Count < 2) return null;

        var atFire = shooter[0];
        if (!atFire.Alive) return null;

        // The shot must actually be on an enemy.
        if (NearestEnemyError(atFire, enemies) > OnTargetErrorDeg) return null;

        // Walk back while the aim was still on target; the tick before that is the crossing.
        int limit = Math.Min(MaxLookbackSamples, shooter.Count);
        int onsetIndex = 0;
        for (int i = 1; i < limit; i++)
        {
            var older = shooter[i];
            if (shooter[i - 1].Sequence - older.Sequence != 1) break; // lag gap => untrusted
            if (!older.Alive) break;

            if (NearestEnemyError(older, enemies) <= OnTargetErrorDeg)
                onsetIndex = i;   // still on target this far back
            else
                break;            // crossed onto target between i and i-1
        }

        // On target for the entire lookback => a hold / pre-aim, not a trigger. Can't judge.
        if (onsetIndex == limit - 1) return null;

        var onset = shooter[onsetIndex];
        float reactionMs = (atFire.Time - onset.Time) * 1000f;
        if (reactionMs >= _humanFloorMs) return null;

        // The crossing must be shooter-driven — they swept their aim ONTO the target — not the
        // enemy walking into a static/held aim (which is a hold or a spray, not a triggerbot).
        float aimMovement = 0f;
        int moveTicks = Math.Min(PreFireMoveTicks, shooter.Count - 1);
        for (int i = 0; i < moveTicks; i++)
            aimMovement += Geometry.AngleBetween(shooter[i + 1].Angles, shooter[i].Angles);
        if (aimMovement < MinShooterAimMoveDeg) return null;

        float severity = Math.Clamp(
            (_humanFloorMs - reactionMs) / (_humanFloorMs - CertainMs), 0f, 1f);

        // Record this fast crossing and prune the window.
        if (!_recent.TryGetValue(shooter.Slot, out var window))
            _recent[shooter.Slot] = window = new List<TriggerEvent>();
        window.RemoveAll(e => atFire.Time - e.Time > WindowSeconds);
        window.Add(new TriggerEvent(atFire.Time, reactionMs, severity));

        // One fast tap is a pre-aim artefact — stay silent until the pattern repeats.
        if (window.Count < _minShotsInWindow) return null;

        float countFactor = Math.Clamp(
            (window.Count - _minShotsInWindow + 1f) / (CertainShotsInWindow - _minShotsInWindow + 1f),
            0f, 1f);
        float avgSeverity = window.Average(e => e.Severity);
        float confidence = countFactor * avgSeverity;

        return new Signal(
            Id, shooter.Slot, atFire.Time, confidence,
            $"{window.Count} sub-{_humanFloorMs:F0}ms shots-on-crossing in {WindowSeconds:F0}s " +
            $"(latest {reactionMs:F0}ms, avg severity {avgSeverity:F2})");
    }

    private static float NearestEnemyError(in TickSample shot, IEnumerable<PlayerTracker> enemies)
    {
        var eye = shot.Origin + new Vector3(0f, 0f, EyeHeight);
        float best = float.MaxValue;
        foreach (var enemy in enemies)
        {
            // Compare against the enemy's position at the SAME tick as this shooter sample.
            if (!enemy.TryGetBySequence(shot.Sequence, out var e) || !e.Alive) continue;
            float err = Geometry.NearestBodyAimError(eye, shot.Angles, e.Origin);
            if (err < best) best = err;
        }
        return best;
    }
}
