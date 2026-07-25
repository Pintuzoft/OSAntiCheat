using System.Numerics;
using DemoFile;
using DemoFile.Game.Cs;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;

// Parameter sweep against a demo containing a KNOWN cheater plus legit players in the same
// match — the controlled experiment. Parses the demo once into a trace of per-poll observations,
// then replays that trace through wallhack.track under thousands of parameter combinations.
//
// Metric is signals per alive-minute, not total score: score accumulates, so a 20-minute player
// out-scores a 2-minute player regardless of behaviour. Rate removes that confound.
//
// The question it answers: does ANY configuration make the cheater stand out from the controls?
// If none does, the approach doesn't work and no amount of tuning will save it.
//
// usage: osac-sweep <demo.dem>:<cheaterSteamId> [more...]
//
// Pass several demo:cheater pairs. A config that happens to rank ONE cheater first is chance;
// one that ranks EVERY cheater first, each against their own match's legit players, is evidence.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: osac-sweep <demo.dem>:<cheaterSteamId> [more...]");
    return 1;
}

// --eval bc,ff,br,ts,at  => don't sweep; run ONE config over every demo and print every
// player-session ranked, cheaters marked. This is the population view: ranking first inside
// your own match means nothing if legit players elsewhere score higher.
Combo? evalCombo = null;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--eval" && i + 1 < args.Length)
    {
        var p = args[++i].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        evalCombo = new Combo(p[0], p[1], p[2], p[3], p[4]);
        continue;
    }
    inputs.Add(args[i]);
}

var cases = new List<Case>();
foreach (var arg in inputs)
{
    // "demo.dem:steamid" = known cheater present; "demo.dem" = assumed-clean demo.
    int sep = arg.LastIndexOf(':');
    if (sep > 1 && ulong.TryParse(arg[(sep + 1)..], out var cid))
        cases.Add(await LoadCase(arg[..sep], cid));
    else
        cases.Add(await LoadCase(arg, 0));
}

if (evalCombo is { } ec)
{
    Console.WriteLine($"\n=== Every player-session under config " +
                      $"bearing={ec.BearingChange:F0} follow={ec.FollowFraction:F1} rate={ec.BearingRate:F0} " +
                      $"track={ec.TrackSeconds:F1} aim={ec.AimThreshold:F0} ===\n");

    var all = new List<(string name, ulong sid, float rate, float minutes, int signals, bool cheater, string demo)>();
    foreach (var k in cases)
        foreach (var (slot, obs) in k.BySlot)
        {
            var det = new WallhackDetector(ec.TrackSeconds, 0f, ec.BearingChange, ec.FollowFraction, ec.BearingRate);
            int signals = 0;
            foreach (var o in obs)
            {
                WallhackDetector.WallTarget? t = (o.EnemyId >= 0 && o.AimErr <= ec.AimThreshold)
                    ? new WallhackDetector.WallTarget(o.EnemyId, o.EnemyPos, o.AimErr, o.ViewYaw, o.BearingYaw)
                    : null;
                if (det.Observe(slot, o.Time, t) is not null) signals++;
            }
            float minutes = k.AlivePolls.GetValueOrDefault(slot) * 0.05f / 60f;
            if (minutes < 0.5f) continue; // sub-30s cameos: rate is meaningless
            all.Add((k.Names.GetValueOrDefault(slot, "?"), k.SteamIds.GetValueOrDefault(slot),
                     signals / minutes, minutes, signals, slot == k.CheaterSlot, Path.GetFileName(k.Demo)));
        }

    Console.WriteLine("  rate/min  signals  min   player                    demo                       flag");
    foreach (var r in all.OrderByDescending(r => r.rate))
        Console.WriteLine($"  {r.rate,8:F2}  {r.signals,7}  {r.minutes,4:F1}  {r.name,-24}  {r.demo,-26} {(r.cheater ? "<<< BANNED CHEATER" : "")}");

    var cheats = all.Where(r => r.cheater).ToList();
    var legits = all.Where(r => !r.cheater).ToList();
    if (cheats.Count > 0 && legits.Count > 0)
    {
        int above = legits.Count(l => l.rate >= cheats.Min(c => c.rate));
        Console.WriteLine($"\n  legit sessions scoring >= the LOWEST cheater: {above} / {legits.Count}");
        Console.WriteLine($"  cheater rates: {string.Join(", ", cheats.OrderByDescending(c => c.rate).Select(c => $"{c.name}={c.rate:F2}"))}");
        Console.WriteLine($"  legit  max/median: {legits.Max(l => l.rate):F2} / {legits.OrderBy(l => l.rate).ElementAt(legits.Count / 2).rate:F2}");
    }
    return 0;
}

cases = cases.Where(c => c.CheaterSlot >= 0).ToList();
if (cases.Count == 0) { Console.Error.WriteLine("no cheater cases to sweep"); return 1; }

