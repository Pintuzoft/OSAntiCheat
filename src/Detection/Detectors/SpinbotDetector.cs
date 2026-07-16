using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags impossible sustained yaw rotation (spinbot). A human can flick fast for an
/// instant, but cannot *sustain* a very high yaw rate across many consecutive ticks.
///
/// We therefore measure the average yaw rate over a short window and require it to be
/// sustained (consistent direction, not a back-and-forth artefact). Purely self-contained:
/// no enemies, no LOS — just the player's own angle history.
/// </summary>
public sealed class SpinbotDetector : IDetector
{
    public string Id => "spinbot";
    public float Weight => 1.0f;

    // A fast human flick tops out well under the suspect rate sustained; spinbot runs above it.
    private readonly float _suspectRateDegPerSec;
    private readonly float _certainRateDegPerSec;
    private const int WindowSamples = 16;       // ~0.25s at 64 tick
    private const float MinConsistency = 0.85f; // |net rotation| / |total rotation|; ~1 = one direction

    public SpinbotDetector(float suspectRateDegPerSec = 1000f, float certainRateDegPerSec = 2200f)
    {
        _suspectRateDegPerSec = suspectRateDegPerSec;
        _certainRateDegPerSec = MathF.Max(certainRateDegPerSec, suspectRateDegPerSec + 1f);
    }

    /// <summary>Examine a player's recent history; returns a signal if a spin is detected.</summary>
    public Signal? Inspect(PlayerTracker tracker)
    {
        if (tracker.Count < WindowSamples) return null;

        float net = 0f;    // signed sum — cancels on back-and-forth
        float total = 0f;  // absolute sum — the raw amount of turning
        float elapsed = 0f;

        // Walk newest -> older across the window, summing per-tick yaw deltas.
        for (int i = 0; i < WindowSamples - 1; i++)
        {
            var newer = tracker[i];
            var older = tracker[i + 1];
            if (!newer.Alive || !older.Alive) return null;

            // Gap in the sample sequence => possible lag; don't trust this window.
            if (newer.Sequence - older.Sequence != 1) return null;

            float dt = newer.Time - older.Time;
            if (dt <= 0f) return null;

            float d = Geometry.YawDelta(older.Angles.Yaw, newer.Angles.Yaw);
            net += d;
            total += MathF.Abs(d);
            elapsed += dt;
        }

        if (elapsed <= 0f || total <= 0f) return null;

        float rate = total / elapsed;                 // deg/sec of turning
        float consistency = MathF.Abs(net) / total;   // one-directional spin vs jitter

        if (rate < _suspectRateDegPerSec || consistency < MinConsistency)
            return null;

        float confidence = Math.Clamp(
            (rate - _suspectRateDegPerSec) / (_certainRateDegPerSec - _suspectRateDegPerSec),
            0f, 1f);

        var latest = tracker[0];
        return new Signal(
            Id, tracker.Slot, latest.Time, confidence,
            $"sustained yaw {rate:F0} deg/s over {elapsed * 1000f:F0}ms, consistency {consistency:F2}");
    }
}
