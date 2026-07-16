using System.Globalization;
using System.Numerics;
using DemoFile;
using DemoFile.Game.Cs;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;

// Offline calibration harness: replays CS2 demos through the SAME detector code the live plugin
// runs, reporting each player's peak suspicion score. Point it at one demo or a whole directory.
//
// Why: it gives ground truth without anyone having to cheat. Replay a corpus of ordinary demos to
// see the real score distribution (the population baseline), then cross-reference the SteamIDs
// against Steam's ban API — anyone VAC-banned since is a labelled cheater. If the banned and
// non-banned distributions don't separate, the detector doesn't work, and we'd know it.

// ~1.5s back at 64 tick. Long enough that an enemy has moved somewhere else, short enough to fit
// the tracker and to stay inside the same engagement. Compile-time constants, so ReplayOne reads
// them fine either way - but they sat after a return, which is how a reader learns to skim
// warnings, and skimmed warnings are how three broken measurements shipped today.
const int NullLagIndex = 96;
const float UnseenAimDeg = 5f;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: osac-replay <demo.dem | directory> [--poll 0.05] [--csv out.csv]");
    return 1;
}

string path = args[0];
float pollInterval = 0.05f;
string? csvPath = null;
string? shotsPath = null;
string? since = null, until = null;
string? failLog = null;
int limit = 0;   // sanity-check a sample before committing an hour to the whole archive
int jobs = Math.Max(1, Environment.ProcessorCount - 2); // parsing is CPU-bound; leave a couple free
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--poll" && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        pollInterval = p;
    if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[i + 1];
    if (args[i] == "--jobs" && i + 1 < args.Length && int.TryParse(args[i + 1], out var j)) jobs = Math.Max(1, j);
    if (args[i] == "--shots" && i + 1 < args.Length) shotsPath = args[i + 1];
    if (args[i] == "--since" && i + 1 < args.Length) since = args[i + 1];
    if (args[i] == "--until" && i + 1 < args.Length) until = args[i + 1];
    if (args[i] == "--fail-log" && i + 1 < args.Length) failLog = args[i + 1];
    if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var L)) limit = L;
}

// Archives are stored gzipped (1.7 TB of them) — read .dem.gz directly rather than making
// anyone unpack the lot first.
var demoFiles = Directory.Exists(path)
    ? Directory.EnumerateFiles(path, "*.dem*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dem.gz", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f).ToList()
    : File.Exists(path) ? new List<string> { path } : new List<string>();

// Pick a demo's date without opening it. Archive names vary by server config — the date may be
// at the front, after the map name, or absent entirely — so look for a plausible YYYYMMDD run
// anywhere in the name and fall back to the file's mtime. Silently keeping unparseable files is
// how a "last 6 months" run quietly replayed all 17097 demos, so report the fallback count.
static string? DateFromName(string name)
{
    for (int i = 0; i + 8 <= name.Length; i++)
    {
        if (!char.IsDigit(name[i]) || (i > 0 && char.IsDigit(name[i - 1]))) continue;
        var d = name.AsSpan(i, 8);
        bool digits = true;
        foreach (var c in d) if (!char.IsDigit(c)) { digits = false; break; }
        if (!digits) continue;
        int y = int.Parse(d[..4]), m = int.Parse(d.Slice(4, 2)), dd = int.Parse(d.Slice(6, 2));
        if (y >= 2012 && y <= 2100 && m >= 1 && m <= 12 && dd >= 1 && dd <= 31) return d.ToString();
    }
    return null;
}

if (since is not null || until is not null)
{
    int before = demoFiles.Count, fromMtime = 0;
    demoFiles = demoFiles.Where(f =>
    {
        var d = DateFromName(Path.GetFileName(f));
        if (d is null)
        {
            try { d = File.GetLastWriteTime(f).ToString("yyyyMMdd"); fromMtime++; }
            catch { return true; }        // unreadable: keep it and let the replay report the error
        }
        return (since is null || string.CompareOrdinal(d, since) >= 0)
            && (until is null || string.CompareOrdinal(d, until) <= 0);
    }).ToList();
    Console.WriteLine($"Date filter [{since ?? "..."} .. {until ?? "..."}]: {demoFiles.Count} of {before} demo(s)" +
                      (fromMtime > 0 ? $"  ({fromMtime} dated by file mtime, not name)" : ""));
}

if (limit > 0 && demoFiles.Count > limit)
{
    // Spread the sample across the archive rather than taking the oldest N: demos are named by
    // date, so the first N are one week of one season, which is not what the population looks like.
    int step = demoFiles.Count / limit;
    demoFiles = demoFiles.Where((_, i) => i % step == 0).Take(limit).ToList();
    Console.WriteLine($"--limit {limit}: sampling every {step}th demo across the archive");
}

if (demoFiles.Count == 0)
{
    Console.Error.WriteLine($"no .dem files found at: {path}");
    return 1;
}

