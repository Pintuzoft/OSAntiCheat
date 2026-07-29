using System.Numerics;
using OSAntiCheat.Visibility;
using Xunit;

namespace OSAntiCheat.Tests;

public class BodyOcclusionTests
{
    private static Bvh8Map Wall() => Bvh8Format.Parse(Bvh8Format.Serialize(TestMaps.BuildMap(TestMaps.WallAtXZero)));

    [Fact]
    public void FullyOccludedBody_AllSamplesBlocked()
    {
        var map = Wall();
        // Enemy fully behind the wall: every sample height (feet+4 .. feet+60±shoulder)
        // crosses the wall plane well inside its extent.
        uint cache = Bvh8Map.InvalidRef;
        Assert.True(BodyOcclusion.AllSamplesBlocked(
            map, new Vector3(-50, 0, -20), new Vector3(50, 0, -50), ref cache));
        Assert.NotEqual(Bvh8Map.InvalidRef, cache); // blocking packet cached for the next poll
    }

    [Fact]
    public void HeadAboveWall_IsNotBlocked()
    {
        var map = Wall();
        // Enemy right behind the wall with the observer far away: the standing-head sample
        // (feet z=90 -> z=150) crosses the x=0 plane at z~147, above the wall top (z=100),
        // while the ankle sample crosses at z~94, inside it. One clear sample must veto
        // the whole verdict.
        uint cache = Bvh8Map.InvalidRef;
        Assert.False(BodyOcclusion.AllSamplesBlocked(
            map, new Vector3(-200, 0, 95), new Vector3(10, 0, 90), ref cache));
    }

    [Fact]
    public void CachedPacket_GivesSameVerdictOnRepeat()
    {
        var map = Wall();
        uint cache = Bvh8Map.InvalidRef;
        Assert.True(BodyOcclusion.AllSamplesBlocked(
            map, new Vector3(-50, 0, -20), new Vector3(50, 0, -50), ref cache));
        uint firstCache = cache;
        Assert.True(BodyOcclusion.AllSamplesBlocked(
            map, new Vector3(-50, 1, -20), new Vector3(50, 1, -50), ref cache));
        Assert.Equal(firstCache, cache);
    }
}

public class MapBakeServiceTests
{
    private static string WriteBake(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test_map.bvh8");
        File.WriteAllBytes(path, Bvh8Format.Serialize(TestMaps.BuildMap(TestMaps.WallAtXZero)));
        return directory;
    }

    private static Bvh8Map? WaitForLoad(MapBakeService service, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (service.Current is null && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        return service.Current;
    }

    [Fact]
    public void LoadsMatchingBake()
    {
        var dir = WriteBake(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var service = new MapBakeService();
        var log = new List<string>();
        service.LoadFor("test_map", dir, log.Add);
        var bake = WaitForLoad(service);
        Assert.NotNull(bake);
        Assert.Equal("test_map", bake!.MapName);
        Assert.Contains(log, line => line.Contains("geo gate active"));
    }

    [Fact]
    public void MissingBake_StaysNullAndLogs()
    {
        var service = new MapBakeService();
        var log = new List<string>();
        service.LoadFor("no_such_map", Path.GetTempPath(), line => { lock (log) log.Add(line); });
        Assert.Null(WaitForLoad(service, 2000));
        lock (log) Assert.Contains(log, line => line.Contains("bake missing"));
    }

    [Fact]
    public void MapNameMismatch_IsRejected()
    {
        var dir = WriteBake(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        // A bake whose embedded map name differs from the requested map must not activate.
        File.Move(Path.Combine(dir, "test_map.bvh8"), Path.Combine(dir, "other_map.bvh8"));
        var service = new MapBakeService();
        var log = new List<string>();
        service.LoadFor("other_map", dir, line => { lock (log) log.Add(line); });
        Assert.Null(WaitForLoad(service, 2000));
        lock (log) Assert.Contains(log, line => line.Contains("not 'other_map'"));
    }
}
