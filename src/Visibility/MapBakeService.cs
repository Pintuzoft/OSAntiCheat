namespace OSAntiCheat.Visibility;

/// <summary>
/// Owns the current map's bake for live detection. Loading runs on a background task at
/// map start (a large bake validates in ~100 ms, but the game thread never waits); until
/// it completes — or if no valid bake exists — <see cref="Current"/> stays null and geo
/// gating simply doesn't apply (detectors fall back to spotted-only behaviour, never off).
///
/// v1 trusts the morning bake cron for freshness and only verifies the bake's embedded map
/// name; the loaded source CRC is logged so any staleness is auditable after the fact.
/// </summary>
public sealed class MapBakeService
{
    private volatile Bvh8Map? _current;
    private int _generation;

    /// <summary>The validated bake for the active map, or null (missing/invalid/still loading).</summary>
    public Bvh8Map? Current => _current;

    /// <summary>
    /// Kicks off a background load of <c>{bakesDir}/{mapName}.bvh8</c>, replacing whatever was
    /// loaded before. <paramref name="log"/> receives one line describing the outcome.
    /// </summary>
    public void LoadFor(string mapName, string bakesDir, Action<string> log)
    {
        int generation = Interlocked.Increment(ref _generation);
        _current = null;
        var path = Path.Combine(bakesDir, mapName + ".bvh8");

        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    log($"bake missing for '{mapName}' ({path}) — geo gate inactive this map");
                    return;
                }
                var bake = Bvh8Format.Load(path);
                if (!string.Equals(bake.MapName, mapName, StringComparison.OrdinalIgnoreCase))
                {
                    log($"bake at {path} is for '{bake.MapName}', not '{mapName}' — geo gate inactive");
                    return;
                }
                // A later map change must not let a slow older load overwrite the newer state.
                if (Volatile.Read(ref _generation) == generation)
                {
                    _current = bake;
                    log($"bake loaded for '{mapName}': {bake.TriangleCount:N0} triangles, " +
                        $"source crc 0x{bake.SourceCrc32:x8} — geo gate active");
                }
            }
            catch (Exception exception) when (exception is Bvh8FormatException or IOException)
            {
                log($"bake load failed for '{mapName}': {exception.Message} — geo gate inactive");
            }
        });
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
    }
}
