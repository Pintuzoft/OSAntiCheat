using System.Numerics;

namespace OSAntiCheat.Visibility;

/// <summary>
/// Answers whether the open segment between two points crosses baked map geometry.
/// Scalar port of CS2FOW's AVX traversal with identical epsilons and semantics: the
/// endpoints themselves are excluded (t strictly inside (ε, 1−ε)), comparisons are
/// written so NaN lanes never count as hits, and stack exhaustion fails open (reports
/// not blocked) exactly like upstream.
///
/// "Blocked" means a *static-geometry* occlusion, which is the trustworthy direction
/// for detection use: a baked wall really blocks sight, while "not blocked" may still
/// be occluded by doors, props, or smoke that the bake cannot know about.
/// </summary>
public static class Bvh8Raycaster
{
    private const float RayEpsilon = 1.0e-5f;
    private const float AxisEpsilon = 1.0e-12f;
    private const int StackCapacity = 512;

    public readonly record struct RayHit(bool Blocked, uint PacketIndex)
    {
        public static readonly RayHit Clear = new(false, Bvh8Map.InvalidRef);
    }

    /// <summary>
    /// Tests the segment from <paramref name="origin"/> to <paramref name="target"/>.
    /// Pass a previous hit's <c>PacketIndex</c> as <paramref name="cachedPacket"/> to
    /// retest the last blocking packet first — near-stationary players usually stay
    /// blocked by the same wall, skipping the full descent.
    /// </summary>
    public static RayHit SegmentBlocked(
        Bvh8Map map, Vector3 origin, Vector3 target, uint cachedPacket = Bvh8Map.InvalidRef)
    {
        var direction = target - origin;

        if (cachedPacket < (uint)map.PacketCount
            && PacketHit(map, cachedPacket, 8, origin, direction))
            return new RayHit(true, cachedPacket);

        Span<uint> stack = stackalloc uint[StackCapacity];
        int stackSize = 1;
        stack[0] = 0;
        while (stackSize != 0)
        {
            uint nodeIndex = stack[--stackSize];
            for (int lane = 0; lane < 8; lane++)
            {
                int i = (int)(nodeIndex * 8) + lane;
                uint reference = map.Child[i];
                if (reference == Bvh8Map.InvalidRef || !ChildHit(map, i, origin, direction))
                    continue;

                if (Bvh8Map.IsLeafRef(reference))
                {
                    uint packetIndex = Bvh8Map.LeafIndex(reference);
                    if (packetIndex != cachedPacket
                        && PacketHit(map, packetIndex, Bvh8Map.LeafTriangleCount(reference), origin, direction))
                        return new RayHit(true, packetIndex);
                }
                else if (stackSize < StackCapacity)
                {
                    stack[stackSize++] = reference;
                }
                else
                {
                    return RayHit.Clear;
                }
            }
        }
        return RayHit.Clear;
    }

    /// <summary>Slab test of one child AABB against the segment, clipped to t &lt; 1−ε.</summary>
    private static bool ChildHit(Bvh8Map map, int i, Vector3 origin, Vector3 direction)
    {
        float near = float.NegativeInfinity;
        float far = float.PositiveInfinity;

        if (!SlabAxis(map.NodeMinX[i], map.NodeMaxX[i], origin.X, direction.X, ref near, ref far)
            || !SlabAxis(map.NodeMinY[i], map.NodeMaxY[i], origin.Y, direction.Y, ref near, ref far)
            || !SlabAxis(map.NodeMinZ[i], map.NodeMaxZ[i], origin.Z, direction.Z, ref near, ref far))
            return false;

        return far >= MathF.Max(near, 0f) && near < 1f - RayEpsilon;
    }

    private static bool SlabAxis(
        float minimum, float maximum, float position, float direction, ref float near, ref float far)
    {
        if (MathF.Abs(direction) < AxisEpsilon)
            return position >= minimum && position <= maximum;

        float inverse = 1f / direction;
        float a = (minimum - position) * inverse;
        float b = (maximum - position) * inverse;
        near = MathF.Max(near, MathF.Min(a, b));
        far = MathF.Min(far, MathF.Max(a, b));
        return true;
    }

    /// <summary>Möller–Trumbore over the packet's first <paramref name="count"/> lanes.</summary>
    private static bool PacketHit(Bvh8Map map, uint packetIndex, uint count, Vector3 origin, Vector3 direction)
    {
        int baseIndex = (int)(packetIndex * 8);
        for (int lane = 0; lane < count; lane++)
        {
            int i = baseIndex + lane;
            var edge1 = new Vector3(map.Edge1X[i], map.Edge1Y[i], map.Edge1Z[i]);
            var edge2 = new Vector3(map.Edge2X[i], map.Edge2Y[i], map.Edge2Z[i]);

            var p = Vector3.Cross(direction, edge2);
            float det = Vector3.Dot(edge1, p);
            if (!(MathF.Abs(det) > RayEpsilon))
                continue;
            float inverseDet = 1f / det;

            var toOrigin = origin - new Vector3(map.V0X[i], map.V0Y[i], map.V0Z[i]);
            float u = Vector3.Dot(toOrigin, p) * inverseDet;
            if (!(u >= 0f && u <= 1f))
                continue;

            var q = Vector3.Cross(toOrigin, edge1);
            float v = Vector3.Dot(direction, q) * inverseDet;
            if (!(v >= 0f && u + v <= 1f))
                continue;

            float distance = Vector3.Dot(edge2, q) * inverseDet;
            if (distance > RayEpsilon && distance < 1f - RayEpsilon)
                return true;
        }
        return false;
    }
}
