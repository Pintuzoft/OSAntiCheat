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
string? killsPath = null;   // --kills out.csv: one row per kill with raw deadaim components
string? configPath = null;  // --config thresholds.json: per-detector knobs for the detections report
string? since = null, until = null;
string? failLog = null;
string? skipFile = null;   // --skip fails.txt: don't re-attempt demos a prior run already failed on
int limit = 0;   // sanity-check a sample before committing an hour to the whole archive
int jobs = Math.Max(1, Environment.ProcessorCount - 2); // parsing is CPU-bound; leave a couple free
ulong revisitTarget = 0; // --revisit-detail <steamId>: print each double-peek episode (tick+time+enemy) to review
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--poll" && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        pollInterval = p;
    if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[i + 1];
    if (args[i] == "--jobs" && i + 1 < args.Length && int.TryParse(args[i + 1], out var j)) jobs = Math.Max(1, j);
    if (args[i] == "--shots" && i + 1 < args.Length) shotsPath = args[i + 1];
    if (args[i] == "--kills" && i + 1 < args.Length) killsPath = args[i + 1];
    if (args[i] == "--config" && i + 1 < args.Length) configPath = args[i + 1];
    if (args[i] == "--since" && i + 1 < args.Length) since = args[i + 1];
    if (args[i] == "--until" && i + 1 < args.Length) until = args[i + 1];
    if (args[i] == "--fail-log" && i + 1 < args.Length) failLog = args[i + 1];
    if (args[i] == "--skip" && i + 1 < args.Length) skipFile = args[i + 1];
    if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var L)) limit = L;
    if (args[i] == "--revisit-detail" && i + 1 < args.Length) ulong.TryParse(args[i + 1], out revisitTarget);
}

// Archives are stored gzipped (1.7 TB of them) — read .dem.gz directly rather than making
// anyone unpack the lot first.
var demoFiles = Directory.Exists(path)
    ? Directory.EnumerateFiles(path, "*.dem*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dem.gz", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f).ToList()
    : File.Exists(path) ? new List<string> { path } : new List<string>();

