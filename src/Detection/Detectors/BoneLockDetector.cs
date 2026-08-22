using System.Numerics;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags a bone-locking aimbot: the view vector at the shot sits on the head's CENTRE far tighter,
/// and REPEATEDLY, than a human hand can. Validated skill-invariant offline — tier-1 pros (b1t) sit
/// in the same 1–2° hump as ordinary regulars, and the machine zone (repeated ≤0.05°) was empty
/// across ~1.5M archive shots. So this is a <see cref="DetectorKind.LogicBreach"/> axis: a cheater
/// falls BELOW the human floor, never rises to the top of it, and there is a physical gap between.
///
/// Measured on the AIM, not the bullet: weapon spread is noise the cheat does not control. A raw
/// aimbot computes the exact bone angle then quantises → error ≤ ~half a quant step (0.022°) every
/// locked shot; a human aiming at a head scatters over ~100 quant cells. The tell is the repeated
/// SPIKE at zero — never one shot (one exact hit happens at chance, ~0.2%). First-of-burst +
/// on-target only (gated by the plugin), and a degenerate-range guard drops stacked/point-blank
/// targets where every angular metric collapses to exactly 0 (spawn-stacking on small maps).
///
/// Repeated means RE-ACQUIRED: a lock counts only once the aim has travelled at least
/// <c>reacquireDeg</c> away from the previous counted lock. Without that, one frozen hold is counted
/// once per tap — the first live Review on this axis (2026-08-20 de_canals) was a regular standing
/// still and wallbanging a stationary enemy: six taps in 1.5 s, view rate 0, crosshair by chance
/// within a quant step of the model head, damage 9/9/41/33/10 (never a headshot), counted as five
/// "locks". One hold is one event; a bot re-locks across engagements and travels between them.
/// </summary>
public sealed class BoneLockDetector : IDetector
{
    public string Id => "aimbot.bonelock";
    public float Weight => 1.6f;                       // logic-breach axis: a confirmed pattern is near-certain
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private const float EyeHeight = 64f;
    private const float OnTargetErrorDeg = 5f;         // shot must be on an enemy to count
    private const float MinRangeUnits = 64f;           // below ~1.2m every angular metric is degenerate
    private const float WindowSeconds = 1200f;         // spikes are rare; a long session window

    private readonly float _spikeDeg;                  // ≤ this to head centre = a "lock" (default one quant step)
    private readonly int _minSpikes;                   // repeated locks required before speaking
    private readonly float _reacquireDeg;              // aim must travel this far from the last counted lock

    private readonly Dictionary<int, List<float>> _spikes = new();       // spike times per slot
    private readonly Dictionary<int, Hold> _lastLock = new();            // last counted lock per slot

    public BoneLockDetector(float spikeDeg = 0.05f, int minSpikes = 3, float reacquireDeg = 2f)
    {
        _spikeDeg = spikeDeg;
        _minSpikes = Math.Max(2, minSpikes);
        _reacquireDeg = Math.Max(0f, reacquireDeg);
    }

    public void Remove(int slot)
    {
        _spikes.Remove(slot);
        _lastLock.Remove(slot);
    }

    /// <summary>
    /// Called on a player's first-of-burst shot. Returns a signal once the head-centre lock has
    /// repeated <see cref="_minSpikes"/> times — never on a single exact hit.
    /// </summary>
    public Signal? OnFire(PlayerTracker shooter, IEnumerable<PlayerTracker> enemies, float now)
    {
        if (!shooter.TryLatest(out var atFire) || !atFire.Alive) return null;
        var eye = atFire.Origin + new Vector3(0f, 0f, EyeHeight);

        // Has the aim left the last counted lock? Any buffered tick or any shot since that travelled
        // ≥ reacquireDeg away marks the hold as departed; the next lock is then a new acquisition.
        if (_lastLock.TryGetValue(shooter.Slot, out var hold) && !hold.Departed)
        {
            for (int ago = 0; ago < shooter.Count; ago++)
            {
                var t = shooter[ago];
                if (t.Sequence <= hold.Sequence) break;
                if (Geometry.AngleBetween(hold.Angles, t.Angles) >= _reacquireDeg) { hold.Departed = true; break; }
            }
        }

        // Nearest enemy by body at this tick; the shot must be on them, and not point-blank.
        PlayerTracker? target = null;
        Vector3 targetFeet = default;
        float bestErr = OnTargetErrorDeg;
        foreach (var enemy in enemies)
        {
            if (!enemy.TryGetBySequence(atFire.Sequence, out var e) || !e.Alive) continue;
            float err = Geometry.NearestBodyAimError(eye, atFire.Angles, e.Origin);
            if (err < bestErr) { bestErr = err; target = enemy; targetFeet = e.Origin; }
        }
        if (target is null) return null;
        if (Vector3.Distance(atFire.Origin, targetFeet) < MinRangeUnits) return null; // degenerate range

        // Angle from the view vector to the head CENTRE (feet + eye height). A bone-lock pins this
        // to ~a quant step; a human scatters. Not the nearest-body error — the head specifically.
        float headErr = Geometry.AimErrorTo(eye, atFire.Angles, targetFeet + new Vector3(0f, 0f, EyeHeight));
        if (headErr > _spikeDeg) return null; // not a lock this shot

        // Same hold, another tap: not a new lock. The aim has to have left and come back.
        if (hold is not null && !hold.Departed) return null;
        _lastLock[shooter.Slot] = new Hold(atFire.Sequence, atFire.Angles);

        if (!_spikes.TryGetValue(shooter.Slot, out var window))
            _spikes[shooter.Slot] = window = new List<float>();
        window.RemoveAll(t => atFire.Time - t > WindowSeconds);
        window.Add(atFire.Time);

        if (window.Count < _minSpikes) return null; // one exact hit is chance; wait for the pattern

        // Beyond-human and repeated → high confidence, ramping with the count.
        float confidence = Math.Clamp(0.8f + 0.05f * (window.Count - _minSpikes), 0.8f, 1f);
        return new Signal(
            Id, shooter.Slot, atFire.Time, confidence,
            $"{window.Count} head-centre locks ≤{_spikeDeg:F3}° in {WindowSeconds:F0}s (latest {headErr:F3}°) — beyond human");
    }

    private sealed class Hold
    {
        public readonly int Sequence;
        public readonly ViewAngles Angles;
        public bool Departed;
        public Hold(int sequence, ViewAngles angles) { Sequence = sequence; Angles = angles; }
    }
}
