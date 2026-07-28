using System.Numerics;

namespace OSAntiCheat.Visibility;

/// <summary>
/// An immutable, validated CS2FOW <c>.bvh8</c> map bake: static collision triangles in an
/// 8-wide BVH, used to answer "does a straight segment cross baked geometry".
///
/// C# port of the reader in CS2FOW (github.com/karola3vax/CS2FOW, MIT) pinned to format
/// version 3 / bake recipe 1 — the loader rejects anything else, exactly like upstream.
/// Payload is stored lane-major (index = record * 8 + lane) matching the file's SoA layout.
///
/// Bake files themselves are derived from Valve map data and carry their own DATA_NOTICE;
/// they are runtime assets and must never be committed to this repository.
/// </summary>
public sealed class Bvh8Map
{
    public const uint FormatVersion = 3;
    public const uint RecipeVersion = 1;
    public const uint FlagNestedMapVpk = 1u << 0;
    public const uint KnownFlags = FlagNestedMapVpk;
    public const uint InvalidRef = 0xffffffffu;
    public const uint LeafRefBit = 0x80000000u;
    public const uint LeafIndexMask = 0x0fffffffu;
    public const uint MaxTreeDepth = 64;
    public const int HeaderSize = 256;
    public const int NodeSize = 224;   // 6 * 8 floats bounds + 8 uint children
    public const int PacketSize = 288; // 9 * 8 floats: v0, edge1, edge2

    public required string MapName { get; init; }
    public required uint Flags { get; init; }
    public required uint SourceCrc32 { get; init; }
    public required ulong SourceSize { get; init; }
    public required Vector3 WorldMin { get; init; }
    public required Vector3 WorldMax { get; init; }
    public required uint TriangleCount { get; init; }
    public required uint MaxDepth { get; init; }
    public required uint PayloadCrc32 { get; init; }

    public int NodeCount => Child.Length / 8;
    public int PacketCount => V0X.Length / 8;

    // BVH nodes: per-child AABBs and child references. child == InvalidRef is an empty
    // lane; LeafRefBit marks a triangle-packet leaf (see LeafIndex/LeafTriangleCount).
    public required float[] NodeMinX { get; init; }
    public required float[] NodeMinY { get; init; }
    public required float[] NodeMinZ { get; init; }
    public required float[] NodeMaxX { get; init; }
    public required float[] NodeMaxY { get; init; }
    public required float[] NodeMaxZ { get; init; }
    public required uint[] Child { get; init; }

    // Triangle packets, Möller–Trumbore form: v0 plus the two edge vectors.
    public required float[] V0X { get; init; }
    public required float[] V0Y { get; init; }
    public required float[] V0Z { get; init; }
    public required float[] Edge1X { get; init; }
    public required float[] Edge1Y { get; init; }
    public required float[] Edge1Z { get; init; }
    public required float[] Edge2X { get; init; }
    public required float[] Edge2Y { get; init; }
    public required float[] Edge2Z { get; init; }

    public static bool IsLeafRef(uint reference)
        => reference != InvalidRef && (reference & LeafRefBit) != 0;

    public static uint LeafIndex(uint reference) => reference & LeafIndexMask;

    public static uint LeafTriangleCount(uint reference) => ((reference >> 28) & 7u) + 1u;

    public static uint MakeLeafRef(uint packetIndex, uint triangleCount)
        => LeafRefBit | ((triangleCount - 1u) << 28) | packetIndex;
}
