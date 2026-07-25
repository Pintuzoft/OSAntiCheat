using System.Numerics;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags an aimbot SNAP-to-head at a kill: the crosshair was clearly OFF the victim's head one tick
/// before the fatal shot, then sits on the head CENTRE at bone-lock precision on the shot tick. Two
/// independently-weak facts that are lethal only in conjunction:
///
///   • landing precision ≤ <see cref="_exactDeg"/> — the <see cref="BoneLockDetector"/> hard edge
///     (a human lands on the 1–2° motor hump even at tier-1; the sub-quant zone was empty across the
///     archive), AND
///   • the crosshair was ≥ <see cref="_offFloor"/>° off that SAME head ONE tick earlier — so the
///     exact placement was ACQUIRED this tick, not a held pre-aim or a settled track.
///
/// Bone-lock alone can be explained away as a lucky held angle; the off-target-before gate kills that
/// story — a tick ago you were pointing somewhere else. A human flick that covers the same angle in
/// one tick lands on the motor hump (1–2°), never sub-quant: you cannot both traverse a large angle
/// AND stop on the head's centre in a single tick. So this is a <see cref="DetectorKind.LogicBreach"/>.
///
/// Runs on EVERY shot, INCLUDING mid-burst — deliberately NOT first-of-burst gated like bone-lock.
/// That is the whole point: the classic spray-aimbot pulls only one shot in the spray (the "3rd/5th
/// shot headshot") onto the head and lets the rest scatter, so the pull lives mid-burst where bone-lock
/// never looks. Recoil control / muscle memory can walk a spray TOWARD a head — but it lands on the
/// motor hump (1–2°), never sub-quant; the pull-to-centre a tick before the shot is the machine tell.
///
/// Deliberately NARROW (only N-1, the discontinuity): a SMOOTHED/humanised aimbot spreads its
/// approach over several ticks and looks exactly like a human ramp — separable only by where it lands,
/// which is precisely what bone-lock already measures on repeat. Widening the window here would just
/// re-derive bone-lock, so the smoothed case is left to it; snap owns the instant pull, and its unique
/// value is firing on very few shots (the off→exact conjunction is far rarer than exact alone).
///
/// Honest caveat: at 64-tick a fast flick can complete inside one sample interval, so the SNAP timing
/// alone is muddied by granularity — the discriminator that survives is the LANDING PRECISION, not the
/// snap speed. Repetition-gated: a single instance could be tick-aliasing / position-interp noise.
/// </summary>
public sealed class SnapDetector : IDetector
{
    public string Id => "aimbot.snap";
    public float Weight => 1.6f;                        // logic-breach axis: a confirmed pattern is near-certain
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private const float EyeHeight = 64f;
    private const float MinRangeUnits = 64f;            // below ~1.2m every angular metric is degenerate
    private const float OnTargetErrorDeg = 5f;          // the shot must be on an enemy to pick a target
    private const float WindowSeconds = 1200f;          // snaps are rare; a long session window

    private readonly float _exactDeg;                   // ≤ this to head centre AT the shot = machine precision
    private readonly float _offFloor;                   // ≥ this off the head ONE tick before = acquired, not pre-aim
    private readonly int _minSnaps;                      // repeated snaps required before speaking

    private readonly Dictionary<int, List<float>> _snaps = new(); // snap times per slot

    public SnapDetector(float exactDeg = 0.05f, float offFloorDeg = 5f, int minSnaps = 2)
    {
        _exactDeg = exactDeg;
        _offFloor = offFloorDeg;
        _minSnaps = Math.Max(1, minSnaps);
    }

    public void Remove(int slot) => _snaps.Remove(slot);

    /// <summary>
    /// Called on every shot (mid-burst included). Returns a signal once the off→exact pull-to-head has
    /// repeated <see cref="_minSnaps"/> times: off the head at N-1, exact head centre at the shot (N).
    /// </summary>
    public Signal? OnFire(PlayerTracker shooter, IEnumerable<PlayerTracker> enemies, float now)
    {
        if (shooter.Count < 2) return null;
        var k0 = shooter[0];  // fire tick (N)
        var k1 = shooter[1];  // one tick before (N-1)
        if (k0.Sequence - k1.Sequence != 1 || !k0.Alive || !k1.Alive) return null;
        var eye0 = k0.Origin + new Vector3(0f, 0f, EyeHeight);

        // The target is the enemy the shot is on at the fire tick (a snap lands exact, so nearest-body
        // picks it out cleanly). Must be on-target and not point-blank.
        PlayerTracker? target = null;
        TickSample tgt0 = default;
        float bestErr = OnTargetErrorDeg;
        foreach (var enemy in enemies)
        {
            if (!enemy.TryGetBySequence(k0.Sequence, out var e) || !e.Alive) continue;
            float err = Geometry.NearestBodyAimError(eye0, k0.Angles, e.Origin);
            if (err < bestErr) { bestErr = err; target = enemy; tgt0 = e; }
        }
        if (target is null) return null;
        if (Vector3.Distance(k0.Origin, tgt0.Origin) < MinRangeUnits) return null;   // degenerate range
        if (!target.TryGetBySequence(k1.Sequence, out var tgt1) || !tgt1.Alive) return null;

        // Head-centre error at the shot, and at the tick before, each to the head at THAT tick.
        float errFire = Geometry.AimErrorTo(eye0, k0.Angles, tgt0.Origin + new Vector3(0f, 0f, EyeHeight));
        if (errFire > _exactDeg) return null;   // not on the head centre at the shot

        var eye1 = k1.Origin + new Vector3(0f, 0f, EyeHeight);
        float errPrev = Geometry.AimErrorTo(eye1, k1.Angles, tgt1.Origin + new Vector3(0f, 0f, EyeHeight));
        if (errPrev < _offFloor) return null;   // already near the head a tick ago — held/tracked, not a pull

        if (!_snaps.TryGetValue(shooter.Slot, out var window))
            _snaps[shooter.Slot] = window = new List<float>();
        window.RemoveAll(t => k0.Time - t > WindowSeconds);
        window.Add(k0.Time);
        if (window.Count < _minSnaps) return null; // one could be tick-aliasing; wait for the pattern

        float confidence = Math.Clamp(0.85f + 0.05f * (window.Count - _minSnaps), 0.85f, 1f);
        return new Signal(
            Id, shooter.Slot, k0.Time, confidence,
            $"{window.Count} snap-to-head shots: {errPrev:F1}° off → {errFire:F3}° on head in 1 tick — beyond human");
    }
}
