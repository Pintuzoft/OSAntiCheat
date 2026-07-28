using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OSAntiCheat.Visibility;

/// <summary>
/// Loads and writes <c>.bvh8</c> bakes with the same validation rules as CS2FOW's loader:
/// exact magic/version/recipe, no unknown flags, zero reserved bytes, exact offsets and
/// file size, a single rooted tree with unique reachable nodes/packets, consistent depth
/// and triangle totals, and a matching payload CRC. Anything else throws
/// <see cref="Bvh8FormatException"/> — a bake is either fully valid or unusable.
/// </summary>
public static class Bvh8Format
{
    private const string Magic = "CS2FOW8\0";

    public static Bvh8Map Load(string path) => Parse(File.ReadAllBytes(path));

    public static Bvh8Map Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < Bvh8Map.HeaderSize)
            throw new Bvh8FormatException("BVH8 file is truncated");

        var header = file[..Bvh8Map.HeaderSize];
        if (!header[..8].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            throw new Bvh8FormatException("invalid BVH8 magic");
        if (ReadU32(header, 8) != Bvh8Map.FormatVersion
            || ReadU32(header, 12) != Bvh8Map.HeaderSize
            || ReadU32(header, 164) != Bvh8Map.RecipeVersion)
            throw new Bvh8FormatException("invalid BVH8 version or bake recipe");

        uint flags = ReadU32(header, 16);
        if ((flags & ~Bvh8Map.KnownFlags) != 0)
            throw new Bvh8FormatException("BVH8 contains unsupported flags");
        if (header[168..256].ContainsAnyExcept((byte)0))
            throw new Bvh8FormatException("BVH8 reserved header bytes are not zero");

        var nameBytes = header.Slice(32, 64);
        int nameEnd = nameBytes.IndexOf((byte)0);
        if (nameEnd <= 0)
            throw new Bvh8FormatException("invalid map name");
        string mapName = Encoding.ASCII.GetString(nameBytes[..nameEnd]);

        uint nodeCount = ReadU32(header, 120);
        uint packetCount = ReadU32(header, 124);
        uint triangleCount = ReadU32(header, 128);
        uint maxDepth = ReadU32(header, 132);
        if (nodeCount == 0 || packetCount == 0 || triangleCount == 0
            || maxDepth == 0 || maxDepth > Bvh8Map.MaxTreeDepth
            || triangleCount > (ulong)packetCount * 8)
            throw new Bvh8FormatException("invalid BVH8 counts or depth");

        var worldMin = ReadVec3(header, 96);
        var worldMax = ReadVec3(header, 108);
        if (!FiniteBounds(worldMin, worldMax))
            throw new Bvh8FormatException("invalid BVH8 world bounds");

        // Offsets are fully determined by the counts; both record sizes are multiples of
        // 32 so the align-up steps are structurally no-ops, but we mirror upstream anyway.
        ulong nodesOffset = AlignUp(Bvh8Map.HeaderSize, 32);
        ulong packetsOffset = AlignUp(nodesOffset + nodeCount * (ulong)Bvh8Map.NodeSize, 32);
        ulong fileSize = packetsOffset + packetCount * (ulong)Bvh8Map.PacketSize;
        if (ReadU64(header, 136) != nodesOffset || ReadU64(header, 144) != packetsOffset
            || ReadU64(header, 152) != fileSize || (ulong)file.Length != fileSize)
            throw new Bvh8FormatException("BVH8 offsets or file size are invalid");

        var nodeBytes = file.Slice((int)nodesOffset, (int)nodeCount * Bvh8Map.NodeSize);
        var packetBytes = file.Slice((int)packetsOffset, (int)packetCount * Bvh8Map.PacketSize);
        uint payloadCrc = ReadU32(header, 160);
        if (Crc32.Extend(Crc32.Compute(nodeBytes), packetBytes) != payloadCrc)
            throw new Bvh8FormatException("BVH8 payload CRC mismatch");

        var map = new Bvh8Map
        {
            MapName = mapName,
            Flags = flags,
            SourceCrc32 = ReadU32(header, 20),
            SourceSize = ReadU64(header, 24),
            WorldMin = worldMin,
            WorldMax = worldMax,
            TriangleCount = triangleCount,
            MaxDepth = maxDepth,
            PayloadCrc32 = payloadCrc,
            NodeMinX = new float[nodeCount * 8],
            NodeMinY = new float[nodeCount * 8],
            NodeMinZ = new float[nodeCount * 8],
            NodeMaxX = new float[nodeCount * 8],
            NodeMaxY = new float[nodeCount * 8],
            NodeMaxZ = new float[nodeCount * 8],
            Child = new uint[nodeCount * 8],
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

        for (int node = 0; node < nodeCount; node++)
        {
            var record = nodeBytes.Slice(node * Bvh8Map.NodeSize, Bvh8Map.NodeSize);
            ReadLanes(record, 0, map.NodeMinX, node);
            ReadLanes(record, 32, map.NodeMinY, node);
            ReadLanes(record, 64, map.NodeMinZ, node);
            ReadLanes(record, 96, map.NodeMaxX, node);
            ReadLanes(record, 128, map.NodeMaxY, node);
            ReadLanes(record, 160, map.NodeMaxZ, node);
            for (int lane = 0; lane < 8; lane++)
                map.Child[node * 8 + lane] = ReadU32(record, 192 + lane * 4);
        }

        for (int packet = 0; packet < packetCount; packet++)
        {
            var record = packetBytes.Slice(packet * Bvh8Map.PacketSize, Bvh8Map.PacketSize);
            ReadLanes(record, 0, map.V0X, packet);
            ReadLanes(record, 32, map.V0Y, packet);
            ReadLanes(record, 64, map.V0Z, packet);
            ReadLanes(record, 96, map.Edge1X, packet);
            ReadLanes(record, 128, map.Edge1Y, packet);
            ReadLanes(record, 160, map.Edge1Z, packet);
            ReadLanes(record, 192, map.Edge2X, packet);
            ReadLanes(record, 224, map.Edge2Y, packet);
            ReadLanes(record, 256, map.Edge2Z, packet);
        }

        ValidateTree(map);
        return map;
    }

    /// <summary>
    /// Serializes a map back to the exact on-disk layout (header CRC and offsets are
    /// recomputed). Exists for tests and future own-bake tooling; the result round-trips
    /// through <see cref="Parse"/>.
    /// </summary>
    public static byte[] Serialize(Bvh8Map map)
    {
        int nodeCount = map.NodeCount;
        int packetCount = map.PacketCount;
        ulong nodesOffset = AlignUp(Bvh8Map.HeaderSize, 32);
        ulong packetsOffset = AlignUp(nodesOffset + (ulong)nodeCount * Bvh8Map.NodeSize, 32);
        ulong fileSize = packetsOffset + (ulong)packetCount * Bvh8Map.PacketSize;

        var file = new byte[fileSize];
        var output = file.AsSpan();

        var nodeBytes = output.Slice((int)nodesOffset, nodeCount * Bvh8Map.NodeSize);
        for (int node = 0; node < nodeCount; node++)
        {
            var record = nodeBytes.Slice(node * Bvh8Map.NodeSize, Bvh8Map.NodeSize);
            WriteLanes(record, 0, map.NodeMinX, node);
            WriteLanes(record, 32, map.NodeMinY, node);
            WriteLanes(record, 64, map.NodeMinZ, node);
            WriteLanes(record, 96, map.NodeMaxX, node);
            WriteLanes(record, 128, map.NodeMaxY, node);
            WriteLanes(record, 160, map.NodeMaxZ, node);
            for (int lane = 0; lane < 8; lane++)
                BinaryPrimitives.WriteUInt32LittleEndian(record[(192 + lane * 4)..], map.Child[node * 8 + lane]);
        }

        var packetBytes = output.Slice((int)packetsOffset, packetCount * Bvh8Map.PacketSize);
        for (int packet = 0; packet < packetCount; packet++)
        {
            var record = packetBytes.Slice(packet * Bvh8Map.PacketSize, Bvh8Map.PacketSize);
            WriteLanes(record, 0, map.V0X, packet);
            WriteLanes(record, 32, map.V0Y, packet);
            WriteLanes(record, 64, map.V0Z, packet);
            WriteLanes(record, 96, map.Edge1X, packet);
            WriteLanes(record, 128, map.Edge1Y, packet);
            WriteLanes(record, 160, map.Edge1Z, packet);
            WriteLanes(record, 192, map.Edge2X, packet);
            WriteLanes(record, 224, map.Edge2Y, packet);
            WriteLanes(record, 256, map.Edge2Z, packet);
        }

        var header = output[..Bvh8Map.HeaderSize];
        Encoding.ASCII.GetBytes(Magic, header[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Bvh8Map.FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Bvh8Map.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], map.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], map.SourceCrc32);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], map.SourceSize);
        Encoding.ASCII.GetBytes(map.MapName, header[32..]);
        WriteVec3(header, 96, map.WorldMin);
        WriteVec3(header, 108, map.WorldMax);
        BinaryPrimitives.WriteUInt32LittleEndian(header[120..], (uint)nodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[124..], (uint)packetCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[128..], map.TriangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[132..], map.MaxDepth);
        BinaryPrimitives.WriteUInt64LittleEndian(header[136..], nodesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[144..], packetsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[152..], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[160..],
            Crc32.Extend(Crc32.Compute(nodeBytes), packetBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(header[164..], Bvh8Map.RecipeVersion);
        return file;
    }

    /// <summary>
    /// Ports upstream validate_bvh8: exactly one rooted tree, references only forward,
    /// every node/packet reachable with a single parent, and depth/triangle totals that
    /// match the header.
    /// </summary>
    private static void ValidateTree(Bvh8Map map)
    {
        int nodeCount = map.NodeCount;
        int packetCount = map.PacketCount;
        var visitedNodes = new bool[nodeCount];
        var visitedPackets = new bool[packetCount];
        var pending = new Stack<(uint Node, uint Depth)>();
        pending.Push((0u, 1u));
        visitedNodes[0] = true;
        uint reachedNodes = 0;
        uint reachedPackets = 0;
        uint maxDepth = 0;
        ulong triangles = 0;

        while (pending.Count > 0)
        {
            var (nodeIndex, depth) = pending.Pop();
            reachedNodes++;
            maxDepth = Math.Max(maxDepth, depth);
            if (depth > Bvh8Map.MaxTreeDepth)
                throw new Bvh8FormatException("BVH8 tree is too deep");

            for (int lane = 0; lane < 8; lane++)
            {
                uint reference = map.Child[nodeIndex * 8 + lane];
                if (reference == Bvh8Map.InvalidRef)
                    continue;

                int i = (int)(nodeIndex * 8) + lane;
                if (!FiniteBounds(
                        new Vector3(map.NodeMinX[i], map.NodeMinY[i], map.NodeMinZ[i]),
                        new Vector3(map.NodeMaxX[i], map.NodeMaxY[i], map.NodeMaxZ[i])))
                    throw new Bvh8FormatException("BVH8 contains invalid child bounds");

                if (Bvh8Map.IsLeafRef(reference))
                {
                    uint packet = Bvh8Map.LeafIndex(reference);
                    if (packet >= packetCount)
                        throw new Bvh8FormatException("BVH8 leaf reference is out of range");
                    if (visitedPackets[packet])
                        throw new Bvh8FormatException("BVH8 packet has more than one parent");
                    visitedPackets[packet] = true;
                    reachedPackets++;
                    triangles += Bvh8Map.LeafTriangleCount(reference);
                }
                else
                {
                    if (reference >= nodeCount || reference <= nodeIndex)
                        throw new Bvh8FormatException("BVH8 node reference is invalid");
                    if (visitedNodes[reference])
                        throw new Bvh8FormatException("BVH8 node has more than one parent");
                    visitedNodes[reference] = true;
                    pending.Push((reference, depth + 1));
                }
            }
        }

        if (reachedNodes != nodeCount || reachedPackets != packetCount)
            throw new Bvh8FormatException("BVH8 contains unreachable nodes or packets");
        if (triangles != map.TriangleCount)
            throw new Bvh8FormatException("BVH8 triangle count is inconsistent");
        if (maxDepth != map.MaxDepth)
            throw new Bvh8FormatException("BVH8 depth is inconsistent");
    }

    private static bool FiniteBounds(Vector3 minimum, Vector3 maximum)
        => float.IsFinite(minimum.X) && float.IsFinite(minimum.Y) && float.IsFinite(minimum.Z)
        && float.IsFinite(maximum.X) && float.IsFinite(maximum.Y) && float.IsFinite(maximum.Z)
        && minimum.X <= maximum.X && minimum.Y <= maximum.Y && minimum.Z <= maximum.Z;

    private static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static ulong ReadU64(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);

    private static Vector3 ReadVec3(ReadOnlySpan<byte> bytes, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]));

    private static void WriteVec3(Span<byte> bytes, int offset, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 4)..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 8)..], value.Z);
    }

    private static void ReadLanes(ReadOnlySpan<byte> record, int offset, float[] destination, int recordIndex)
    {
        for (int lane = 0; lane < 8; lane++)
            destination[recordIndex * 8 + lane] =
                BinaryPrimitives.ReadSingleLittleEndian(record[(offset + lane * 4)..]);
    }

    private static void WriteLanes(Span<byte> record, int offset, float[] source, int recordIndex)
    {
        for (int lane = 0; lane < 8; lane++)
            BinaryPrimitives.WriteSingleLittleEndian(
                record[(offset + lane * 4)..], source[recordIndex * 8 + lane]);
    }
}

public sealed class Bvh8FormatException(string message) : Exception(message);

/// <summary>Standard CRC-32 (reflected 0xEDB88320) — the variant CS2FOW bakes use.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes) => Extend(0, bytes);

    public static uint Extend(uint previous, ReadOnlySpan<byte> bytes)
    {
        uint value = ~previous;
        foreach (byte b in bytes)
            value = (value >> 8) ^ Table[(value ^ b) & 0xff];
        return ~value;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint entry = index;
            for (int bit = 0; bit < 8; bit++)
                entry = (entry >> 1) ^ (0xedb88320u & (0u - (entry & 1)));
            table[index] = entry;
        }
        return table;
    }
}
