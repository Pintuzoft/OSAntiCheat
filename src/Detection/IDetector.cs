namespace OSAntiCheat.Detection;

/// <summary>
/// A behavioural detector. Each detector measures ONE largely independent axis
/// (aim snaps, LOS correlation, timing, rotation …) so the fusion engine can
/// triangulate: no single detector condemns, but corroboration across axes does.
///
/// Detectors have different triggers (some on <c>WeaponFire</c>, some per tick), so
/// the trigger wiring lives in the plugin; this contract only carries identity and
/// the fusion weight the engine applies to this detector's signals.
/// </summary>
public interface IDetector
{
    /// <summary>Stable id, e.g. "aimbot.sweep". Also the corroboration key.</summary>
    string Id { get; }

    /// <summary>
    /// Fusion weight applied to this detector's signal confidence. Reflects how
    /// trustworthy / independent this axis is — kept low for FP-prone detectors.
    /// </summary>
    float Weight { get; }

    /// <summary>
    /// Response class: <see cref="DetectorKind.Behavioural"/> (improbable → review only) or
    /// <see cref="DetectorKind.LogicBreach"/> (beyond-human → auto-eligible). Defaults to
    /// Behavioural — the conservative ceiling — so a detector only reaches auto-action by
    /// explicitly declaring it measures a mechanical impossibility.
    /// </summary>
    DetectorKind Kind => DetectorKind.Behavioural;
}