// ---- 2. sweep every config across every case ----
float[] bearingChanges = { 5f, 10f, 15f, 20f, 30f };
float[] followFractions = { 0.3f, 0.4f, 0.5f, 0.6f, 0.7f };
float[] bearingRates = { 0f, 5f, 10f, 15f };
float[] trackSeconds = { 0.2f, 0.4f, 0.6f, 1.0f };
float[] aimThresholds = { 3f, 5f, 8f, 12f };

var combos = new List<Combo>();
foreach (float bc in bearingChanges)
foreach (float ff in followFractions)
foreach (float br in bearingRates)
foreach (float ts in trackSeconds)
foreach (float at in aimThresholds)
    combos.Add(new Combo(bc, ff, br, ts, at));

Console.WriteLine($"Sweeping {combos.Count:N0} configs across {cases.Count} case(s)...\n");

var scored = new List<MultiResult>();
foreach (var c in combos)
{
    var ranks = new List<int>();
    var margins = new List<float>();
    bool anySignal = false;

    foreach (var kase in cases)
    {
        var (rank, cheatRate, maxControl) = Evaluate(kase, c);
        ranks.Add(rank);
        margins.Add(cheatRate / MathF.Max(maxControl, 0.0001f));
        if (cheatRate > 0f) anySignal = true;
    }

    scored.Add(new MultiResult(c, ranks.ToArray(), margins.ToArray(), anySignal));
}

var allFirst = scored.Where(r => r.Ranks.All(x => x == 1) && r.AnySignal).ToList();

Console.WriteLine($"Configs ranking the cheater #1 in EVERY case: {allFirst.Count} / {scored.Count}\n");
if (allFirst.Count > 0)
{
    Console.WriteLine("  bearing follow rate track aim | ranks        worst-margin");
    foreach (var r in allFirst.OrderByDescending(r => r.Margins.Min()).Take(15))
        Console.WriteLine($"  {r.C.BearingChange,7:F0} {r.C.FollowFraction,6:F1} {r.C.BearingRate,4:F0} {r.C.TrackSeconds,5:F1} {r.C.AimThreshold,3:F0} | " +
                          $"{string.Join(",", r.Ranks),-12} {r.Margins.Min(),8:F2}x");
}
else
{
    Console.WriteLine("NO config ranks every cheater first. Best by average rank:");
    Console.WriteLine("  bearing follow rate track aim | ranks        avg");
    foreach (var r in scored.Where(r => r.AnySignal).OrderBy(r => r.Ranks.Average()).Take(15))
        Console.WriteLine($"  {r.C.BearingChange,7:F0} {r.C.FollowFraction,6:F1} {r.C.BearingRate,4:F0} {r.C.TrackSeconds,5:F1} {r.C.AimThreshold,3:F0} | " +
                          $"{string.Join(",", r.Ranks),-12} {r.Ranks.Average(),5:F1}");
}

Console.WriteLine($"\nPer-case cheater playtime (tiny samples = noisy rates):");
foreach (var k in cases)
    Console.WriteLine($"  {k.CheaterName,-22} {k.AlivePolls.GetValueOrDefault(k.CheaterSlot) * 0.05f / 60f,5:F1} alive-min in {Path.GetFileName(k.Demo)}");

return 0;

static (int rank, float cheatRate, float maxControl) Evaluate(Case k, Combo c)
{
    var rates = new Dictionary<int, float>();
    foreach (var (slot, obs) in k.BySlot)
    {
        var det = new WallhackDetector(c.TrackSeconds, 0f, c.BearingChange, c.FollowFraction, c.BearingRate);
        int signals = 0;
        foreach (var o in obs)
        {
            WallhackDetector.WallTarget? t = (o.EnemyId >= 0 && o.AimErr <= c.AimThreshold)
                ? new WallhackDetector.WallTarget(o.EnemyId, o.EnemyPos, o.AimErr, o.ViewYaw, o.BearingYaw)
                : null;
            if (det.Observe(slot, o.Time, t) is not null) signals++;
        }
        float minutes = k.AlivePolls.GetValueOrDefault(slot) * 0.05f / 60f;
        rates[slot] = minutes > 0.5f ? signals / minutes : 0f;
    }

    float cheat = rates.GetValueOrDefault(k.CheaterSlot);
    var controls = rates.Where(kv => kv.Key != k.CheaterSlot).Select(kv => kv.Value).ToList();
    float maxControl = controls.Count > 0 ? controls.Max() : 0f;
    int rank = 1 + controls.Count(x => x > cheat);
    return (rank, cheat, maxControl);
}

