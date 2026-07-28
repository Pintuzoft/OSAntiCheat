using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OSAntiCheat.Visibility;

// Inspect and query CS2FOW .bvh8 map bakes: the static-geometry visibility oracle
// used by the demo-analysis tools. See docs/visibility-oracle.md.

if (args.Length < 2)
{
    Console.Error.WriteLine(
        """
        usage: osac-visoracle info <bake.bvh8>
               osac-visoracle ray <bake.bvh8> <x1> <y1> <z1> <x2> <y2> <z2>
               osac-visoracle bench <bake.bvh8> [queries]
        """);
    return 2;
}

var command = args[0];
Bvh8Map map;
var loadTimer = Stopwatch.StartNew();
try
{
    map = Bvh8Format.Load(args[1]);
}
catch (Exception exception) when (exception is Bvh8FormatException or IOException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
loadTimer.Stop();

switch (command)
{
    case "info":
        Console.WriteLine($"map:        {map.MapName}");
        Console.WriteLine($"flags:      0x{map.Flags:x}");
        Console.WriteLine($"source:     crc32 0x{map.SourceCrc32:x8}, {map.SourceSize} bytes");
        Console.WriteLine($"world min:  {map.WorldMin}");
        Console.WriteLine($"world max:  {map.WorldMax}");
        Console.WriteLine($"nodes:      {map.NodeCount}");
        Console.WriteLine($"packets:    {map.PacketCount}");
        Console.WriteLine($"triangles:  {map.TriangleCount}");
        Console.WriteLine($"max depth:  {map.MaxDepth}");
        Console.WriteLine($"loaded in:  {loadTimer.ElapsedMilliseconds} ms (validated, payload CRC ok)");
        return 0;

    case "ray" when args.Length == 8:
    {
        var values = new float[6];
        for (int index = 0; index < 6; index++)
        {
            if (!float.TryParse(args[index + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
            {
                Console.Error.WriteLine($"error: '{args[index + 2]}' is not a number");
                return 2;
            }
        }
        var origin = new Vector3(values[0], values[1], values[2]);
        var target = new Vector3(values[3], values[4], values[5]);
        var hit = Bvh8Raycaster.SegmentBlocked(map, origin, target);
        Console.WriteLine(hit.Blocked ? $"BLOCKED (packet {hit.PacketIndex})" : "CLEAR");
        return 0;
    }

    case "bench":
    {
        int queries = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : 100_000;
        // Deterministic pseudo-random segments spanning the playable volume.
        var random = new Random(1337);
        var extent = map.WorldMax - map.WorldMin;
        Vector3 RandomPoint() => map.WorldMin + extent * new Vector3(
            (float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());

        var segments = new (Vector3 Origin, Vector3 Target)[queries];
        for (int index = 0; index < queries; index++)
            segments[index] = (RandomPoint(), RandomPoint());

        int blocked = 0;
        var timer = Stopwatch.StartNew();
        foreach (var (origin, target) in segments)
        {
            if (Bvh8Raycaster.SegmentBlocked(map, origin, target).Blocked)
                blocked++;
        }
        timer.Stop();
        double perQuery = timer.Elapsed.TotalMicroseconds / queries;
        Console.WriteLine($"{queries} queries in {timer.ElapsedMilliseconds} ms "
            + $"({perQuery:f2} us/query), {blocked} blocked ({100.0 * blocked / queries:f1}%)");
        return 0;
    }

    default:
        Console.Error.WriteLine($"error: unknown command '{command}' or wrong argument count");
        return 2;
}