// Resume: a 17k-demo run takes hours, so results are appended per demo and already-processed
// demos are skipped. Kill it, reboot, rerun the same command — it picks up where it left off.
// Resume off BOTH outputs. They're appended independently, so if only one is consulted the
// two can desync - delete baseline.csv but keep shots.csv and every re-parsed demo's shots get
// written a second time, silently double-counting those players.
// Returns null when the file exists but predates the demo column, i.e. it cannot tell us which
// demos it covers. Guessing in that case would append every re-parsed demo's rows a second time.
static HashSet<string>? DemosIn(string? p)
{
    if (p is null || !File.Exists(p)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var header = File.ReadLines(p).FirstOrDefault() ?? "";
    if (!header.StartsWith("demo,", StringComparison.OrdinalIgnoreCase)) return null;

    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(p).Skip(1))
    {
        int c = line.IndexOf(',');
        if (c > 0) set.Add(line[..c].Trim('"'));
    }
    return set;
}

var doneInCsv = DemosIn(csvPath);
var doneInShots = DemosIn(shotsPath);

if (shotsPath is not null && doneInShots is null)
{
    // Old-format shots.csv: resuming would silently double its rows.
    Console.Error.WriteLine($"[!] {shotsPath} has no demo column (written by an older build), so this run");
    Console.Error.WriteLine($"    cannot tell which demos it already covers. Resuming would duplicate its rows.");
    Console.Error.WriteLine($"    Either let the original run finish, or delete {shotsPath} and {csvPath} and start over.");
    return 1;
}

if ((doneInCsv?.Count ?? 0) > 0 || (doneInShots?.Count ?? 0) > 0)
{
    // Only skip a demo when every requested output already has it.
    var alreadyDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in (doneInCsv?.Count ?? 0) > 0 ? doneInCsv! : doneInShots!)
        if ((csvPath is null || doneInCsv!.Contains(d)) && (shotsPath is null || doneInShots!.Contains(d)))
            alreadyDone.Add(d);

    int before = demoFiles.Count;
    demoFiles = demoFiles.Where(f => !alreadyDone.Contains(Path.GetFileName(f))).ToList();
    Console.WriteLine($"Resuming: {before - demoFiles.Count} demo(s) already done, {demoFiles.Count} to go");

    if (csvPath is not null && shotsPath is not null && doneInCsv!.Count != doneInShots!.Count)
        Console.WriteLine($"  [!] {csvPath} has {doneInCsv.Count} demos, {shotsPath} has {doneInShots.Count} " +
                          $"- outputs are out of sync. Delete both and restart for a clean run.");
}

Console.WriteLine($"Replaying {demoFiles.Count} demo(s), poll {pollInterval * 1000:F0}ms, {jobs} parallel job(s)\n");

// Rows go straight to the CSV, so holding every PlayerResult would only serve the end-of-run
// summary - at 17k demos that's ~170k objects each with a dictionary, and the GC pressure on top
// of 20 inflated demos is what drags a long run from 224/min down to 84/min. Keep the scores for
// the percentiles and a bounded top list; drop the rest.
var allScores = new List<float>();
var topResults = new List<PlayerResult>();
const int TopKeep = 40;
var gate = new object();
// Track WHICH demos fail, not just how many: the parser chokes on entities in certain
// workshop maps (CSun, CFireSmoke), so the loss lands on whole maps. Grouping the failures
// by map is how we tell whether the gap skews the sample or just thins it.
var failures = new List<(string demo, string reason)>();
int done = 0, failed = 0;
var started = DateTime.UtcNow;

StreamWriter? csv = null;
if (csvPath is not null)
{
    bool fresh = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
    // AutoFlush would syscall per line - ~3500 shot rows per demo, inside the global write
    // lock, with 19 other threads waiting. Flush once per demo instead: same crash-safety
    // (resume granularity is a whole demo anyway), thousands of times fewer syscalls.
    csv = new StreamWriter(csvPath, append: true) { AutoFlush = false };
    // aliveMinutes is the exposure: without it you cannot compute a rate or a Poisson baseline.
    if (fresh) csv.WriteLine("demo,steamId,name,peakScore,aliveMinutes,wallhackTrack,aimbotSweep,triggerbot,shots,hits,headshots,unseenSamples,unseenNow,unseenPast,kills,killWall,killStill,killOnTgt");
    csv.Flush();
}

StreamWriter? shotsCsv = null;
if (shotsPath is not null)
{
    bool freshShots = !File.Exists(shotsPath) || new FileInfo(shotsPath).Length == 0;
    shotsCsv = new StreamWriter(shotsPath, append: true) { AutoFlush = false };
    // switchMs/switchDeg are -1 when the shot stayed on the same target.
    // demo first: without it the file can't be deduped or resumed, and a half-finished run
    // silently doubles some players' shots.
    if (freshShots) shotsCsv.WriteLine("demo,steamId,name,aimErrDeg,switchMs,switchDeg,onTargetMs,viewRateDegPerSec,burstStart");
    shotsCsv.Flush();
}

