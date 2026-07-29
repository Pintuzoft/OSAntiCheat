using System.Numerics;
using OSAntiCheat.Visibility;
using Xunit;

namespace OSAntiCheat.Tests;

public class Bvh8Tests
{
    private static (Vector3, Vector3, Vector3)[] WallAtXZero => TestMaps.WallAtXZero;

    private static Bvh8Map BuildMap(params (Vector3 V0, Vector3 V1, Vector3 V2)[][] packets)
        => TestMaps.BuildMap(packets);

    private static Bvh8Map RoundTrip(Bvh8Map map) => Bvh8Format.Parse(Bvh8Format.Serialize(map));

    [Fact]
    public void RoundTrip_PreservesHeaderAndGeometry()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.Equal("test_map", map.MapName);
        Assert.Equal(0x12345678u, map.SourceCrc32);
        Assert.Equal(1000ul, map.SourceSize);
        Assert.Equal(2u, map.TriangleCount);
        Assert.Equal(1u, map.MaxDepth);
        Assert.Equal(1, map.NodeCount);
        Assert.Equal(1, map.PacketCount);
        Assert.Equal(-100f, map.V0Y[0]);
    }

    [Fact]
    public void SegmentThroughWall_IsBlocked()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        var hit = Bvh8Raycaster.SegmentBlocked(map, new Vector3(-50, 0, 0), new Vector3(50, 0, 0));
        Assert.True(hit.Blocked);
        Assert.Equal(0u, hit.PacketIndex);
    }

    [Fact]
    public void DiagonalThroughWall_IsBlocked()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.True(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, -50, -50), new Vector3(50, 50, 50)).Blocked);
    }

    [Fact]
    public void SegmentPastWallEdge_IsClear()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.False(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 200, 0), new Vector3(50, 200, 0)).Blocked);
    }

    [Fact]
    public void SegmentEndingBeforeWall_IsClear()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.False(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 0, 0), new Vector3(-10, 0, 0)).Blocked);
    }

    [Fact]
    public void EndpointOnWall_IsClear_OpenSegmentSemantics()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.False(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(0, 0, 0), new Vector3(50, 0, 0)).Blocked);
    }

    [Fact]
    public void ZeroLengthSegment_IsClear()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        Assert.False(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 0, 0), new Vector3(-50, 0, 0)).Blocked);
    }

    [Fact]
    public void AxisAlignedZeroDirection_UsesInsideTest()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        // Segment varies only in X at y=0, z=50: crosses the wall inside its bounds.
        Assert.True(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 0, 50), new Vector3(50, 0, 50)).Blocked);
        // Same segment shifted to z=150: outside the wall AABB on a zero-direction axis.
        Assert.False(Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 0, 150), new Vector3(50, 0, 150)).Blocked);
    }

    [Fact]
    public void CachedPacket_ShortCircuitsToSameResult()
    {
        var map = RoundTrip(BuildMap(WallAtXZero));
        var first = Bvh8Raycaster.SegmentBlocked(map, new Vector3(-50, 0, 0), new Vector3(50, 0, 0));
        var again = Bvh8Raycaster.SegmentBlocked(
            map, new Vector3(-50, 1, 0), new Vector3(50, 1, 0), first.PacketIndex);
        Assert.True(again.Blocked);
        Assert.Equal(first.PacketIndex, again.PacketIndex);
    }

    [Fact]
    public void TwoPackets_EachBlocksItsOwnWall()
    {
        (Vector3, Vector3, Vector3)[] wallAtY50 =
        [
            (new(-100, 50, -100), new(100, 50, -100), new(100, 50, 100)),
            (new(-100, 50, -100), new(100, 50, 100), new(-100, 50, 100)),
        ];
        var map = RoundTrip(BuildMap(WallAtXZero, wallAtY50));
        var hitX = Bvh8Raycaster.SegmentBlocked(map, new Vector3(-50, 0, 0), new Vector3(50, 0, 0));
        var hitY = Bvh8Raycaster.SegmentBlocked(map, new Vector3(10, 0, 0), new Vector3(10, 100, 0));
        Assert.True(hitX.Blocked);
        Assert.True(hitY.Blocked);
        Assert.Equal(0u, hitX.PacketIndex);
        Assert.Equal(1u, hitY.PacketIndex);
    }

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var file = Bvh8Format.Serialize(BuildMap(WallAtXZero));
        file[Bvh8Map.HeaderSize + 17] ^= 0x01;
        var error = Assert.Throws<Bvh8FormatException>(() => Bvh8Format.Parse(file));
        Assert.Contains("CRC", error.Message);
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        var file = Bvh8Format.Serialize(BuildMap(WallAtXZero));
        file[8] = 99;
        Assert.Throws<Bvh8FormatException>(() => Bvh8Format.Parse(file));
    }

    [Fact]
    public void TruncatedFile_IsRejected()
    {
        var file = Bvh8Format.Serialize(BuildMap(WallAtXZero));
        Assert.Throws<Bvh8FormatException>(() => Bvh8Format.Parse(file.AsSpan()[..^32]));
    }

    [Fact]
    public void NonZeroReservedBytes_AreRejected()
    {
        var file = Bvh8Format.Serialize(BuildMap(WallAtXZero));
        file[200] = 1;
        var error = Assert.Throws<Bvh8FormatException>(() => Bvh8Format.Parse(file));
        Assert.Contains("reserved", error.Message);
    }

    [Fact]
    public void InconsistentTriangleCount_IsRejected()
    {
        var file = Bvh8Format.Serialize(BuildMap(WallAtXZero));
        // The payload CRC covers only nodes and packets, so a lying header triangle
        // count must be caught by the tree walk, not the checksum.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(128), 3);
        var error = Assert.Throws<Bvh8FormatException>(() => Bvh8Format.Parse(file));
        Assert.Contains("triangle count", error.Message);
    }
}