static async Task<Case> LoadCase(string demoPath, ulong cheaterId)
{
    Console.WriteLine($"Parsing {Path.GetFileName(demoPath)} once into a trace...");

    var trace = new List<Obs>(1 << 20);
    var alivePolls = new Dictionary<int, int>();
    var names = new Dictionary<int, string>();
    var steamIds = new Dictionary<int, ulong>();

    const float WidestCone = 25f; // nearest unspotted enemy up to here; per-config cone filters later
    var demo = new CsDemoParser();
    float last = -1f;
    float Now() => demo.CurrentDemoTick.Value / (float)Math.Max(1, CsDemoParser.TickRate);

    demo.OnCommandFinishPersistent = () =>
    {
    float now = Now();
    if (now - last < 0.05f) return;
    last = now;

    var players = demo.Players.ToList();
    foreach (var observer in players)
    {
        if (!observer.PawnIsAlive || observer.SteamID == 0) continue;
        var team = observer.CSTeamNum;
        if (team != CSTeamNumber.Terrorist && team != CSTeamNumber.CounterTerrorist) continue;
        var pawn = observer.PlayerPawn;
        if (pawn is null) continue;

        int slot = (int)observer.EntityIndex.Value - 1;
        if (slot < 0) continue;
        names[slot] = observer.PlayerName ?? $"slot {slot}";
        steamIds[slot] = observer.SteamID;
        alivePolls[slot] = alivePolls.GetValueOrDefault(slot) + 1;

        var eye = new Vector3(pawn.Origin.X, pawn.Origin.Y, pawn.Origin.Z + 64f);
        var angles = new ViewAngles(pawn.EyeAngles.Pitch, pawn.EyeAngles.Yaw, pawn.EyeAngles.Roll);

        // Nearest UNSPOTTED enemy to the crosshair (the nearest one is the same regardless of
        // which cone we later apply, so one trace serves every aim-threshold in the sweep).
        float bestErr = WidestCone;
        Vector3 bestPos = default;
        int bestId = -1;
        float bestBearing = 0f;

        foreach (var enemy in players)
        {
            if (ReferenceEquals(enemy, observer) || !enemy.PawnIsAlive) continue;
            if (enemy.CSTeamNum == team) continue;
            if (enemy.CSTeamNum != CSTeamNumber.Terrorist && enemy.CSTeamNum != CSTeamNumber.CounterTerrorist) continue;
            var ep = enemy.PlayerPawn;
            if (ep is null) continue;

            var mask = ep.EntitySpottedState.SpottedByMask;
            if (mask is null || mask.Length <= slot / 32) continue;
            if ((mask[slot / 32] & (1u << (slot % 32))) != 0) continue; // spotted => not a candidate

            var feet = new Vector3(ep.Origin.X, ep.Origin.Y, ep.Origin.Z);
            float err = OSAntiCheat.Detection.Geometry.NearestBodyAimError(eye, angles, feet);
            if (err < bestErr)
            {
                bestErr = err;
                bestPos = feet;
                bestId = (int)enemy.EntityIndex.Value;
                bestBearing = MathF.Atan2(feet.Y - eye.Y, feet.X - eye.X) * (180f / MathF.PI);
            }
        }

        trace.Add(new Obs(slot, now, bestId, bestPos, bestErr, angles.Yaw, bestBearing));
    }
    };

    var reader = DemoFileReader.Create(demo, File.OpenRead(demoPath));
    await reader.ReadAllAsync();

    int cheaterSlot = -1;
    if (cheaterId != 0)
    {
        foreach (var (slot, sid) in steamIds)
            if (sid == cheaterId) cheaterSlot = slot;
        if (cheaterSlot < 0)
            throw new InvalidOperationException($"cheater {cheaterId} not in {Path.GetFileName(demoPath)}");
    }

    var bySlot = trace.GroupBy(o => o.Slot).ToDictionary(g => g.Key, g => g.OrderBy(o => o.Time).ToArray());
    Console.WriteLine($"  {trace.Count:N0} obs, {alivePolls.Count} players" +
                      (cheaterSlot >= 0 ? $", cheater = {names[cheaterSlot]}" : ""));

    return new Case(demoPath, cheaterSlot, cheaterSlot >= 0 ? names[cheaterSlot] : "", bySlot, alivePolls, names, steamIds);
}

internal readonly record struct Obs(
    int Slot, float Time, int EnemyId, Vector3 EnemyPos, float AimErr, float ViewYaw, float BearingYaw);

internal readonly record struct Combo(
    float BearingChange, float FollowFraction, float BearingRate, float TrackSeconds, float AimThreshold);

internal sealed record Case(
    string Demo, int CheaterSlot, string CheaterName,
    Dictionary<int, Obs[]> BySlot, Dictionary<int, int> AlivePolls,
    Dictionary<int, string> Names, Dictionary<int, ulong> SteamIds);

internal sealed record MultiResult(Combo C, int[] Ranks, float[] Margins, bool AnySignal);