// Each demo is parsed independently, so this scales straight across cores.
await Parallel.ForEachAsync(demoFiles, new ParallelOptions { MaxDegreeOfParallelism = jobs },
    async (file, _) =>
    {
        try
        {
            var (results, shotRows) = await ReplayOne(file, pollInterval);
            lock (gate)
            {
                foreach (var r in results) allScores.Add(r.PeakScore);
                topResults.AddRange(results);
                if (topResults.Count > TopKeep * 4)
                {
                    topResults.Sort((a, b) => b.PeakScore.CompareTo(a.PeakScore));
                    topResults.RemoveRange(TopKeep, topResults.Count - TopKeep);
                }
                done++;
                foreach (var r in results) csv?.WriteLine(CsvRow(r)); // flushed per demo
                foreach (var sh in shotRows)
                    shotsCsv?.WriteLine($"{Csv(Path.GetFileName(file))},{sh.SteamId},{Csv(sh.Name)}," +
                        $"{sh.AimErrDeg.ToString("F2", CultureInfo.InvariantCulture)}," +
                        $"{sh.SwitchMs.ToString("F0", CultureInfo.InvariantCulture)}," +
                        $"{sh.SwitchDeg.ToString("F1", CultureInfo.InvariantCulture)}," +
                        $"{sh.OnTargetMs.ToString("F0", CultureInfo.InvariantCulture)}," +
                        $"{sh.ViewRateDegPerSec.ToString("F0", CultureInfo.InvariantCulture)}," +
                        $"{sh.BurstStart}");
                csv?.Flush();       // one flush per demo, not one per row
                shotsCsv?.Flush();
                DrawProgress(done, demoFiles.Count, started, failed);
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                failed++;
                failures.Add((Path.GetFileName(file), ex.Message));
                DrawProgress(done, demoFiles.Count, started, failed);
            }
        }
    });

csv?.Dispose();
shotsCsv?.Dispose();
Console.WriteLine();
Console.WriteLine($"\nParsed in {(DateTime.UtcNow - started).TotalMinutes:F1} min");

Console.WriteLine($"\n=== {allScores.Count} player-sessions across {done} demo(s) ({failed} failed) ===");

