using System.Numerics;

namespace OSAntiCheat.Visibility;

/// <summary>
/// The detection-side visibility question: is EVERY sampled point on an enemy body occluded
/// by baked static geometry from the observer's eye? Samples several heights plus head-height
/// shoulder offsets perpendicular to the sightline. Sampling above a crouched head or beside
/// the body errs toward CLEAR, which errs toward not flagging — mirroring CS2FOW's fail-open
/// into detection's fail-safe. Validated in the 21-demo A/B sweep (see docs/visibility-oracle.md
/// and TODO.md "GEO-GATE-EXPERIMENTET").
/// </summary>
public static class BodyOcclusion
{
    /// <summary>
    /// True only when eye→every sample is blocked. <paramref name="cachedPacket"/> carries the
    /// last blocking packet for this observer/enemy pair (pass <see cref="Bvh8Map.InvalidRef"/>
    /// initially); near-stationary pairs then skip the full tree descent.
    /// </summary>
    public static bool AllSamplesBlocked(Bvh8Map bake, Vector3 eye, Vector3 feet, ref uint cachedPacket)
    {
        var toEnemy = new Vector3(feet.X - eye.X, feet.Y - eye.Y, 0f);
        var lateral = toEnemy.LengthSquared() > 1e-6f
            ? Vector3.Normalize(new Vector3(-toEnemy.Y, toEnemy.X, 0f)) * 14f
            : Vector3.Zero;
        Span<Vector3> samples =
        [
            feet + new Vector3(0f, 0f, 4f),   // ankles (sees through low gaps like CS2FOW's feet point)
            feet + new Vector3(0f, 0f, 36f),  // chest
            feet + new Vector3(0f, 0f, 46f),  // crouched head
            feet + new Vector3(0f, 0f, 60f),  // standing head
            feet + new Vector3(0f, 0f, 60f) + lateral,
            feet + new Vector3(0f, 0f, 60f) - lateral,
        ];

        uint cached = cachedPacket;
        foreach (var sample in samples)
        {
            var hit = Bvh8Raycaster.SegmentBlocked(bake, eye, sample, cached);
            if (!hit.Blocked)
                return false;
            cached = hit.PacketIndex;
        }
        cachedPacket = cached;
        return true;
    }
}
