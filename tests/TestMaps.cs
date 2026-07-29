using System.Numerics;
using OSAntiCheat.Visibility;

namespace OSAntiCheat.Tests;

/// <summary>Synthetic BVH8 maps shared by the format, raycaster, and occlusion tests.</summary>
internal static class TestMaps
{
    public static readonly (Vector3 V0, Vector3 V1, Vector3 V2)[] WallAtXZero =
    [
        (new(0, -100, -100), new(0, 100, -100), new(0, 100, 100)),
        (new(0, -100, -100), new(0, 100, 100), new(0, -100, 100)),
    ];

    /// <summary>
    /// Builds a one-node map: the root's first lanes each reference one leaf packet of
    /// up to 8 triangles. Enough structure to exercise traversal, packets, and lanes.
    /// </summary>
    public static Bvh8Map BuildMap(params (Vector3 V0, Vector3 V1, Vector3 V2)[][] packets)
    {
        int packetCount = packets.Length;
        var map = new Bvh8Map
        {
            MapName = "test_map",
            Flags = 0,
            SourceCrc32 = 0x12345678,
            SourceSize = 1000,
            WorldMin = new Vector3(-1000, -1000, -1000),
            WorldMax = new Vector3(1000, 1000, 1000),
            TriangleCount = (uint)packets.Sum(p => p.Length),
            MaxDepth = 1,
            PayloadCrc32 = 0, // recomputed by Serialize
            NodeMinX = new float[8],
            NodeMinY = new float[8],
            NodeMinZ = new float[8],
            NodeMaxX = new float[8],
            NodeMaxY = new float[8],
            NodeMaxZ = new float[8],
            Child = new uint[8],
            V0X = new float[packetCount * 8],
            V0Y = new float[packetCount * 8],
            V0Z = new float[packetCount * 8],
            Edge1X = new float[packetCount * 8],
            Edge1Y = new float[packetCount * 8],
            Edge1Z = new float[packetCount * 8],
            Edge2X = new float[packetCount * 8],
            Edge2Y = new float[packetCount * 8],
            Edge2Z = new float[packetCount * 8],
        };

        Array.Fill(map.Child, Bvh8Map.InvalidRef);
        for (int packet = 0; packet < packetCount; packet++)
        {
            var minimum = new Vector3(float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity);
            for (int lane = 0; lane < packets[packet].Length; lane++)
            {
                var (v0, v1, v2) = packets[packet][lane];
                int i = packet * 8 + lane;
                map.V0X[i] = v0.X;
                map.V0Y[i] = v0.Y;
                map.V0Z[i] = v0.Z;
                map.Edge1X[i] = v1.X - v0.X;
                map.Edge1Y[i] = v1.Y - v0.Y;
                map.Edge1Z[i] = v1.Z - v0.Z;
                map.Edge2X[i] = v2.X - v0.X;
                map.Edge2Y[i] = v2.Y - v0.Y;
                map.Edge2Z[i] = v2.Z - v0.Z;
                minimum = Vector3.Min(minimum, Vector3.Min(v0, Vector3.Min(v1, v2)));
                maximum = Vector3.Max(maximum, Vector3.Max(v0, Vector3.Max(v1, v2)));
            }
            map.NodeMinX[packet] = minimum.X;
            map.NodeMinY[packet] = minimum.Y;
            map.NodeMinZ[packet] = minimum.Z;
            map.NodeMaxX[packet] = maximum.X;
            map.NodeMaxY[packet] = maximum.Y;
            map.NodeMaxZ[packet] = maximum.Z;
            map.Child[packet] = Bvh8Map.MakeLeafRef((uint)packet, (uint)packets[packet].Length);
        }
        return map;
    }
}