if (failures.Count > 0)
{
    Console.WriteLine($"\n=== {failures.Count} failed demo(s) ({100.0 * failures.Count / Math.Max(1, failures.Count + done):F1}%) ===");
    Console.WriteLine("  by reason:");
    foreach (var g in failures.GroupBy(f => f.reason).OrderByDescending(g => g.Count()).Take(6))
        Console.WriteLine($"    {g.Count(),5}  {Trunc(g.Key, 70)}");

    // Map name sits between the timestamp and the extension: YYYYMMDD-HHMMSS-<map>.dem[.gz]
    Console.WriteLine("  by map (top 10):");
    foreach (var g in failures
                 .Select(f => { var p = f.demo.Split('-', 3); return p.Length == 3 ? p[2].Replace(".dem.gz", "").Replace(".dem", "") : f.demo; })
                 .GroupBy(m => m).OrderByDescending(g => g.Count()).Take(10))
        Console.WriteLine($"    {g.Count(),5}  {g.Key}");

    if (failLog is not null)
    {
        File.WriteAllLines(failLog, failures.Select(f => $"{f.demo}\t{f.reason}"));
        Console.WriteLine($"  wrote {failLog}");
    }
    else Console.WriteLine("  (pass --fail-log failures.txt to save the full list)");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

// The distribution is the point: where do ordinary players actually land?
if (allScores.Count > 0)
{
    var scores = allScores.OrderBy(s => s).ToList();
    Console.WriteLine($"\nPeak-score distribution (all player-sessions):");
    foreach (var q in new[] { 0.50, 0.90, 0.99, 0.999, 1.0 })
    {
        int idx = Math.Min(scores.Count - 1, (int)(q * (scores.Count - 1)));
        Console.WriteLine($"  p{q * 100,-5:0.#}: {scores[idx]:F2}");
    }

    Console.WriteLine($"\nTop 20 by peak score:");
    foreach (var r in topResults.OrderByDescending(r => r.PeakScore).Take(20))
        Console.WriteLine($"  {r.PeakScore,5:F2}  {r.Name,-24} {r.SteamId,-18} {r.Detail}  [{Path.GetFileName(r.Demo)}]");
}

return 0;

// One line that rewrites itself, rather than 17k lines of scrollback.
static void DrawProgress(int done, int total, DateTime started, int failed)
{
    // A failed demo is still a demo we're finished with. Counting only successes made the bar
    // read 36% when 76% of the archive had been processed, and pushed the ETA up over time.
    int processed = done + failed;
    var elapsed = DateTime.UtcNow - started;
    double pct = total > 0 ? 100.0 * processed / total : 100.0;
    double perMin = elapsed.TotalMinutes > 0.01 ? processed / elapsed.TotalMinutes : 0;
    var eta = processed > 0 ? TimeSpan.FromTicks(elapsed.Ticks / processed * Math.Max(0, total - processed)) : TimeSpan.Zero;

    const int width = 28;
    int filled = Math.Clamp((int)(width * pct / 100.0), 0, width);
    string bar = new string('#', filled) + new string('.', width - filled);
    string fail = failed > 0 ? $"  {failed} failed" : "";

    Console.Write($"\r  [{bar}] {pct,5:F1}%  {processed}/{total}  {perMin,4:F0}/min  ETA {eta:hh\\:mm\\:ss}{fail}   ");
}

static string CsvRow(PlayerResult r) =>
    $"{Csv(Path.GetFileName(r.Demo))},{r.SteamId},{Csv(r.Name)}," +
    $"{r.PeakScore.ToString("F3", CultureInfo.InvariantCulture)}," +
    $"{r.AliveMinutes.ToString("F2", CultureInfo.InvariantCulture)}," +
    $"{r.Signals.GetValueOrDefault("wallhack.track")},{r.Signals.GetValueOrDefault("aimbot.sweep")}," +
    $"{r.Signals.GetValueOrDefault("triggerbot")},{r.Shots},{r.Hits},{r.Headshots}," +
    $"{r.UnseenSamples},{r.UnseenNow},{r.UnseenPast}," +
    $"{r.Kills},{r.KillWall.ToString("F5", CultureInfo.InvariantCulture)}," +
    $"{r.KillStill.ToString("F1", CultureInfo.InvariantCulture)},{r.KillOnTgt.ToString("F2", CultureInfo.InvariantCulture)}";

static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

static async Task<(List<PlayerResult> players, List<ShotRow> shots)> ReplayOne(string file, float pollInterval)
{
    var demo = new CsDemoParser();

    // Same detectors + fusion engine as the plugin, at their default (production) thresholds.
    // Gaze is NOT run: it's disabled by default in production (it fired on 100% of players),
    // so including it here would misrepresent what the live plugin actually scores.
    var wallhack = new WallhackDetector();
    var aimbot = new AimbotSweepDetector();
    var triggerbot = new TriggerbotDetector();
    var engine = new SuspicionEngine();

    var peakScore = new Dictionary<int, float>();
    var names = new Dictionary<int, string>();
    var steamIds = new Dictionary<int, ulong>();
    var signals = new Dictionary<(int slot, string detector), int>();
    var alivePolls = new Dictionary<int, int>();
    // Null test: per observer, how often the crosshair sat on an UNSEEN enemy's current position
    // versus that same enemy's position 1.5s earlier. The past is the control - a player who
    // simply knows the map, heard him, or saw him run there scores on both.
    var unseenSamples = new Dictionary<int, int>();
    var unseenNow = new Dictionary<int, int>();
    var unseenPast = new Dictionary<int, int>();

    // Kill-anchored wallhack signature (XGuardian Case D, validated on their labelled CS2 data,
    // player-level AUC 0.68): at each kill, the attacker's pre-kill aim STILLNESS x proximity to
    // the actual victim. A wallhacker parks the crosshair on the (unseen) enemy and waits; a
    // normal player scans and reacts, so their pre-kill aim moves more and sits off-target until
    // the flick. The CONJUNCTION is the signal - stillness alone points the WRONG way (the
    // stillest players are campers, AUC 0.46). Product, not sum: a still crosshair is only
    // suspicious when it is already on the enemy. Keep the raw means too, so the best combination
    // can be read off OUR population rather than carried over from XGuardian's pixel scale.
    var killCount = new Dictionary<int, int>();
    var killWallSum = new Dictionary<int, float>();    // sum of per-kill (still x on-target) signature
    var killStillSum = new Dictionary<int, float>();   // sum of per-kill mean view angular speed (deg/s)
    var killOnTgtSum = new Dictionary<int, float>();   // sum of per-kill mean aim error to victim (deg)

    // Per-tick spotted snapshots so the kill-anchored check can apply the WALLHACK-SPECIFIC gate
    // XGuardian could not: was the victim UNSPOTTED by the attacker during the run-up. Being still
    // and on-target only condemns when the target could not be seen - otherwise it is just good
    // pre-aim, which is why the LOS-free v1 topped out on skilled regulars. Slots cap at 64, so the
    // whole SpottedByMask packs into one ulong (bit s = observer slot s has spotted this pawn).
    const int MaskRing = 128;
    var maskSeq = new Dictionary<int, int[]>();
    var maskVal = new Dictionary<int, ulong[]>();
    var maskPos = new Dictionary<int, int>();
    // Missing record -> no evidence of being seen -> treat as unspotted (same default as the null test).
    bool VictimSeenByAttacker(int victimSlot, int seq, int attackerSlot)
    {
        if (!maskSeq.TryGetValue(victimSlot, out var sq)) return false;
        var vals = maskVal[victimSlot];
        for (int i = 0; i < MaskRing; i++)
            if (sq[i] == seq) return (vals[i] & (1UL << attackerSlot)) != 0UL;
        return false;
    }

    // Shot-triggered detectors need the per-tick history the live plugin keeps, so mirror
    // TrackingService here: sample every tick, not at the (slower) wallhack poll rate.
    var trackers = new Dictionary<int, PlayerTracker>();
    var teams = new Dictionary<int, CSTeamNumber>();
    // Accuracy: the most direct aim signal there is - an aimbot has to hit more often, that's
    // the whole point of it. Counting hurts raw overcounts, though: a shotgun pull logs nine
    // pellet hurts against one shot, and a bullet that penetrates one player into another logs
    // two. One bullet is fired on exactly one tick, so hurts sharing (attacker, tick) are one
    // bullet - dedupe on that and shotguns need no special case. Utility still has to go: it
    // damages with no shot behind it.
    var shotCount = new Dictionary<int, int>();
    var bulletsThatHit = new HashSet<(int slot, int tick)>();
    var bulletsThatHeadshot = new HashSet<(int slot, int tick)>();
    var lastFire = new Dictionary<int, float>();
    var prevShot = new Dictionary<int, (float time, int target, ViewAngles angles)>();
    var shots = new List<ShotRow>();
    int sequence = 0;
    const float BurstWindowSeconds = 0.25f;

    float roundStartTime = 0f;
    float Now() => demo.CurrentDemoTick.Value / (float)Math.Max(1, CsDemoParser.TickRate);
    demo.Source1GameEvents.RoundStart += _ => roundStartTime = Now();

    void Report(IDetector detector, Signal? signal)
    {
        if (signal is not { } s) return;
        signals[(s.PlayerSlot, s.Detector)] = signals.GetValueOrDefault((s.PlayerSlot, s.Detector)) + 1;
        engine.Report(s, detector.Weight);
    }

    demo.Source1GameEvents.PlayerHurt += e =>
    {
        var att = e.Attacker;
        if (att is null || att.SteamID == 0 || ReferenceEquals(att, e.Player)) return;
        var w = (e.Weapon ?? "").ToLowerInvariant();
        if (w.Contains("grenade") || w.Contains("molotov") || w.Contains("inferno") ||
            w.Contains("knife") || w.Contains("bayonet") || w.Contains("taser")) return;
        int s2 = (int)att.EntityIndex.Value - 1;
        if (s2 < 0) return;
        int tk = demo.CurrentDemoTick.Value;
        bulletsThatHit.Add((s2, tk));
        if (e.Hitgroup == 1) bulletsThatHeadshot.Add((s2, tk));   // 1 = head
    };

    // Kill-anchored pre-kill window: the offline equivalent of XGuardian's 96-tick elimination
    // window. At each kill, walk back over the attacker's buffered aim and compare it, tick for
    // tick, against where the victim ACTUALLY was - the run-up, not the shot itself.
    demo.Source1GameEvents.PlayerDeath += e =>
    {
        var att = e.Attacker;
        var victim = e.Player;
        if (att is null || victim is null || att.SteamID == 0 || ReferenceEquals(att, victim)) return;
        var w = (e.Weapon ?? "").ToLowerInvariant();
        if (w.Contains("grenade") || w.Contains("molotov") || w.Contains("inferno") ||
            w.Contains("knife") || w.Contains("bayonet") || w.Contains("taser")) return;
        int aSlot = (int)att.EntityIndex.Value - 1;
        int vSlot = (int)victim.EntityIndex.Value - 1;
        if (aSlot < 0 || vSlot < 0) return;
        if (!trackers.TryGetValue(aSlot, out var aTr) || !trackers.TryGetValue(vSlot, out var vTr)) return;

        // ~1s of run-up at 64 tick, but only the ticks where the attacker could NOT see the victim.
        // A normal visible fight has the victim spotted, so it contributes nothing; a kill that was
        // set up on an unseen enemy is the wallhack case. Same contiguous-sequence rule the other
        // detectors use: a gap means the buffer skipped ticks, so those deltas are untrustworthy.
        const int PreKillTicks = 64;
        float speedSum = 0f, errSum = 0f;
        int errN = 0, speedN = 0;
        int limit = Math.Min(PreKillTicks, aTr.Count);
        for (int i = 0; i < limit; i++)
        {
            var a = aTr[i];
            if (!a.Alive) break;
            if (VictimSeenByAttacker(vSlot, a.Sequence, aSlot)) continue;   // spotted: legitimate visibility
            // aim error to the victim's position at the SAME tick (the "on-target" axis)
            if (vTr.TryGetBySequence(a.Sequence, out var vs) && vs.Alive)
            {
                var eye = a.Origin + new Vector3(0f, 0f, 64f);
                errSum += Geometry.NearestBodyAimError(eye, a.Angles, vs.Origin);
                errN++;
            }
            // view angular speed between consecutive ticks (the "stillness" axis)
            if (i + 1 < limit)
            {
                var older = aTr[i + 1];
                if (a.Sequence - older.Sequence == 1)
                {
                    float dt = a.Time - older.Time;
                    if (dt > 0f) { speedSum += Geometry.AngleBetween(older.Angles, a.Angles) / dt; speedN++; }
                }
            }
        }
        // Require a real unseen run-up, not one or two stray ticks, before this kill counts.
        if (errN < 10 || speedN == 0) return;
        float meanErr = errSum / errN;            // deg: low = crosshair sat on the victim pre-kill
        float meanSpeed = speedSum / speedN;      // deg/s: low = still
        // The conjunction that scored AUC 0.68 on XGuardian's labelled cheaters: high only when the
        // aim is BOTH still and already on the enemy. Constants keep it finite; the exact scale is
        // XGuardian's (pixels), so treat killWall as a ranking and re-fit off killStill/killOnTgt.
        float signature = 1f / ((meanSpeed + 0.5f) * (meanErr + 5f));

        killCount[aSlot] = killCount.GetValueOrDefault(aSlot) + 1;
        killWallSum[aSlot] = killWallSum.GetValueOrDefault(aSlot) + signature;
        killStillSum[aSlot] = killStillSum.GetValueOrDefault(aSlot) + meanSpeed;
        killOnTgtSum[aSlot] = killOnTgtSum.GetValueOrDefault(aSlot) + meanErr;
    };

    // Shots: run the same aimbot/triggerbot the plugin runs, against the tick buffers.
    demo.Source1GameEvents.WeaponFire += e =>
    {
        var shooter = e.Player;
        if (shooter is null || shooter.SteamID == 0 || !shooter.PawnIsAlive) return;
        int slot = (int)shooter.EntityIndex.Value - 1;
        shotCount[slot] = shotCount.GetValueOrDefault(slot) + 1;
        if (slot < 0 || !trackers.TryGetValue(slot, out var shooterTracker)) return;

        var team = teams.GetValueOrDefault(slot);
        var enemies = trackers.Where(kv => kv.Key != slot &&
                                           teams.TryGetValue(kv.Key, out var t) && t != team &&
                                           (t == CSTeamNumber.Terrorist || t == CSTeamNumber.CounterTerrorist))
                              .Select(kv => kv.Value).ToList();
        if (enemies.Count == 0) return;

        float now = Now();
        // Spray gating, same as the plugin: only the first shot of a burst has a real reaction.
        bool burst = lastFire.TryGetValue(slot, out var lf) && now - lf < BurstWindowSeconds;
        lastFire[slot] = now;

        // Both gate on the burst's first shot, matching the plugin: a spray's later bullets are
        // not fresh decisions, and counting each one reads one trigger pull as many.
        if (!burst) Report(aimbot, aimbot.OnFire(shooterTracker, enemies, now));
        if (!burst) Report(triggerbot, triggerbot.OnFire(shooterTracker, enemies, now));

        // --- raw measurements, so thresholds can be READ OFF the population instead of guessed ---
        // The metric that matters: switching from one enemy to another. A human has to find the
        // next target; an aimbot is already there. Angle travelled / time = the scale we need.
        if (!shooterTracker.TryLatest(out var atFire)) return;
        var eye = atFire.Origin + new Vector3(0f, 0f, 64f);

        int nearestId = -1;
        float nearestErr = float.MaxValue;
        foreach (var kv in trackers)
        {
            if (kv.Key == slot) continue;
            if (!teams.TryGetValue(kv.Key, out var et) || et == team) continue;
            if (!kv.Value.TryLatest(out var es) || !es.Alive) continue;
            float err = Geometry.NearestBodyAimError(eye, atFire.Angles, es.Origin);
            if (err < nearestErr) { nearestErr = err; nearestId = kv.Key; }
        }
        if (nearestId < 0) return;

        float switchMs = -1f, switchDeg = -1f;
        int fromTarget = -1;
        if (prevShot.TryGetValue(slot, out var prev) && prev.target != nearestId)
        {
            fromTarget = prev.target;
            switchMs = (now - prev.time) * 1000f;
            switchDeg = Geometry.AngleBetween(prev.angles, atFire.Angles);
        }
        prevShot[slot] = (now, nearestId, atFire.Angles);

        // The triggerbot metric: how long had the crosshair been ON this enemy before firing?
        // Walk back while the aim stayed on target, comparing against the enemy's position AT
        // THAT tick. A triggerbot fires the instant the crosshair touches; a human dwells.
        // -1 = the shot wasn't on target at all (no dwell time to speak of).
        const float OnTargetDeg = 3f;
        float onTargetMs = -1f;
        if (nearestErr <= OnTargetDeg && trackers.TryGetValue(nearestId, out var targetTracker))
        {
            int limit = Math.Min(64, shooterTracker.Count);
            int onset = 0;
            for (int i = 1; i < limit; i++)
            {
                var older = shooterTracker[i];
                if (shooterTracker[i - 1].Sequence - older.Sequence != 1) break; // gap: untrusted
                if (!older.Alive) break;
                if (!targetTracker.TryGetBySequence(older.Sequence, out var es) || !es.Alive) break;
                var olderEye = older.Origin + new Vector3(0f, 0f, 64f);
                if (Geometry.NearestBodyAimError(olderEye, older.Angles, es.Origin) > OnTargetDeg) break;
                onset = i;
            }
            onTargetMs = (atFire.Time - shooterTracker[onset].Time) * 1000f;
        }

        // How fast the view was still travelling as the trigger went. On CS2CD this is the only
        // metric that earned its keep: thresholded above the non-cheaters' p99 it caught 15.4% of
        // 254 verified cheaters while touching 1.1% of everyone else -- a 14x lift, where dwell
        // managed 1.8% and hit% 6.9%.
        //
        // It was built to catch triggerbots and found none (a trigger fires only on a body, so its
        // mid-sweep shots would be ~100% on target; the cheaters sat at 6.6%). What it catches is
        // aim assist: a crosshair that sweeps past and is suddenly correct leaves the same mark
        // whatever is pulling it. Paired with !burst it also excludes the two things that make
        // everyone look fast -- the parked pre-fire and the spray.
        float viewRate = -1f;
        if (shooterTracker.Count >= 2)
        {
            var prevTick = shooterTracker[1];
            float dt = atFire.Time - prevTick.Time;
            if (atFire.Sequence - prevTick.Sequence == 1 && dt > 0f && dt <= 0.1f)
                viewRate = Geometry.AngleBetween(prevTick.Angles, atFire.Angles) / dt;
        }

        shots.Add(new ShotRow(
            steamIds.GetValueOrDefault(slot), names.GetValueOrDefault(slot, "?"),
            nearestErr, fromTarget >= 0 ? switchMs : -1f, fromTarget >= 0 ? switchDeg : -1f,
            onTargetMs, viewRate, burst ? 0 : 1));
    };

    float lastPoll = float.NegativeInfinity;
    demo.OnCommandFinishPersistent = () =>
    {
        float now = Now();

        // --- per-tick: keep the trackers the shot-triggered detectors read from ---
        sequence++;
        foreach (var p in demo.Players)
        {
            if (p.SteamID == 0) continue;
            var t = p.CSTeamNum;
            if (t != CSTeamNumber.Terrorist && t != CSTeamNumber.CounterTerrorist) continue;
            var pw = p.PlayerPawn;
            if (pw is null) continue;
            int s = (int)p.EntityIndex.Value - 1;
            if (s < 0) continue;

            teams[s] = t;
            if (!trackers.TryGetValue(s, out var tr))
                trackers[s] = tr = new PlayerTracker(256, s);   // ~4s of history: the null test looks 1.5s back
            tr.Add(new TickSample(
                sequence, now,
                new Vector3(pw.Origin.X, pw.Origin.Y, pw.Origin.Z),
                new ViewAngles(pw.EyeAngles.Pitch, pw.EyeAngles.Yaw, pw.EyeAngles.Roll),
                Vector3.Zero, OnGround: true, Alive: p.PawnIsAlive));

            // Pack this pawn's spotted mask (who has seen it) into the ring, keyed by sequence.
            ulong packed = 0UL;
            var sm = pw.EntitySpottedState?.SpottedByMask;
            if (sm is not null)
            {
                if (sm.Length > 0) packed |= sm[0];
                if (sm.Length > 1) packed |= ((ulong)sm[1]) << 32;
            }
            if (!maskSeq.TryGetValue(s, out var mq))
            {
                maskSeq[s] = mq = new int[MaskRing];
                maskVal[s] = new ulong[MaskRing];
                Array.Fill(mq, -1);
                maskPos[s] = 0;
            }
            int mp = maskPos[s];
            mq[mp] = sequence;
            maskVal[s][mp] = packed;
            maskPos[s] = (mp + 1) % MaskRing;
        }

        if (now - lastPoll < pollInterval) return;
        lastPoll = now;

        bool roundStart = now - roundStartTime < 20f;
        var players = demo.Players.ToList();

        foreach (var observer in players)
        {
            if (!observer.PawnIsAlive) continue;
            // Bots have SteamID 0 and server-driven aim — they trip the detectors constantly and
            // aren't cheaters. The live plugin skips them too (IncludeBots defaults to false).
            if (observer.SteamID == 0) continue;
            var team = observer.CSTeamNum;
            if (team != CSTeamNumber.Terrorist && team != CSTeamNumber.CounterTerrorist) continue;

            var pawn = observer.PlayerPawn;
            if (pawn is null) continue;

            // Player slot = controller entity index - 1 in CS2; must match the SpottedByMask bit layout.
            int slot = (int)observer.EntityIndex.Value - 1;
            if (slot < 0) continue;

            names[slot] = observer.PlayerName ?? $"slot {slot}";
            steamIds[slot] = observer.SteamID;
            // Register every player we poll, so players who never trigger a signal still appear
            // as 0.00. Without this the distribution is computed only over players WITH signals,
            // which quietly inflates every percentile.
            peakScore.TryAdd(slot, 0f);
            alivePolls[slot] = alivePolls.GetValueOrDefault(slot) + 1;

            var eye = new Vector3(pawn.Origin.X, pawn.Origin.Y, pawn.Origin.Z + 64f);
            var angles = new ViewAngles(pawn.EyeAngles.Pitch, pawn.EyeAngles.Yaw, pawn.EyeAngles.Roll);

            WallhackDetector.WallTarget? bestAim = null;
            float bestAimErr = 5f;
            WallhackGazeDetector.GazeSample? bestGaze = null;
            float bestGazeErr = 25f;

            foreach (var enemy in players)
            {
                if (ReferenceEquals(enemy, observer) || !enemy.PawnIsAlive) continue;
                if (enemy.CSTeamNum == team) continue;
                if (enemy.CSTeamNum != CSTeamNumber.Terrorist && enemy.CSTeamNum != CSTeamNumber.CounterTerrorist) continue;

                var ep = enemy.PlayerPawn;
                if (ep is null) continue;

                // Only enemies NOT spotted by this observer count — the wallhack premise.
                var mask = ep.EntitySpottedState.SpottedByMask;
                if (mask is null || mask.Length <= slot / 32) continue;
                if ((mask[slot / 32] & (1u << (slot % 32))) != 0) continue;

                int enemyId = (int)enemy.EntityIndex.Value;
                var feet = new Vector3(ep.Origin.X, ep.Origin.Y, ep.Origin.Z);
                float err = Geometry.NearestBodyAimError(eye, angles, feet);
                float bearingYaw = MathF.Atan2(feet.Y - eye.Y, feet.X - eye.X) * (180f / MathF.PI);

                // THE NULL TEST. Every metric built here so far asks how GOOD a player is, and a
                // cheat with aim assist just makes someone statistically excellent - which is what
                // it is sold to do. So levels overlap, the six measured all landed at 1.2-1.8x,
                // and that is structural rather than bad luck.
                //
                // What no amount of skill buys is knowing where an enemy is while unable to see
                // them. That is not a scale, it is an information channel that should not exist.
                //
                // So compare each unseen enemy's position NOW against where that same enemy was
                // 1.5 seconds ago, from the same crosshair. Game sense - common angles, footsteps,
                // remembering where he ran - correlates a player's aim with the enemy's PAST just
                // as well as their present, because both are only places on a map. Tracking the
                // present is what cannot be explained, and it is the same number for a five-year
                // regular as for anyone else, which is exactly what the level metrics never were.
                int eSlot = enemyId - 1;
                if (eSlot >= 0 && trackers.TryGetValue(eSlot, out var eTr) && eTr.Count > NullLagIndex)
                {
                    var past = eTr[NullLagIndex];
                    float lag = now - past.Time;
                    if (lag >= 1.0f && lag <= 2.5f && past.Alive)
                    {
                        unseenSamples[slot] = unseenSamples.GetValueOrDefault(slot) + 1;
                        if (err <= UnseenAimDeg)
                            unseenNow[slot] = unseenNow.GetValueOrDefault(slot) + 1;
                        if (Geometry.NearestBodyAimError(eye, angles, past.Origin) <= UnseenAimDeg)
                            unseenPast[slot] = unseenPast.GetValueOrDefault(slot) + 1;
                    }
                }

                if (err < bestAimErr)
                {
                    bestAimErr = err;
                    bestAim = new WallhackDetector.WallTarget(enemyId, feet, err, angles.Yaw, bearingYaw);
                }
                if (err < bestGazeErr)
                {
                    bestGazeErr = err;
                    bestGaze = new WallhackGazeDetector.GazeSample(enemyId, err, angles.Yaw, bearingYaw, roundStart);
                }
            }

            Report(wallhack, wallhack.Observe(slot, now, bestAim));

            float score = engine.ScoreOf(slot, now);
            if (score > peakScore.GetValueOrDefault(slot)) peakScore[slot] = score;
        }
    };

    await using var raw = File.OpenRead(file);
    Stream input = raw;
    if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
    {
        // The parser needs a seekable stream, so inflate into memory (~100-400 MB per demo;
        // budget --jobs accordingly on big archives).
        var buffer = new MemoryStream();
        await using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress))
            await gz.CopyToAsync(buffer);
        buffer.Position = 0;
        input = buffer;
    }

    var reader = DemoFileReader.Create(demo, input);
    await reader.ReadAllAsync();

    var playerRows = peakScore.Select(kv => new PlayerResult(
        file,
        steamIds.GetValueOrDefault(kv.Key),
        names.GetValueOrDefault(kv.Key, $"slot {kv.Key}"),
        kv.Value,
        alivePolls.GetValueOrDefault(kv.Key) * pollInterval / 60f,
        shotCount.GetValueOrDefault(kv.Key),
        bulletsThatHit.Count(b => b.slot == kv.Key),
        bulletsThatHeadshot.Count(b => b.slot == kv.Key),
        unseenSamples.GetValueOrDefault(kv.Key),
        unseenNow.GetValueOrDefault(kv.Key),
        unseenPast.GetValueOrDefault(kv.Key),
        killCount.GetValueOrDefault(kv.Key),
        killCount.GetValueOrDefault(kv.Key) > 0 ? killWallSum.GetValueOrDefault(kv.Key) / killCount[kv.Key] : 0f,
        killCount.GetValueOrDefault(kv.Key) > 0 ? killStillSum.GetValueOrDefault(kv.Key) / killCount[kv.Key] : 0f,
        killCount.GetValueOrDefault(kv.Key) > 0 ? killOnTgtSum.GetValueOrDefault(kv.Key) / killCount[kv.Key] : 0f,
        signals.Where(s => s.Key.slot == kv.Key).ToDictionary(s => s.Key.detector, s => s.Value)))
        .ToList();
    return (playerRows, shots);
}

internal readonly record struct ShotRow(
    ulong SteamId, string Name, float AimErrDeg, float SwitchMs, float SwitchDeg, float OnTargetMs,
    float ViewRateDegPerSec, int BurstStart);

internal sealed record PlayerResult(
    string Demo, ulong SteamId, string Name, float PeakScore, float AliveMinutes,
    int Shots, int Hits, int Headshots,
    int UnseenSamples, int UnseenNow, int UnseenPast,
    int Kills, float KillWall, float KillStill, float KillOnTgt, Dictionary<string, int> Signals)
{
    public string Detail =>
        Signals.Count > 0 ? string.Join(", ", Signals.Select(kv => $"{kv.Key}×{kv.Value}")) : "no signals";
}