// --skip: drop demos a prior run already failed on (fail-log format: "basename<TAB>reason"). Those
// produced no data — skipping them turns a re-parse over the whole archive into one over just the
// readable demos (e.g. 7.8k CS2 vs 26k total: the CSun/CFireSmoke workshop maps never parse).
if (skipFile is not null && File.Exists(skipFile))
{
    var skip = File.ReadLines(skipFile)
        .Select(l => l.Split('\t')[0].Trim())
        .Where(s => s.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    int before = demoFiles.Count;
    demoFiles = demoFiles.Where(f => !skip.Contains(Path.GetFileName(f))).ToList();
    Console.WriteLine($"--skip {skipFile}: skipping {before - demoFiles.Count} known-failed demo(s), {demoFiles.Count} to parse");
}

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
var recoilResults = new List<(string name, ulong id, float spread, float pull, float ratio, int sprays, string demo)>();
var revisitResults = new List<(string name, ulong id, int count, int depth, float mins, string demo)>();
// Detector thresholds: tuned offline against known-legit players (raise a knob until they go
// quiet), then the SAME file ports to the live plugin. Defaults = today's best guesses.
var cfg = configPath is not null
    ? System.Text.Json.JsonSerializer.Deserialize<SweepConfig>(File.ReadAllText(configPath),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SweepConfig()
    : new SweepConfig();

var followResults = new List<(string name, ulong id, float ms, float sweep, int tick, string demo)>();
var allGatedSigs = new List<float>();   // every kill's gatedSig -> the distribution thresholds are read from
var gatedTop = new List<(string att, ulong attId, string vict, int round, int tick, string weap, float sig, float gated, string demo)>();
var headSpikers = new List<(string name, ulong id, int spike, int n, string demo)>();
var nullTestHits = new List<(string name, ulong id, float excess, int n, string demo)>();
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
    if (fresh) csv.WriteLine("demo,steamId,name,peakScore,aliveMinutes,wallhackTrack,aimbotSweep,triggerbot,shots,hits,headshots,unseenSamples,unseenNow,unseenPast,kills,killWall,killStill,killOnTgt,recoilSprays,recoilConsist,recoilPull,recoilRatio,revisits,maxPeekDepth,followMs,followSweep,followTick,killWallMax,headN,headSpike,spinMaxYaw");
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
    if (freshShots) shotsCsv.WriteLine("demo,steamId,name,aimErrDeg,switchMs,switchDeg,onTargetMs,viewRateDegPerSec,burstStart,headErrDeg,tick,targetId");
    shotsCsv.Flush();
}

StreamWriter? killsCsv = null;
if (killsPath is not null)
{
    bool freshKills = !File.Exists(killsPath) || new FileInfo(killsPath).Length == 0;
    killsCsv = new StreamWriter(killsPath, append: true) { AutoFlush = false };
    if (freshKills) killsCsv.WriteLine("demo,attackerId,attackerName,victimId,victimName,round,tick,weapon,headshot,dmg," +
        "stillDegS,onTgtDeg,sig,blindTicks,aliveTicks,teamSeenTicks,victimPathU,victimNetU,frozenMs,shotsInHold," +
        "sinceAttSawSec,sinceMateSawSec,distU,gatedSig");
    killsCsv.Flush();
}

// Each demo is parsed independently, so this scales straight across cores.
await Parallel.ForEachAsync(demoFiles, new ParallelOptions { MaxDegreeOfParallelism = jobs },
    async (file, _) =>
    {
        try
        {
            var (results, shotRows, killRows) = await ReplayOne(file, pollInterval, revisitTarget, demoFiles.Count <= 5);
            lock (gate)
            {
                foreach (var r in results) allScores.Add(r.PeakScore);
                foreach (var r in results)
                    // Candidate gate: real sprays only. >=4 sprays AND pull >=2deg (accumulated recoil
                    // was actually compensated) — a tapper never reaches this, so it can't be flagged.
                    if (r.RecoilSprays >= 4 && r.RecoilRatio >= 0f && r.RecoilPull >= 2f)
                        recoilResults.Add((r.Name, r.SteamId, r.RecoilConsist, r.RecoilPull, r.RecoilRatio, r.RecoilSprays, Path.GetFileName(r.Demo)));
                foreach (var r in results)
                    if (r.Revisits > 0)
                        revisitResults.Add((r.Name, r.SteamId, r.Revisits, r.MaxPeekDepth, r.AliveMinutes, Path.GetFileName(r.Demo)));
                foreach (var r in results)
                    if (r.FollowMs > 0)
                        followResults.Add((r.Name, r.SteamId, r.FollowMs, r.FollowSweep, r.FollowTick, Path.GetFileName(r.Demo)));
                // Multi-detector sweep collections: every kill's gatedSig for the distribution, the
                // extreme rows for review, and any player whose head-precision spiked repeatedly.
                foreach (var k in killRows)
                {
                    allGatedSigs.Add(k.GatedSig);
                    if (k.GatedSig >= 0.02f)
                        gatedTop.Add((k.AttackerName, k.AttackerId, k.VictimName, k.Round, k.Tick, k.Weapon, k.Sig, k.GatedSig, Path.GetFileName(file)));
                }
                foreach (var r in results)
                    if (r.HeadSpike >= cfg.BoneLockMinSpikes)
                        headSpikers.Add((r.Name, r.SteamId, r.HeadSpike, r.HeadN, Path.GetFileName(r.Demo)));
                foreach (var r in results)
                    if (r.UnseenSamples >= cfg.NullTestMinSamples &&
                        (r.UnseenNow - r.UnseenPast) / (float)r.UnseenSamples >= cfg.NullTestMinExcess)
                        nullTestHits.Add((r.Name, r.SteamId,
                            (r.UnseenNow - r.UnseenPast) / (float)r.UnseenSamples, r.UnseenSamples, Path.GetFileName(r.Demo)));
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
                        $"{sh.BurstStart}," +
                        $"{sh.HeadErrDeg.ToString("F3", CultureInfo.InvariantCulture)},{sh.Tick},{sh.TargetId}");
                foreach (var k in killRows)
                    killsCsv?.WriteLine($"{Csv(Path.GetFileName(file))},{k.AttackerId},{Csv(k.AttackerName)},{k.VictimId},{Csv(k.VictimName)}," +
                        $"{k.Round},{k.Tick},{Csv(k.Weapon)},{(k.Headshot ? 1 : 0)},{k.Dmg}," +
                        $"{k.StillDegS.ToString("F2", CultureInfo.InvariantCulture)},{k.OnTgtDeg.ToString("F2", CultureInfo.InvariantCulture)}," +
                        $"{k.Sig.ToString("F5", CultureInfo.InvariantCulture)},{k.BlindTicks},{k.AliveTicks},{k.TeamSeenTicks}," +
                        $"{k.VictimPathU.ToString("F0", CultureInfo.InvariantCulture)},{k.VictimNetU.ToString("F0", CultureInfo.InvariantCulture)}," +
                        $"{k.FrozenMs.ToString("F0", CultureInfo.InvariantCulture)},{k.ShotsInHold}," +
                        $"{k.SinceAttSawSec.ToString("F1", CultureInfo.InvariantCulture)},{k.SinceMateSawSec.ToString("F1", CultureInfo.InvariantCulture)}," +
                        $"{k.DistU.ToString("F0", CultureInfo.InvariantCulture)},{k.GatedSig.ToString("F5", CultureInfo.InvariantCulture)}");
                csv?.Flush();       // one flush per demo, not one per row
                shotsCsv?.Flush();
                killsCsv?.Flush();
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
killsCsv?.Dispose();
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

// Recoil consistency (raw measurement): lowest cross-spray spread = most machine-like. On a legit
// corpus this shows the HUMAN floor — how consistent the best sprayers actually get. A recoil script
// should sit far below it. Require >=4 sprays so a lucky pair doesn't top the list.
if (recoilResults.Count > 0)
{
    var rs = recoilResults.Select(r => r.ratio).OrderBy(x => x).ToList();
    // === The multi-detector sweep report: thresholds are READ off these distributions, never guessed ===
    if (allGatedSigs.Count > 0)
    {
        var gs = allGatedSigs.OrderBy(x => x).ToList();
        float GP(double p) => gs[Math.Clamp((int)(p * (gs.Count - 1)), 0, gs.Count - 1)];
        Console.WriteLine($"\n=== deadaim (gatedSig) over {gs.Count} kills — wallhack-parked-shot axis ===");
        Console.WriteLine($"  p99 {GP(.99):F3}  p99.9 {GP(.999):F3}  p99.99 {GP(.9999):F3}  max {gs[^1]:F3}");
        Console.WriteLine($"  review threshold candidate = above the innocent tail + admin budget; kills >=0.05 below:");
        foreach (var k in gatedTop.Where(k => k.gated >= 0.05f).OrderByDescending(k => k.gated).Take(30))
            Console.WriteLine($"    {k.gated,6:F3}  (raw {k.sig:F3})  {k.att,-20} -> {k.vict,-16} {k.weap,-10} r{k.round} tick {k.tick}  [{k.demo}]");
    }
    // === DETECTIONS, split by response TIER (the owner's principle: scream only at the impossible,
    // whisper about the improbable). Tuning loop: raise a knob until known-legit go quiet; the
    // calibrated file ports to the live plugin, which honours the same two tiers. ===
    Console.WriteLine($"\n=== DETECTIONS (config: {configPath ?? "defaults"}) ===");
    Console.WriteLine($"  knobs: deadaim>={cfg.DeadaimMin:F3}  boneLockSpikes>={cfg.BoneLockMinSpikes}  " +
                      $"antiRecoil<={cfg.AntiRecoilMaxRatio:F3} (>={cfg.AntiRecoilMinSprays} sprays)  " +
                      $"nullTestExcess>={cfg.NullTestMinExcess:P1} (n>={cfg.NullTestMinSamples})");

    // TIER 1 — LOGIC BREACH: mechanical impossibility (the hand physically cannot). High confidence,
    // auto-action-ELIGIBLE (still a human confirms before a ban in v1). These are the concrete-wall
    // axes: a cheater falls BELOW the human floor, never rises to the top of it.
    Console.WriteLine("\n  -- TIER 1: LOGIC BREACH (beyond-human — the hand can't do this) --");
    int t1 = 0;
    foreach (var h in headSpikers.OrderByDescending(h => h.spike))
    { t1++; Console.WriteLine($"  [bone-lock]   spike {h.spike}/{h.n}  {h.name,-20} {h.id}  [{h.demo}]"); }
    foreach (var r in recoilResults.Where(r => r.ratio <= cfg.AntiRecoilMaxRatio && r.sprays >= cfg.AntiRecoilMinSprays).OrderBy(r => r.ratio))
    { t1++; Console.WriteLine($"  [anti-recoil] ratio {r.ratio:F3} over {r.sprays} sprays  {r.name,-20} {r.id}  [{r.demo}]"); }
    if (t1 == 0) Console.WriteLine("     (none — machine zone empty)");

    // TIER 2 — REVIEW FLAG: improbable, never impossible (a human COULD have; a lucky hold, a great
    // read). Never auto-action. Produces a clip; a human judges; repetition is the discriminator.
    Console.WriteLine("\n  -- TIER 2: REVIEW FLAG (improbable — a human COULD, but should we look?) --");
    int t2 = 0;
    foreach (var k in gatedTop.Where(k => k.gated >= cfg.DeadaimMin).OrderByDescending(k => k.gated))
    { t2++; Console.WriteLine($"  [deadaim]     {k.gated,6:F3}  {k.att,-20} -> {k.vict,-16} {k.weap,-10} r{k.round} tick {k.tick}  [{k.demo}]"); }
    foreach (var n in nullTestHits.OrderByDescending(n => n.excess))
    { t2++; Console.WriteLine($"  [null-test]   excess {n.excess:P1} (n={n.n})  {n.name,-20} {n.id}  [{n.demo}]"); }
    if (t2 == 0) Console.WriteLine("     (none at these thresholds)");

    Console.WriteLine($"\nRecoil ratio (spread/pull; lower = more machine-like) over {recoilResults.Count} sessions (>=4 sprays):");
    foreach (var q in new[] { 0.01, 0.05, 0.10, 0.50, 0.90 })
        Console.WriteLine($"  p{q * 100,-4:0.#}: {rs[Math.Min(rs.Count - 1, (int)(q * (rs.Count - 1)))]:F2}");
    Console.WriteLine($"\nMost machine-consistent recoil (lowest ratio first) — ratio | spread | pull | sprays:");
    foreach (var r in recoilResults.OrderBy(r => r.ratio).Take(20))
        Console.WriteLine($"  {r.ratio,5:F2}  {r.spread,5:F2}deg  {r.pull,6:F2}deg  {r.sprays,3}  {r.name,-24} {r.id,-18} [{r.demo}]");
}

// wallhack.revisit: THE first question (the gaze lesson) — does the double-peek fire on ~everyone, or
// is it rare? If most legit sessions have revisits, the raw signature is too loose and needs the
// off-angle/baseline gate before it means anything. Rate per alive-minute, since count = playtime.
{
    int total = allScores.Count, withR = revisitResults.Count;
    Console.WriteLine($"\n=== wallhack.revisit (clutch, SILENT enemy, ~1s park x2 on SAME enemy): " +
                      $"{withR}/{total} sessions had a hit ({100.0 * withR / Math.Max(1, total):F2}%) ===");
    if (revisitResults.Count > 0)
    {
        int triples = revisitResults.Count(r => r.depth >= 3);
        Console.WriteLine($"  depth>=3 (triple+ peek — a callout explains one position, not three): {triples} session(s)");
        Console.WriteLine("  hardest hits (deepest peek on ONE silent unseen enemy first):");
        foreach (var r in revisitResults.OrderByDescending(r => r.depth).ThenByDescending(r => r.count).Take(20))
            Console.WriteLine($"    peek x{r.depth}  ({r.count} total)  {r.mins,5:F1}min  {r.name,-22} {r.id,-18} [{r.demo}]");
    }
}

// wallhack.follow: longest sustained track of a MOVING unseen enemy. Duration = certainty (a legit
// player loses a hidden enemy; a wallhacker stays glued). "swept" = bearing followed — high swept over
// a long follow = tracked through turns, which pre-aim can't fake. Ranked longest first.
if (followResults.Count > 0)
{
    Console.WriteLine($"\n=== wallhack.follow (tracked a MOVING unseen enemy >=3s): {followResults.Count} sessions ===");
    Console.WriteLine("  longest follow first — seconds | bearing swept | start tick:");
    foreach (var r in followResults.OrderByDescending(r => r.ms).Take(20))
        Console.WriteLine($"    {r.ms / 1000f,4:F1}s  swept {r.sweep,4:F0}deg  tick {r.tick,-8}  {r.name,-22} {r.id,-18} [{r.demo}]");
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
    $"{r.KillStill.ToString("F1", CultureInfo.InvariantCulture)},{r.KillOnTgt.ToString("F2", CultureInfo.InvariantCulture)}," +
    $"{r.RecoilSprays},{r.RecoilConsist.ToString("F3", CultureInfo.InvariantCulture)}," +
    $"{r.RecoilPull.ToString("F3", CultureInfo.InvariantCulture)},{r.RecoilRatio.ToString("F3", CultureInfo.InvariantCulture)}," +
    $"{r.Revisits},{r.MaxPeekDepth}," +
    $"{r.FollowMs.ToString("F0", CultureInfo.InvariantCulture)},{r.FollowSweep.ToString("F0", CultureInfo.InvariantCulture)},{r.FollowTick}," +
    $"{r.KillWallMax.ToString("F5", CultureInfo.InvariantCulture)},{r.HeadN},{r.HeadSpike}," +
    $"{r.SpinMaxYaw.ToString("F0", CultureInfo.InvariantCulture)}";

static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

// printBlocks: the per-demo reaction-cloud and head-precision tables are for eyeballing single
// demos; an archive sweep would print thousands of them (the data still lands in the shots CSV).
static async Task<(List<PlayerResult> players, List<ShotRow> shots, List<KillRow> kills)> ReplayOne(string file, float pollInterval, ulong revisitTarget, bool printBlocks = true)
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
    var killWallMax = new Dictionary<int, float>();    // peak per-kill signature (survives dilution)
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
    // Raw spotted mask for this pawn at this tick (0 = no record). The team-level test the
    // "information exculpates" principle needs: a callout means a LIVE TEAMMATE, not just the
    // attacker, had eyes on the victim -> a legitimate way to know the position.
    ulong VictimMaskAt(int victimSlot, int seq)
    {
        if (!maskSeq.TryGetValue(victimSlot, out var sq)) return 0UL;
        var vals = maskVal[victimSlot];
        for (int i = 0; i < MaskRing; i++)
            if (sq[i] == seq) return vals[i];
        return 0UL;
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
    var lastHurt = new Dictionary<(int att, int victim), (int dmg, int hpBefore)>();   // most recent hit, for kill dmg
    var fireTimes = new Dictionary<int, List<float>>();   // per-slot WeaponFire timestamps, for "how many shots during the hold"

    // spotted-rising -> shot: the LEGAL timing baseline. For each first-of-burst on-target shot,
    // walk back to the tick where the target flipped unseen->seen (the rising edge = earliest
    // legitimate visual stimulus) and record (reaction ms, crosshair distance at the edge).
    // flick<=3deg at the edge = the crosshair was already there (anticipation/pre-aim, NOT a
    // reaction — bucketed separately). The human cloud should sit ~200-450ms; a triggerbot is
    // pinned to the edge with no variance. the flagship blind-kill case has NO rising edge (never seen) —
    // it lives outside this measurement's domain by construction, which is the point.
    var reactionRows = new List<(int slot, float ms, float flickDeg)>();
    var lastSpottedAt = new Dictionary<(int observer, int victim), float>();   // whole-round memory, beyond the 2s mask ring
    var headRows = new List<(int slot, float err)>();   // head-center aim error at fire, for the bone-lock floor
    var kills = new List<KillRow>();                    // per-kill components, exported via --kills
    var spinMax = new Dictionary<int, float>();         // max SUSTAINED yaw rate (deg/s) — spinbot / auto-ban candidate
    var spinRun = new Dictionary<int, int>();           // current consecutive-tick spin run length
    var spinRunMin = new Dictionary<int, float>();      // min rate seen during the current run (its sustained floor)
    var spinDir = new Dictionary<int, int>();           // sign of the current run's yaw direction
    var lastFire = new Dictionary<int, float>();
    var prevShot = new Dictionary<int, (float time, int target, ViewAngles angles)>();
    var shots = new List<ShotRow>();

    // Recoil-script signature (v0, raw measurement): an anti-recoil script's per-spray view-angle
    // compensation curve is near-identical spray to spray; a human's pull-down varies. Group sprays
    // by weapon (recoil is weapon-specific) and, per player, measure the cross-spray SPREAD of the
    // compensation curve. Low spread = machine-consistent. NOTE: a PURE anti-recoil (player still aims
    // manually) mixes script-recoil + human-aim, so aim variance inflates the spread on real demos;
    // the clean test is a recoil script on bots (no aim) on a private server. Read it off the
    // population like the other raw columns — do not threshold it blind.
    var sprayWeapon = new Dictionary<int, string>();
    var sprayLastFire = new Dictionary<int, float>();
    var sprayCurve = new Dictionary<int, List<(float pitch, float yaw)>>();
    var spraysByWeapon = new Dictionary<(int slot, string weapon), List<(float pitch, float yaw)[]>>();
    // A real spray is CONTINUOUS auto fire at the weapon's cyclic rate (rifles ~0.09-0.10s/shot).
    // Tapping is click-aim-click, >=0.2s apart. Chain shots only within ~0.13s (cyclic + tick jitter)
    // so tapping physically cannot form a "spray" — the first, definitive tapping exclusion. The
    // min-pull gate on the candidate list is the second: a tap accumulates no recoil to compensate.
    const float SprayGapSeconds = 0.13f;
    const int MinSprayShots = 6;
    const int RecoilCurveLen = 8;

    // wallhack.revisit (the double-peek): per (observer, unspotted enemy), a small state machine that
    // fires when the aim goes ON-target -> OFF -> ON-target again within a window while the enemy stays
    // UNSPOTTED and STILL. A single pre-aim is just a held angle (exculpatory); the REVISIT onto an
    // unseen, stationary enemy is what game sense cannot explain. Raw count for now — off-angle/baseline
    // gate comes later IF this fires on everyone (like gaze did). phase: 0 seeking, 1 on-target, 2 left.
    var revisitState = new Dictionary<(int obs, int enemy), (int phase, float tOn1, Vector3 pos, int dwell, int depth)>();
    var revisitCount = new Dictionary<int, int>();
    var maxDepth = new Dictionary<int, int>();  // deepest single-enemy peek sequence (2=double, 3=triple...)
    const float RevisitOnDeg = 2.5f;    // tight LOCK cone: the aim must sit precisely on the body for
                                        // ~1s = a genuine STOP, not a slow pan that grazes a 5deg cone.
                                        // A pan fast enough to be a pan can't hold 2.5deg for a full second.
    const float RevisitOffDeg = 20f;    // must clearly leave (the glance away), not just drift
    const float RevisitWindow = 4f;     // FAST: 1s park + a quick glance (<=2s) + 1s re-park. A real
                                        // wallhack check is rapid; a slow "happened to look back" is not it.
    const int RevisitDwellPolls = 20;   // must PARK on the unseen enemy for ~1s BOTH times — deliberate,
                                        // not a sweep/cluster. This is the whole "specific enough" fix.
    const int RevisitMaxEnemiesAlive = 2; // CLUTCH gate: only in 1vX/2vX. With 10 enemies behind walls
                                          // "aim at an unseen enemy" is trivial geometry; with 1-2 left
                                          // there is no team spread to explain a precise double-park.
    const float RevisitSilentSpeed = 140f; // AUDIBILITY gate: enemy speed (u/s) below which no footstep is
                                           // heard — still OR sneaking. Above it the observer could have
                                           // heard them, so it is not a "no information" case. Kills the
                                           // game-sense-via-sound confound that a still-only gate missed.

    // wallhack.follow: sustained tracking of a MOVING unspotted enemy — the view stays on the enemy
    // (err < cone) for >=3s while the enemy MOVES (active tracking, not a static hold). Longer = more
    // certain. "swept" = bearing the view followed (a straight pre-aim sweeps little; following a turning
    // enemy sweeps a lot — the part pre-aim can't fake).
    var followState = new Dictionary<(int obs, int enemy), (float start, int startTick, float lastBearing, float swept, float lastTime)>();
    var maxFollowMs = new Dictionary<int, float>();
    var maxFollowSweep = new Dictionary<int, float>();
    var maxFollowTick = new Dictionary<int, int>();
    var followStartPos = new Dictionary<(int obs, int enemy), Vector3>();  // enemy pos at follow start (diagnostic)
    const float FollowCone = 5f;         // view must stay within this of the enemy
    const float FollowMoveSpeed = 60f;   // enemy must be MOVING (u/s), not a static hold
    const float FollowMinMs = 3000f;     // >=3s sustained to count
    const float FollowMinDisplacement = 300f; // enemy must have actually GONE somewhere (net units) — a
                                              // taser/eco standoff (both waiting either side of a wall) is
                                              // near-stationary, and instantaneous speed false-positives on
                                              // its tiny shifts. Net displacement kills that.

    void FlushSpray(int s)
    {
        if (sprayCurve.TryGetValue(s, out var curve) && curve.Count >= MinSprayShots &&
            sprayWeapon.TryGetValue(s, out var w))
        {
            var key = (s, w);
            if (!spraysByWeapon.TryGetValue(key, out var list))
                spraysByWeapon[key] = list = new List<(float, float)[]>();
            list.Add(curve.ToArray());
        }
        sprayCurve.Remove(s);
    }

    int sequence = 0;
    const float BurstWindowSeconds = 0.25f;

    float roundStartTime = 0f;
    int roundNumber = 0;
    float Now() => demo.CurrentDemoTick.Value / (float)Math.Max(1, CsDemoParser.TickRate);
    demo.Source1GameEvents.RoundStart += _ => { roundStartTime = Now(); roundNumber++; };

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
        if (e.Player is null) return;
        int vs2 = (int)e.Player.EntityIndex.Value - 1;
        if (vs2 >= 0) lastHurt[(s2, vs2)] = (e.DmgHealth, e.Health + e.DmgHealth);   // dmg dealt, victim HP before
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
        int seenTicks = 0, aliveTicks = 0;   // --revisit-detail: how much of the run-up was spotted vs blind
        int limit = Math.Min(PreKillTicks, aTr.Count);
        for (int i = 0; i < limit; i++)
        {
            var a = aTr[i];
            if (!a.Alive) break;
            aliveTicks++;
            if (VictimSeenByAttacker(vSlot, a.Sequence, aSlot)) { seenTicks++; continue; }   // spotted: legitimate visibility
            // aim error to the victim's position at the SAME tick (the "on-target" axis)
            if (vTr.TryGetBySequence(a.Sequence, out var vs) && vs.Alive)
            {
                var eye = a.Origin + new Vector3(0f, 0f, 64f);
                errSum += Geometry.NearestBodyAimError(eye, a.Angles, vs.Origin);
                errN++;
            }
            // view angular speed between consecutive ticks (the "stillness" axis). Componentwise
            // deltas, NOT AngleBetween: acos-of-dot has a float-precision floor of ~0.03deg/tick
            // (~2deg/s of phantom motion) that made a bit-frozen hand read the same as a slowly
            // drifting one — compressing exactly the region the signature lives in.
            if (i + 1 < limit)
            {
                var older = aTr[i + 1];
                if (a.Sequence - older.Sequence == 1)
                {
                    float dt = a.Time - older.Time;
                    if (dt > 0f)
                    {
                        float dy = a.Angles.Yaw - older.Angles.Yaw; dy -= 360f * MathF.Round(dy / 360f);
                        float dp = a.Angles.Pitch - older.Angles.Pitch;
                        speedSum += MathF.Sqrt(dy * dy + dp * dp) / dt; speedN++;
                    }
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
        // Peak, not just mean: one frozen-blind kill in an otherwise average session (the flagship
        // case: sig 0.24 vs a 0.00 aggregate) vanishes in an average but survives as a max.
        killWallMax[aSlot] = MathF.Max(killWallMax.GetValueOrDefault(aSlot), signature);

        // Per-kill component row — every gate the aligned score will need, exported RAW. The baked
        // scalar above ranks the setting (parked crosshair), not the anomaly (parked in the dark on
        // a person); the discriminators live in these components. Exporting them per kill means any
        // candidate formula is a pandas expression over the archive, not a 2h re-parse.
        var attTeam = teams.GetValueOrDefault(aSlot);
        ulong mateMask = 0UL;
        foreach (var kv in teams)
            if (kv.Key != aSlot && kv.Value == attTeam) mateMask |= 1UL << kv.Key;
        int teamSeen = 0;
        float pathLen = 0f, netDisp = 0f;
        Vector3 vShot = default, vOld = default, aShot = default;
        bool haveShot = false, haveOld = false;
        for (int i = 0; i < limit; i++)
        {
            var a = aTr[i];
            if (!a.Alive) break;
            if ((VictimMaskAt(vSlot, a.Sequence) & mateMask) != 0UL) teamSeen++;
            if (vTr.TryGetBySequence(a.Sequence, out var vs) && vs.Alive)
            {
                if (!haveShot) { vShot = vs.Origin; aShot = a.Origin; haveShot = true; }
                if (haveOld) pathLen += Vector3.Distance(vOld, vs.Origin);
                vOld = vs.Origin; haveOld = true;
            }
        }
        if (haveShot && haveOld) netDisp = Vector3.Distance(vOld, vShot);
        // Freeze run: walk back from the shot while the crosshair barely moved and ticks are
        // contiguous. 0.15deg threshold sits safely above the acos noise floor (~0.05deg).
        int frozen = 0;
        for (int i = 1; i < aTr.Count; i++)
        {
            if (aTr[i - 1].Sequence - aTr[i].Sequence != 1) break;
            if (Geometry.AngleBetween(aTr[i].Angles, aTr[i - 1].Angles) > 0.15f) break;
            frozen = i;
        }
        float killT = Now();
        int shotsInHold = fireTimes.TryGetValue(aSlot, out var aft)
            ? aft.Count(t => t >= killT - 2.75f && t <= killT + 0.05f) : -1;
        float sinceAtt = lastSpottedAt.TryGetValue((aSlot, vSlot), out var la) ? killT - la : -1f;
        float sinceMate = float.MaxValue;
        foreach (var kv in teams)
            if (kv.Key != aSlot && kv.Value == attTeam && lastSpottedAt.TryGetValue((kv.Key, vSlot), out var lm))
                sinceMate = MathF.Min(sinceMate, killT - lm);
        if (sinceMate == float.MaxValue) sinceMate = -1f;
        int blind = aliveTicks - seenTicks;
        float frozenMs = frozen * (1f / CsDemoParser.TickRate) * 1000f;

        // Gated signature: the raw sig ranks the SETTING (parked crosshair near an unseen enemy);
        // these factors apply the project's exculpation principle — legal information kills the
        // score, absence of it leaves the score UNCHANGED. Factors only ever damp (<=1), never
        // amplify, so gatedSig <= sig by construction and an un-exculpated kill keeps its rank.
        //   - saw the victim within 2s  -> ordinary sighted fight, zeroed
        //   - a teammate had eyes in the run-up -> callout channel existed, heavy damp
        //   - victim audible (running >=180u/s; walk ~130 silent) -> sound channel existed
        //   - sprayed through the hold -> the timing claim dilutes per speculative shot
        // Constants are game facts (run/walk speeds) or principle, not fits — but UNVALIDATED as
        // a ranking until the archive distribution is read. Freeze/distance stay out: they belong
        // to the clip card, and amplifying would break the exculpation-only contract.
        float victimSpeed = aliveTicks > 0 ? pathLen / (aliveTicks / (float)CsDemoParser.TickRate) : 0f;
        float audibleDamp = victimSpeed >= 180f ? 0.1f :
                            victimSpeed <= 130f ? 1f : 1f - 0.9f * (victimSpeed - 130f) / 50f;
        float gatedSig =
            (sinceAtt >= 0f && sinceAtt < 2f) ? 0f :
            signature
            * (blind / (float)Math.Max(1, aliveTicks))
            * (teamSeen == 0 ? 1f : 0.3f)
            * audibleDamp
            * (1f / Math.Max(1, shotsInHold));

        kills.Add(new KillRow(
            att.SteamID, att.PlayerName ?? "?", victim.SteamID, victim.PlayerName ?? "?",
            roundNumber, demo.CurrentDemoTick.Value, w, e.Headshot,
            lastHurt.TryGetValue((aSlot, vSlot), out var lhd) ? lhd.dmg : -1,
            meanSpeed, meanErr, signature, blind, aliveTicks, teamSeen,
            pathLen, netDisp, frozenMs, shotsInHold, sinceAtt, sinceMate,
            Vector3.Distance(aShot, vShot), gatedSig));

        // --revisit-detail <steamId>: dump this attacker's kills for human review.
        if (revisitTarget != 0 && att.SteamID == revisitTarget)
        {
            Console.WriteLine($"  [KILL] round {roundNumber}, {killT - roundStartTime:F0}s in  tick={demo.CurrentDemoTick.Value,-8} " +
                $"-> {victim.PlayerName,-16} {w,-14} {(e.Headshot ? "HEADSHOT" : "body")}  " +
                $"dmg {(lastHurt.TryGetValue((aSlot, vSlot), out var lh) ? $"{lh.dmg} (of {lh.hpBefore}hp)" : "?")}  " +
                $"unseen {blind}/{aliveTicks} ticks  stillness {meanSpeed:F0}deg/s  onTarget {meanErr:F1}deg  sig {signature:F3}");
            Console.WriteLine($"         team-saw-victim {teamSeen}/{aliveTicks} ticks   " +
                $"victim moved {pathLen:F0}u (net {netDisp:F0}u) during run-up   " +
                $"crosshair frozen {frozenMs:F0}ms   " +
                $"shots during 2.75s hold: {shotsInHold}   " +
                $"@shot att=({aShot.X:F0},{aShot.Y:F0}) victim=({vShot.X:F0},{vShot.Y:F0})");

            // For the blind kills, print the run-up tick by tick (oldest -> shot). aimErr = angle from
            // the attacker's crosshair to where the victim ACTUALLY was that tick; dAim = how far the crosshair
            // itself moved. A frozen crosshair that the victim walks INTO shows dAim~0 with aimErr
            // falling to ~0 only at the end (trigger-like). A crosshair already parked on the body shows
            // aimErr small the whole way. Either way dAim~0 for seconds = a human is not doing this.
            if (blind >= 32)
            {
                string SinceStr(float x) => x < 0f || x > 1e8f ? "NEVER this demo" :
                    (killT - x < roundStartTime ? $"{x:F1}s (previous round!)" : $"{x:F1}s");
                Console.WriteLine($"         attacker last SAW victim: {SinceStr(sinceAtt)}   any teammate: {SinceStr(sinceMate)}");

                // Full 4s window (every 8 ticks): the earlier 1s dump could not test "heard him
                // RUN toward the smoke then park". vSpd = victim speed (u/s) — running ~250 is loud,
                // shift/crouch <~100 silent. dYaw/dPitch printed RAW (AngleBetween's acos hits a
                // float-precision floor ~0.03deg that reads as motion where there is none).
                for (int i = ((aTr.Count - 1) / 8) * 8; i >= 0; i -= 8)
                {
                    var a = aTr[i];
                    if (!a.Alive) continue;
                    string spot = i < MaskRing ? (VictimSeenByAttacker(vSlot, a.Sequence, aSlot) ? "SPOTTED" : "blind") : "  ?";
                    float aimErr = -1f, vSpd = -1f;
                    if (vTr.TryGetBySequence(a.Sequence, out var vs) && vs.Alive)
                    {
                        aimErr = Geometry.NearestBodyAimError(a.Origin + new Vector3(0f, 0f, 64f), a.Angles, vs.Origin);
                        if (vTr.TryGetBySequence(a.Sequence - 8, out var vs8) && vs8.Alive && vs.Time > vs8.Time)
                            vSpd = Vector3.Distance(vs.Origin, vs8.Origin) / (vs.Time - vs8.Time);
                    }
                    float dYaw = 0f, dPitch = 0f;
                    if (i + 8 < aTr.Count && aTr[i + 8].Alive)
                    {
                        dYaw = a.Angles.Yaw - aTr[i + 8].Angles.Yaw; dYaw -= 360f * MathF.Round(dYaw / 360f);
                        dPitch = a.Angles.Pitch - aTr[i + 8].Angles.Pitch;
                    }
                    Console.WriteLine($"      t-{i * (1f / CsDemoParser.TickRate) * 1000f,5:F0}ms  " +
                        $"aimErr {aimErr,5:F1}deg  dYaw {dYaw,7:F3}  dPitch {dPitch,7:F3}  vSpd {vSpd,4:F0}u/s  {spot}{(i == 0 ? "   <-- SHOT" : "")}");
                }
            }
        }
    };

    // Shots: run the same aimbot/triggerbot the plugin runs, against the tick buffers.
    demo.Source1GameEvents.WeaponFire += e =>
    {
        var shooter = e.Player;
        if (shooter is null || shooter.SteamID == 0 || !shooter.PawnIsAlive) return;
        int slot = (int)shooter.EntityIndex.Value - 1;
        shotCount[slot] = shotCount.GetValueOrDefault(slot) + 1;
        if (!fireTimes.TryGetValue(slot, out var ftl)) fireTimes[slot] = ftl = new List<float>();
        ftl.Add(Now());
        if (slot < 0 || !trackers.TryGetValue(slot, out var shooterTracker)) return;

        // Recoil-consistency: fold this shot into the current weapon-specific spray. A new weapon or
        // a gap > SprayGapSeconds closes the previous spray and starts a fresh one.
        {
            string weap = (e.Weapon ?? "").ToLowerInvariant();
            float ts = Now();
            bool cont = sprayLastFire.TryGetValue(slot, out var slf) && ts - slf < SprayGapSeconds &&
                        sprayWeapon.GetValueOrDefault(slot) == weap;
            if (!cont) { FlushSpray(slot); sprayWeapon[slot] = weap; sprayCurve[slot] = new List<(float pitch, float yaw)>(); }
            sprayLastFire[slot] = ts;
            if (shooterTracker.TryLatest(out var sa)) sprayCurve[slot].Add((sa.Angles.Pitch, sa.Angles.Yaw));
        }

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

        // Degenerate-range guard: stacked/point-blank targets (noclip horseplay, spawn stacking)
        // make every angular metric meaningless — AimErrorTo collapses to exactly 0 at ~zero
        // distance, which is how one workshop session produced 11/11 fake bone-lock spikes and
        // saturated dwell. No aim measurement below 64u (~1.2m).
        if (trackers.TryGetValue(nearestId, out var nearTr) && nearTr.TryLatest(out var nearS) &&
            Vector3.Distance(atFire.Origin, nearS.Origin) < 64f) return;

        // Reaction timing: only first-of-burst shots that actually landed near a target count as a
        // "decision", and only when the target is CURRENTLY seen with a recent unseen->seen edge in
        // the buffer. No edge within ~1.5s = ongoing engagement (or never seen), not an appearance.
        if (!burst && nearestErr <= 5f && VictimSeenByAttacker(nearestId, atFire.Sequence, slot) &&
            trackers.TryGetValue(nearestId, out var reactTgt))
        {
            int walk = Math.Min(96, shooterTracker.Count);   // 1.5s; mask ring holds 2s, stay inside it
            for (int i = 1; i < walk; i++)
            {
                var older = shooterTracker[i];
                if (shooterTracker[i - 1].Sequence - older.Sequence != 1 || !older.Alive) break;
                if (!VictimSeenByAttacker(nearestId, older.Sequence, slot))
                {
                    var rise = shooterTracker[i - 1];        // first SEEN tick after the unseen run
                    float ms = (atFire.Time - rise.Time) * 1000f;
                    float flick = -1f;
                    if (reactTgt.TryGetBySequence(rise.Sequence, out var vsr) && vsr.Alive)
                        flick = Geometry.NearestBodyAimError(rise.Origin + new Vector3(0f, 0f, 64f), rise.Angles, vsr.Origin);
                    reactionRows.Add((slot, ms, flick));
                    break;
                }
            }
        }

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

        // Bone-lock axis: angle from the view vector AT FIRE to the target's head CENTER (feet+64,
        // standing approximation — live gets real bones). Measured on the aim, not the bullet:
        // weapon spread is noise the cheat does not control. A raw aimbot computes the exact bone
        // angle then quantizes -> headErr pinned <= ~half a quant step (0.022deg) every locked shot.
        // A human aiming at the head scatters over ~100 quant cells. The tell is the repeated spike
        // at zero, never one shot. First-of-burst + on-target only, same gating as everything else.
        float headErr = -1f;
        if (!burst && nearestErr <= 5f && trackers.TryGetValue(nearestId, out var headTgt) &&
            headTgt.TryLatest(out var headSample) && headSample.Alive)
        {
            headErr = Geometry.AimErrorTo(eye, atFire.Angles, headSample.Origin + new Vector3(0f, 0f, 64f));
            headRows.Add((slot, headErr));
        }

        shots.Add(new ShotRow(
            steamIds.GetValueOrDefault(slot), names.GetValueOrDefault(slot, "?"),
            nearestErr, fromTarget >= 0 ? switchMs : -1f, fromTarget >= 0 ? switchDeg : -1f,
            onTargetMs, viewRate, burst ? 0 : 1, headErr,
            demo.CurrentDemoTick.Value, steamIds.GetValueOrDefault(nearestId)));
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

            // Spinbot: max SUSTAINED yaw rate (deg/s), guarded against the artifacts that fake a
            // spin — angle wraparound, teleport/respawn/tick-gap. Only consecutive ticks (Δseq==1),
            // alive, same rough position, and Δt in a sane tick range count; a run must last
            // >=SpinRunTicks before its rate is recorded. This is the auto-ban-candidate axis, so it
            // must never read a lag jump as 2000°/s. Whole-demo max survives the ring buffer.
            if (p.PawnIsAlive && tr.Count >= 2)
            {
                var cur = tr[0]; var prev = tr[1];
                float dt = cur.Time - prev.Time;
                bool contiguous = cur.Sequence - prev.Sequence == 1 && dt > 0f && dt <= 0.05f
                    && Vector3.Distance(cur.Origin, prev.Origin) < 200f; // no teleport/respawn
                if (contiguous)
                {
                    float dyaw = cur.Angles.Yaw - prev.Angles.Yaw;
                    dyaw -= 360f * MathF.Round(dyaw / 360f);            // unwrap 359->1
                    float rate = MathF.Abs(dyaw) / dt;
                    const float SpinFloor = 1200f;                     // deg/s; well above any human flick
                    const int SpinRunTicks = 6;                        // sustained, not a momentary flick
                    if (rate >= SpinFloor && MathF.Sign(dyaw) == (spinDir.GetValueOrDefault(s, 0) == 0 ? MathF.Sign(dyaw) : spinDir[s]))
                    {
                        spinDir[s] = (int)MathF.Sign(dyaw);
                        int run = spinRun.GetValueOrDefault(s) + 1;
                        spinRun[s] = run;
                        float runMin = run == 1 ? rate : MathF.Min(spinRunMin.GetValueOrDefault(s, rate), rate);
                        spinRunMin[s] = runMin;
                        if (run >= SpinRunTicks && runMin > spinMax.GetValueOrDefault(s))
                            spinMax[s] = runMin;   // the sustained floor of the run, not its peak
                    }
                    else { spinRun[s] = 0; spinDir[s] = 0; }
                }
                else { spinRun[s] = 0; spinDir[s] = 0; }
            }

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

            // Continuous "last seen" per (observer, victim) pair — the mask ring only holds 2s, but
            // "when did the attacker LAST legitimately see this enemy" needs whole-round memory.
            for (ulong bits = packed; bits != 0; bits &= bits - 1)
                lastSpottedAt[(System.Numerics.BitOperations.TrailingZeroCount(bits), s)] = now;
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

            // Clutch gate for wallhack.revisit: how many enemies are still alive right now. A precise
            // double-park is only telling when there are few enemies to coincidentally cover.
            int aliveEnemies = players.Count(e => !ReferenceEquals(e, observer) && e.PawnIsAlive &&
                e.CSTeamNum != team &&
                (e.CSTeamNum == CSTeamNumber.Terrorist || e.CSTeamNum == CSTeamNumber.CounterTerrorist));

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

                // wallhack.revisit: in a clutch, does the observer PARK ~1s on a SILENT (still or
                // sneaking) unseen enemy, glance away, and re-acquire the SAME enemy ~1s? No sound + no
                // sight = no legitimate information; repeating it (depth: 2=double, 3=triple...) is the tell.
                if (aliveEnemies <= RevisitMaxEnemiesAlive)
                {
                    var rk = (slot, enemyId);
                    var rs = revisitState.GetValueOrDefault(rk);

                    // Enemy speed from its per-tick track: is it silent (still/sneaking) right now?
                    float enemySpeed = 0f;
                    if (trackers.TryGetValue(enemyId - 1, out var eTrk) && eTrk.Count >= 2)
                    {
                        float dt2 = eTrk[0].Time - eTrk[1].Time;
                        if (dt2 > 0f) enemySpeed = Vector3.Distance(eTrk[0].Origin, eTrk[1].Origin) / dt2;
                    }

                    // Audible (running), or aged out -> the "no information" premise breaks; reset.
                    if (enemySpeed >= RevisitSilentSpeed || (rs.phase != 0 && now - rs.tOn1 > RevisitWindow))
                        rs = default;

                    if (enemySpeed < RevisitSilentSpeed)
                    {
                        bool onT = err < RevisitOnDeg;
                        if (rs.phase == 0)
                        {
                            // First park: crosshair must REST ~1s on the silent enemy, not sweep past.
                            if (onT) { int d = rs.dwell + 1; bool ok = d >= RevisitDwellPolls; rs = (ok ? 1 : 0, rs.dwell == 0 ? now : rs.tOn1, rs.dwell == 0 ? feet : rs.pos, d, ok ? 1 : 0); }
                            else rs = default;
                        }
                        else if (rs.phase == 1) { if (err > RevisitOffDeg) rs = (2, rs.tOn1, rs.pos, 0, rs.depth); }  // glanced away
                        else // phase 2: re-acquiring the SAME silent enemy?
                        {
                            if (onT)
                            {
                                int d = rs.dwell + 1;
                                if (d >= RevisitDwellPolls)
                                {
                                    int depth = rs.depth + 1;   // 2 = double-peek, 3 = triple ...
                                    revisitCount[slot] = revisitCount.GetValueOrDefault(slot) + 1;
                                    if (depth > maxDepth.GetValueOrDefault(slot)) maxDepth[slot] = depth;
                                    if (revisitTarget != 0 && steamIds.GetValueOrDefault(slot) == revisitTarget)
                                        Console.WriteLine($"  [PEEK x{depth}] t={now,7:F1}s tick={demo.CurrentDemoTick.Value,-8} " +
                                            $"{names.GetValueOrDefault(slot, "?"),-16} -> silent enemy @ ({feet.X:F0},{feet.Y:F0},{feet.Z:F0}) spd={enemySpeed:F0} err={err:F1}deg");
                                    rs = (1, now, feet, 0, depth);
                                }
                                else rs = (2, rs.tOn1, rs.pos, d, rs.depth);
                            }
                            else rs = (2, rs.tOn1, rs.pos, 0, rs.depth);
                        }
                    }
                    revisitState[rk] = rs;
                }

                // wallhack.follow: is the view actively TRACKING a MOVING unseen enemy? Extend a window
                // while [unspotted + enemy moving + on target]; record the LONGEST, with bearing swept.
                // Longer = more certain (a legit player loses a hidden enemy; a wallhacker stays glued).
                {
                    float espeed = 0f;
                    if (trackers.TryGetValue(enemyId - 1, out var eTf) && eTf.Count >= 2)
                    { float dtf = eTf[0].Time - eTf[1].Time; if (dtf > 0f) espeed = Vector3.Distance(eTf[0].Origin, eTf[1].Origin) / dtf; }
                    var fk = (slot, enemyId);
                    var fs = followState.GetValueOrDefault(fk);
                    bool onTgtMoving = espeed > FollowMoveSpeed && err < FollowCone;
                    // Contiguous only if this pair was updated last poll — a gap means the enemy was
                    // spotted/skipped/off-target in between, so the follow is NOT continuous.
                    bool contiguous = fs.start > 0f && now - fs.lastTime <= pollInterval * 2f;

                    if (fs.start > 0f && (!onTgtMoving || !contiguous))   // the follow ended — record it
                    {
                        float dur = (fs.lastTime - fs.start) * 1000f;
                        float disp = Vector3.Distance(feet, followStartPos.GetValueOrDefault(fk));   // net enemy movement
                        if (dur >= FollowMinMs && disp >= FollowMinDisplacement && dur > maxFollowMs.GetValueOrDefault(slot))
                        {
                            maxFollowMs[slot] = dur; maxFollowSweep[slot] = fs.swept; maxFollowTick[slot] = fs.startTick;
                            if (revisitTarget != 0 && steamIds.GetValueOrDefault(slot) == revisitTarget)
                                Console.WriteLine($"  [FOLLOW {dur / 1000f:F1}s] round {roundNumber}, {fs.start - roundStartTime:F0}s in  tick={fs.startTick,-8} " +
                                    $"{names.GetValueOrDefault(slot, "?"),-14} -> enemy '{names.GetValueOrDefault(enemyId - 1, "?")}' moved {disp:F0}u, bearing swept {fs.swept:F0}deg");
                        }
                        fs = default;
                    }

                    if (onTgtMoving)   // start a fresh follow or extend the current one
                    {
                        if (fs.start == 0f) { fs = (now, demo.CurrentDemoTick.Value, bearingYaw, 0f, now); followStartPos[(slot, enemyId)] = feet; }
                        else { float db = bearingYaw - fs.lastBearing; db -= 360f * MathF.Round(db / 360f); fs = (fs.start, fs.startTick, bearingYaw, fs.swept + MathF.Abs(db), now); }
                    }
                    followState[fk] = fs;
                }

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

    // Close any spray still open at demo end.
    foreach (var s in sprayCurve.Keys.ToList()) FlushSpray(s);

    // Per-player recoil consistency: for each weapon with >=2 qualifying sprays, average the
    // per-shot-index cross-spray SPREAD (deg) of the compensation curve (Δ from the spray's first
    // shot; yaw unwrapped). Take the player's MOST consistent weapon (min spread) — a script is
    // near-identical every spray, so its best weapon sits far below any human's. -1 = too few sprays.
    var recoilConsist = new Dictionary<int, float>();   // raw cross-spray spread (deg) of the best weapon
    var recoilPull = new Dictionary<int, float>();        // how far they actually compensated (deg) — the scale
    var recoilRatio = new Dictionary<int, float>();       // spread / pull: a weak spray fakes a low absolute spread
    var recoilSprays = new Dictionary<int, int>();
    foreach (var g in spraysByWeapon.GroupBy(kv => kv.Key.slot))
    {
        int slot = g.Key;
        int total = 0;
        float bestRatio = float.NaN, bestSpread = 0f, bestPull = 0f;
        foreach (var kv in g)
        {
            var sprays = kv.Value;
            total += sprays.Count;
            if (sprays.Count < 2) continue;
            int L = Math.Min(RecoilCurveLen, sprays.Min(s => s.Length));
            if (L < 2) continue;

            // Δ-from-start curves, yaw unwrapped to [-180,180] so a spray that crosses ±180 is fine.
            var curves = sprays.Select(s =>
            {
                var d = new (float p, float y)[L];
                for (int k = 0; k < L; k++)
                {
                    float dp = s[k].pitch - s[0].pitch;
                    float dy = s[k].yaw - s[0].yaw;
                    dy -= 360f * MathF.Round(dy / 360f);
                    d[k] = (dp, dy);
                }
                return d;
            }).ToList();

            float spreadSum = 0f, pullSum = 0f; int idxN = 0, nn = curves.Count;
            for (int k = 1; k < L; k++)
            {
                float mp = 0f, my = 0f;
                foreach (var c in curves) { mp += c[k].p; my += c[k].y; }
                mp /= nn; my /= nn;
                float v = 0f;
                foreach (var c in curves) { float dp = c[k].p - mp, dy = c[k].y - my; v += dp * dp + dy * dy; }
                spreadSum += MathF.Sqrt(v / nn);
                pullSum += MathF.Sqrt(mp * mp + my * my);   // magnitude of the MEAN compensation at index k
                idxN++;
            }
            if (idxN == 0) continue;
            float spread = spreadSum / idxN;
            float pull = pullSum / idxN;
            // Normalise: a short/weak spray moves little, so a small ABSOLUTE spread means nothing.
            // ratio = consistency RELATIVE to how much they actually compensated. Script -> tiny; a
            // barely-sprayed human -> not tiny even if their absolute spread looks low.
            float ratio = spread / MathF.Max(pull, 0.5f);
            if (float.IsNaN(bestRatio) || ratio < bestRatio) { bestRatio = ratio; bestSpread = spread; bestPull = pull; }
        }
        recoilSprays[slot] = total;
        if (!float.IsNaN(bestRatio)) { recoilConsist[slot] = bestSpread; recoilPull[slot] = bestPull; recoilRatio[slot] = bestRatio; }
    }

    // The spotted->shot reaction cloud, printed per demo when there is anything to show. Pre-aimed
    // shots (crosshair already <=3deg at the edge) are anticipation, not reaction — kept separate.
    if (printBlocks && reactionRows.Count > 0)
    {
        var genuine = reactionRows.Where(r => r.flickDeg > 3f).ToList();
        var preAimed = reactionRows.Count - genuine.Count;
        Console.WriteLine($"\n=== spotted->shot timing ({Path.GetFileName(file)}): {reactionRows.Count} decisions " +
                          $"({preAimed} pre-aimed <=3deg excluded from cloud) ===");
        if (genuine.Count > 0)
        {
            var sorted = genuine.Select(r => r.ms).OrderBy(x => x).ToList();
            float P(double p) => sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
            Console.WriteLine($"  cloud (flick>3deg): n={sorted.Count}  p10 {P(.10):F0}ms  p50 {P(.50):F0}ms  p90 {P(.90):F0}ms  min {sorted[0]:F0}ms");
            foreach (var g in reactionRows.Where(r => r.flickDeg > 3f && r.ms < 150f))
                Console.WriteLine($"  [FAST] {names.GetValueOrDefault(g.slot, "?"),-20} {g.ms,5:F0}ms  flick {g.flickDeg,5:F1}deg   <- below human floor, review");
            foreach (var grp in genuine.GroupBy(r => r.slot).Where(g => g.Count() >= 3).OrderBy(g => g.Min(r => r.ms)))
            {
                var s = grp.Select(r => r.ms).OrderBy(x => x).ToList();
                Console.WriteLine($"    {names.GetValueOrDefault(grp.Key, "?"),-20} n={s.Count,3}  median {s[s.Count / 2],5:F0}ms  min {s[0],5:F0}ms");
            }
        }
    }

    // Head-precision floor: per player, the distribution of head-center aim error at fire.
    // human = broad hump ~0.3-1deg; raw aimbot = spike at <=0.05deg (one quant step is 0.044).
    // The spike SHARE is the statistic (mixture-robust: a toggler's legit shots cannot dilute it).
    if (printBlocks && headRows.Count > 0)
    {
        Console.WriteLine($"\n=== head-center precision at fire ({Path.GetFileName(file)}): {headRows.Count} on-target first shots ===");
        foreach (var grp in headRows.GroupBy(r => r.slot).Where(g => g.Count() >= 5)
                                    .OrderBy(g => g.Select(r => r.err).OrderBy(x => x).ElementAt(g.Count() / 2)))
        {
            var s = grp.Select(r => r.err).OrderBy(x => x).ToList();
            int spike = s.Count(e => e <= 0.05f);
            Console.WriteLine($"    {names.GetValueOrDefault(grp.Key, "?"),-20} n={s.Count,4}  median {s[s.Count / 2],5:F2}deg  " +
                              $"p10 {s[(int)(0.1 * (s.Count - 1))],5:F2}deg  min {s[0],5:F3}deg  spike<=0.05: {spike}" +
                              (spike >= 3 ? "   <- BONE-LOCK?" : ""));
        }
    }

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
        recoilSprays.GetValueOrDefault(kv.Key),
        recoilConsist.GetValueOrDefault(kv.Key, -1f),
        recoilPull.GetValueOrDefault(kv.Key, -1f),
        recoilRatio.GetValueOrDefault(kv.Key, -1f),
        revisitCount.GetValueOrDefault(kv.Key),
        maxDepth.GetValueOrDefault(kv.Key),
        maxFollowMs.GetValueOrDefault(kv.Key),
        maxFollowSweep.GetValueOrDefault(kv.Key),
        maxFollowTick.GetValueOrDefault(kv.Key),
        killWallMax.GetValueOrDefault(kv.Key),
        headRows.Count(r => r.slot == kv.Key),
        headRows.Count(r => r.slot == kv.Key && r.err <= 0.05f),
        spinMax.GetValueOrDefault(kv.Key),
        signals.Where(s => s.Key.slot == kv.Key).ToDictionary(s => s.Key.detector, s => s.Value)))
        .ToList();
    return (playerRows, shots, kills);
}

internal readonly record struct ShotRow(
    ulong SteamId, string Name, float AimErrDeg, float SwitchMs, float SwitchDeg, float OnTargetMs,
    float ViewRateDegPerSec, int BurstStart, float HeadErrDeg, int Tick, ulong TargetId);

// Detector knobs, loaded from --config <json>. The tuned file is the future plugin config:
// calibrated offline against known-legit players, ported verbatim to live. All thresholds sit on
// axes with a validated hard edge or an exculpation-gated score — never raw skill gradients.
internal sealed record SweepConfig
{
    public float DeadaimMin { get; init; } = 0.05f;         // gatedSig floor for a review tip (archive will refine)
    public int BoneLockMinSpikes { get; init; } = 2;        // repeated <=0.05deg head-center locks in one session
    public float AntiRecoilMaxRatio { get; init; } = 0.04f; // below the measured human floor 0.06 (17k sessions)
    public int AntiRecoilMinSprays { get; init; } = 6;
    public float NullTestMinExcess { get; init; } = 0.02f;  // present-over-past share, 2pp
    public int NullTestMinSamples { get; init; } = 2000;
}

// One row per (non-utility) kill: the raw components every candidate deadaim/killWall formula needs.
internal readonly record struct KillRow(
    ulong AttackerId, string AttackerName, ulong VictimId, string VictimName,
    int Round, int Tick, string Weapon, bool Headshot, int Dmg,
    float StillDegS, float OnTgtDeg, float Sig,
    int BlindTicks, int AliveTicks, int TeamSeenTicks,
    float VictimPathU, float VictimNetU, float FrozenMs, int ShotsInHold,
    float SinceAttSawSec, float SinceMateSawSec, float DistU, float GatedSig);

internal sealed record PlayerResult(
    string Demo, ulong SteamId, string Name, float PeakScore, float AliveMinutes,
    int Shots, int Hits, int Headshots,
    int UnseenSamples, int UnseenNow, int UnseenPast,
    int Kills, float KillWall, float KillStill, float KillOnTgt,
    int RecoilSprays, float RecoilConsist, float RecoilPull, float RecoilRatio,
    int Revisits, int MaxPeekDepth, float FollowMs, float FollowSweep, int FollowTick,
    float KillWallMax, int HeadN, int HeadSpike, float SpinMaxYaw, Dictionary<string, int> Signals)
{
    public string Detail =>
        Signals.Count > 0 ? string.Join(", ", Signals.Select(kv => $"{kv.Key}×{kv.Value}")) : "no signals";
}
