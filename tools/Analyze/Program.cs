using System.Globalization;

// Reads shots.csv from DemoReplay and works out where the human ceiling actually sits.
//
// The idea (Pintuz's): the best legit players define what is physically achievable. Anything
// consistently above THEM isn't "unusually good", it's not human. That's a far more honest
// bound than a population percentile, which mostly measures luck by average players.
//
// Two metrics, and they are not equally trustworthy:
//   switchDegPerSec  target switching (kill one enemy, snap to the next). A motor skill with a
//                    real ceiling. This is the strong one.
//   onTargetMs       how long the enemy sat in the crosshair before the shot. Muddier: a large
//                    share of legit shots read ~0 ms because the crosshair was already there
//                    (pre-aim), not because anyone reacted. So the MEDIAN is the signal here,
//                    never the minimum.
//
// usage: osac-analyze <shots.csv> [--ref <steamId>[,<steamId>...]] [--min-shots 50]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: osac-analyze <shots.csv> [--ref id1,id2,...] [--min-shots 50]");
    return 1;
}

string file = args[0];
string? baselinePath = null;
int regularDemos = 20, regularDays = 180;
var refIds = new HashSet<ulong>();
var refNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // names are easier to type
int minShots = 50;
// Matches the CS2CD measurement exactly. At 64 tick one sample is 15.6ms, so 90 deg/s is 1.4 deg
// of travel between samples - fast enough that the crosshair is plainly still moving, low enough
// that it isn't only catching wild flicks.
const float SweepRateDegPerSec = 90f;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--ref" && i + 1 < args.Length)
        foreach (var s in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = s.Trim();
            if (ulong.TryParse(t, out var id)) refIds.Add(id); else refNames.Add(t);
        }
    if (args[i] == "--min-shots" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) minShots = m;
    if (args[i] == "--baseline" && i + 1 < args.Length) baselinePath = args[i + 1];
    if (args[i] == "--regular-demos" && i + 1 < args.Length && int.TryParse(args[i + 1], out var rd)) regularDemos = rd;
    if (args[i] == "--regular-days" && i + 1 < args.Length && int.TryParse(args[i + 1], out var rday)) regularDays = rday;
}

if (!File.Exists(file)) { Console.Error.WriteLine($"no such file: {file}"); return 1; }

var players = new Dictionary<ulong, Player>();
var allSwitches = new List<(float deg, float ms, ulong sid)>();
long rows = 0;

// shots.csv gained a leading demo column, so read the header rather than assuming a layout -
// a run started before that change still has to be analysable when it finishes.
var header = File.ReadLines(file).FirstOrDefault() ?? "";
int off = header.StartsWith("demo,", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

// The sweep-through columns arrived last and are the only ones CS2CD's labelled cheaters actually
// responded to, so find them by name — an older shots.csv simply won't have them.
var cols = header.Split(',').Select((h, i) => (h: h.Trim(), i)).ToDictionary(x => x.h, x => x.i);
int iRate = cols.GetValueOrDefault("viewRateDegPerSec", -1);
int iBurst = cols.GetValueOrDefault("burstStart", -1);
int iErr = cols.GetValueOrDefault("aimErrDeg", off + 2);

foreach (var line in File.ReadLines(file).Skip(1))
{
    var f = line.Split(',');
    if (f.Length < 6 + off) continue;
    if (!ulong.TryParse(f[off], out var sid) || sid == 0) continue;

    float F(int i) => float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1f;
    float switchMs = F(off + 3), switchDeg = F(off + 4), onTargetMs = F(off + 5);

    if (!players.TryGetValue(sid, out var p)) players[sid] = p = new Player { Name = f[off + 1] };
    p.Name = f[off + 1];
    rows++;

    // Only real switches: the shot moved to a different enemy, far enough that the traverse is
    // meaningful, and quickly enough that the number isn't dominated by idle time between shots.
    if (switchMs > 0 && switchMs < 2000 && switchDeg > 15f)
    {
        p.SwitchDegPerSec.Add(switchDeg / (switchMs / 1000f));
        p.Switches.Add((switchDeg, switchMs));
        allSwitches.Add((switchDeg, switchMs, sid));
    }

    if (onTargetMs >= 0) p.DwellMs.Add(onTargetMs);

    // Sweep-through: a burst-opening shot fired while the view was still travelling. On CS2CD's
    // 254 labelled cheaters this was the one metric worth keeping - above the non-cheaters' p99 it
    // caught 15.4% of them and 1.1% of everyone else. Everything else we built (dwell, switch)
    // came back at chance. Whether that threshold survives contact with players who are simply
    // very good is exactly what this archive is here to answer.
    if (iRate >= 0 && iBurst >= 0 && f.Length > Math.Max(iRate, iBurst))
    {
        float rate = F(iRate);
        if (rate >= SweepRateDegPerSec && f[iBurst] == "1")
        {
            p.Sweep++;
            if (F(iErr) is >= 0f and <= 1f) p.SweepHit++;
        }
    }
}

// --- tenure: who has been around for years? ---
// Pintuz's point: the archive can't be assumed clean, but someone who has played the server for
// years across hundreds of sessions without an admin ever banning them is legit with high
// confidence. That gives a reference set of dozens rather than three, so the cluster is stable.
// Meanwhile players who show up for two maps and vanish are exactly the suspect pool.
// Demo filenames carry the date, so tenure comes free from baseline.csv.
var tenure = new Dictionary<ulong, (HashSet<string> demos, string first, string last)>();
if (baselinePath is not null && File.Exists(baselinePath))
{
    foreach (var line in File.ReadLines(baselinePath).Skip(1))
    {
        var f = line.Split(',');
        if (f.Length < 3 || !ulong.TryParse(f[1], out var sid)) continue;
        var demo = f[0].Trim('"');
        if (demo.Length < 8 || !demo[..8].All(char.IsDigit)) continue;
        var date = demo[..8];
        if (!tenure.TryGetValue(sid, out var t))
            tenure[sid] = (new HashSet<string> { demo }, date, date);
        else
        {
            t.demos.Add(demo);
            tenure[sid] = (t.demos,
                string.CompareOrdinal(date, t.first) < 0 ? date : t.first,
                string.CompareOrdinal(date, t.last) > 0 ? date : t.last);
        }
    }
    Console.WriteLine($"Tenure loaded for {tenure.Count} players from {baselinePath}");

    // THE NULL TEST, summed across every demo. For each tick an enemy was NOT spotted by this
    // player, did the crosshair sit on where that enemy is NOW more often than on where they were
    // 1.5 seconds ago? The past position is the control, and it is what makes this different from
    // everything else built here: knowing the map, hearing him, remembering where he ran all put
    // the crosshair near his old position just as readily as his new one. Only tracking the
    // present is unexplainable, and skill does not buy it - so unlike the six level metrics, this
    // one should read the same for a five-year regular as for anyone else.
    var hdr = File.ReadLines(baselinePath).FirstOrDefault() ?? "";
    var bc = hdr.Split(',').Select((h, i) => (h: h.Trim(), i)).ToDictionary(x => x.h, x => x.i);
    if (bc.ContainsKey("unseenSamples"))
    {
        var agg = new Dictionary<ulong, (long samples, long now, long past, string name)>();
        foreach (var line in File.ReadLines(baselinePath).Skip(1))
        {
            var f = SplitCsvLine(line);
            if (f.Length <= bc["unseenPast"] || !ulong.TryParse(f[bc["steamId"]], out var sid) || sid == 0) continue;
            long L(string c) => long.TryParse(f[bc[c]], out var v) ? v : 0;
            var a = agg.GetValueOrDefault(sid);
            agg[sid] = (a.samples + L("unseenSamples"), a.now + L("unseenNow"), a.past + L("unseenPast"),
                        f[bc["name"]].Trim('"'));
        }

        // A ratio needs a denominator that isn't noise: sub-100 "hits on his old position" makes
        // the quotient jump around on nothing. This is the same trap that once put rarely-seen
        // players atop a "lowest dwell" list purely because they had the fewest samples.
        var scored = agg.Where(kv => kv.Value.past >= 100)
                        .Select(kv => (sid: kv.Key, r: (double)kv.Value.now / kv.Value.past, v: kv.Value))
                        .OrderByDescending(x => x.r).ToList();
        Console.WriteLine($"\n=== NULL TEST: aim on unseen enemies, now vs 1.5s ago ({scored.Count:N0} players) ===");
        if (scored.Count > 0)
        {
            var rs = scored.Select(x => x.r).OrderBy(x => x).ToList();
            double RQ(double q) => rs[Math.Min(rs.Count - 1, (int)(q * (rs.Count - 1)))];
            long tn = scored.Sum(x => x.v.now), tp = scored.Sum(x => x.v.past);
            Console.WriteLine($"  pooled: {tn:N0} on his position now vs {tp:N0} on his position 1.5s ago = {(double)tn / tp:F3}x");
            Console.WriteLine($"  per player: p50={RQ(0.5):F2}x  p90={RQ(0.9):F2}x  p99={RQ(0.99):F2}x  max={rs[^1]:F2}x");
            Console.WriteLine($"\n  Highest ratio (min 100 past-hits):");
            Console.WriteLine($"  {"ratio",7}  {"now",7} {"past",7}  {"name",-24} steamId");
            foreach (var x in scored.Take(15))
                Console.WriteLine($"  {x.r,6:F2}x  {x.v.now,7:N0} {x.v.past,7:N0}  {Trunc(x.v.name, 24),-24} {x.sid}");
            Console.WriteLine("""

    Reading this: ~1.00x is the honest answer. It means a player's crosshair sits on an unseen
    enemy's current position exactly as often as on where that enemy was a second and a half ago -
    i.e. they are aiming at places, not at people, which is what map knowledge and sound look like.
    Well above 1.00x means the aim follows the enemy's PRESENT position through a wall, and no
    amount of skill supplies that. If the population p50 isn't ~1.00, this measurement is broken
    and nothing below it can be trusted.
    """);
        }
    }
}

static string[] SplitCsvLine(string line)
{
    var outp = new List<string>();
    var cur = new System.Text.StringBuilder();
    bool q = false;
    foreach (char c in line)
    {
        if (c == '"') q = !q;
        else if (c == ',' && !q) { outp.Add(cur.ToString()); cur.Clear(); }
        else cur.Append(c);
    }
    outp.Add(cur.ToString());
    return outp.ToArray();
}

static int SpanDays(string a, string b)
{
    if (!DateTime.TryParseExact(a, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return 0;
    if (!DateTime.TryParseExact(b, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return 0;
    return (int)(d2 - d1).TotalDays;
}

bool IsRegular(ulong sid) =>
    tenure.TryGetValue(sid, out var t) && t.demos.Count >= regularDemos && SpanDays(t.first, t.last) >= regularDays;

// Resolve any names given to --ref against the SteamIDs actually seen in the file.
if (refNames.Count > 0)
{
    foreach (var (sid, p) in players)
        if (refNames.Contains(p.Name)) refIds.Add(sid);
    var unresolved = refNames.Where(n => !players.Values.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
    foreach (var n in unresolved)
        Console.Error.WriteLine($"[!] --ref \"{n}\" matched no player in the file (in-game name may differ)");
}

Console.WriteLine($"{rows:N0} shots, {players.Count} players\n");

static float Q(List<float> v, double q)
{
    if (v.Count == 0) return float.NaN;
    v.Sort();
    return v[Math.Clamp((int)(q * (v.Count - 1)), 0, v.Count - 1)];
}

var eligible = players.Where(kv => kv.Value.DwellMs.Count >= minShots).ToList();

// --- the ceiling, measured on the reference players ---
if (refIds.Count > 0)
{
    Console.WriteLine("=== Reference players (the human ceiling) ===");
    Console.WriteLine("  name                 shots  switch/s p50  switch/s p99   switch/s max | dwell p50  dwell<30ms");
    foreach (var (sid, p) in players.Where(kv => refIds.Contains(kv.Key)))
        Console.WriteLine(Row(p));

    var refs = players.Where(kv => refIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    if (refs.Count > 0)
    {
        var allSwitch = refs.SelectMany(r => r.SwitchDegPerSec).ToList();
        var allDwell = refs.SelectMany(r => r.DwellMs).ToList();
        Console.WriteLine($"\n  CEILING from {refs.Count} reference player(s):");
        Console.WriteLine($"    target switch: p99 = {Q(allSwitch, 0.99):F0} deg/s, max = {(allSwitch.Count > 0 ? allSwitch.Max() : 0):F0} deg/s");
        Console.WriteLine($"    dwell median : {Q(allDwell, 0.50):F0} ms  (fraction <30ms: {allDwell.Count(d => d < 30) * 100.0 / Math.Max(1, allDwell.Count):F1}%)");
        int rs = refs.Sum(r => r.Sweep), rh = refs.Sum(r => r.SweepHit);
        if (rs > 0)
            Console.WriteLine($"    sweep-through: {100.0 * rh / rs:F1}% on target ({rs:N0} sweep shots)  " +
                              $"[CS2CD cheater threshold 16.1%, cheater p50 4.2%, non-cheater p50 3.3%]");
    }
    Console.WriteLine();
}

// --- population ---
Console.WriteLine("=== Population ===");
var popSwitch = players.Values.SelectMany(p => p.SwitchDegPerSec).ToList();
var popDwell = players.Values.SelectMany(p => p.DwellMs).ToList();
Console.WriteLine($"  target switch deg/s : p50={Q(popSwitch, 0.5):F0}  p90={Q(popSwitch, 0.9):F0}  p99={Q(popSwitch, 0.99):F0}  p99.9={Q(popSwitch, 0.999):F0}  max={(popSwitch.Count > 0 ? popSwitch.Max() : 0):F0}  (n={popSwitch.Count:N0})");
Console.WriteLine($"  dwell ms            : p1={Q(popDwell, 0.01):F0}  p5={Q(popDwell, 0.05):F0}  p25={Q(popDwell, 0.25):F0}  p50={Q(popDwell, 0.5):F0}  p90={Q(popDwell, 0.9):F0}  (n={popDwell.Count:N0})");
Console.WriteLine($"  shots fired <30ms after the crosshair touched: {popDwell.Count(d => d < 30) * 100.0 / Math.Max(1, popDwell.Count):F1}%");

// The only metric CS2CD's 254 labelled cheaters responded to. Its threshold (16.1%) was read off
// random matchmaking players, and the regulars here are nothing like random matchmaking players -
// so print where they actually land. If the good ones clear 16.1% the threshold measures skill,
// not cheating, and the metric joins dwell and target-switch on the pile.
const float Cs2cdThreshold = 16.1f;
var swept = players.Values.Where(p => p.Sweep >= 20).ToList();
if (swept.Count > 0)
{
    var rates = swept.Select(p => 100.0 * p.SweepHit / p.Sweep).OrderBy(x => x).ToList();
    double PQ(double q) => rates[Math.Min(rates.Count - 1, (int)(q * (rates.Count - 1)))];
    int over = rates.Count(r => r > Cs2cdThreshold);
    Console.WriteLine($"\n  sweep-through on-target (burst start, view >={SweepRateDegPerSec:F0} deg/s, {swept.Count:N0} players with 20+ such shots)");
    Console.WriteLine($"    p50={PQ(0.5):F1}%  p90={PQ(0.9):F1}%  p99={PQ(0.99):F1}%  max={rates[^1]:F1}%");
    Console.WriteLine($"    over the CS2CD cheater threshold ({Cs2cdThreshold}%): {over:N0} of {swept.Count:N0} = {100.0 * over / swept.Count:F1}%");
    Console.WriteLine($"      CS2CD for reference: cheaters p50=4.2 p90=16.7 max=59.1 | non-cheaters p50=3.3 p90=9.5 max=33.3");
}

Console.WriteLine($"\n=== Highest sweep-through on-target (min 20 sweep shots) ===");
Console.WriteLine($"  {"rate",7}  {"sweep",6}  {"name",-24} steamId");
foreach (var (sid, p) in players.Where(kv => kv.Value.Sweep >= 20)
         .OrderByDescending(kv => (double)kv.Value.SweepHit / kv.Value.Sweep).Take(15))
    Console.WriteLine($"  {100.0 * p.SweepHit / p.Sweep,6:F1}%  {p.Sweep,6}  {Trunc(p.Name, 24),-24} {sid}");

// --- who looks unlike a human? ---
Console.WriteLine($"\n=== Fastest target switchers (p99 deg/s, min {minShots} shots) ===");
Console.WriteLine("  name                 shots  switch/s p50  switch/s p99   switch/s max | dwell p50  dwell<30ms");
foreach (var (sid, p) in eligible
             .Where(kv => kv.Value.SwitchDegPerSec.Count >= 5)
             .OrderByDescending(kv => Q(kv.Value.SwitchDegPerSec, 0.99)).Take(15))
    Console.WriteLine(Row(p) + (refIds.Contains(sid) ? "  <-- reference" : ""));

Console.WriteLine($"\n=== Lowest dwell median (triggerbot shape, min {minShots} shots) ===");
Console.WriteLine("  name                 shots  switch/s p50  switch/s p99   switch/s max | dwell p50  dwell<30ms");
foreach (var (sid, p) in eligible.OrderBy(kv => Q(kv.Value.DwellMs, 0.50)).Take(15))
    Console.WriteLine(Row(p) + (refIds.Contains(sid) ? "  <-- reference" : ""));

// --- the actual formula: time as a function of ANGLE ---
// deg/s is a ratio and hides the thing that matters: a 5 deg nudge in 20 ms and a 90 deg flick
// in 150 ms are both human, but the second has the higher deg/s. What bounds a human is Fitts-like:
// covering D degrees takes at least T(D) ms, and T grows with D. So bin by angle and look at how
// fast the population actually is in each bin. The low edge of each bin IS the speed limit.
Console.WriteLine("\n=== Time vs angle: the human speed limit (all target switches) ===");
Console.WriteLine("  angle bin      n     fastest   p1      p5      p25     p50    | implied p5 deg/s");
foreach (var (lo, hi) in new[] { (15f, 30f), (30f, 45f), (45f, 60f), (60f, 90f), (90f, 135f), (135f, 180f) })
{
    var ms = allSwitches.Where(x => x.deg >= lo && x.deg < hi).Select(x => x.ms).ToList();
    if (ms.Count < 5) { Console.WriteLine($"  {lo,3:F0}-{hi,3:F0} deg  {ms.Count,5}   (too few)"); continue; }
    ms.Sort();
    float P(double q) => ms[Math.Clamp((int)(q * (ms.Count - 1)), 0, ms.Count - 1)];
    float mid = (lo + hi) / 2f;
    Console.WriteLine($"  {lo,3:F0}-{hi,3:F0} deg  {ms.Count,5}   {ms[0],7:F0} {P(0.01),7:F0} {P(0.05),7:F0} {P(0.25),7:F0} {P(0.50),7:F0}    | {mid / (P(0.05) / 1000f),8:F0}");
}
Console.WriteLine("""

    Read the columns, not the ratio: if a 60-90 deg switch never happens faster than ~100 ms for
    anyone, then a player doing it in 30 ms is not merely good. Watch out for the small-angle bins
    though - shots inside one spray sit milliseconds apart and the "nearest enemy" can flip between
    two enemies mid-burst, which manufactures fake fast switches at small angles.
    """);

// --- per-player floor on BIG switches ---
// The population floor is only a human floor if the population is clean, and a public server's
// isn't: an undetected cheater in the archive would set the "human" floor to his own time, and
// we'd calibrate against him and then never catch anyone.
//
// Per-player floors dodge that. One cheater cannot move 92 players' median, so the cluster IS
// the human limit, and anyone sitting far below the cluster is the suspect. The contamination
// shows up as the outlier we were looking for. Only switches >90 deg count: they need a real
// physical traverse, so the nearest-enemy assignment can't fake them the way it can at small
// angles (two enemies 90 deg apart can't both be near the crosshair).
const float BigSwitchDeg = 90f;
var floors = new List<(string name, ulong sid, int n, float fastest, float p5)>();
foreach (var (sid, p) in players)
{
    var big = p.Switches.Where(x => x.deg >= BigSwitchDeg).Select(x => x.ms).OrderBy(x => x).ToList();
    if (big.Count < 5) continue;
    floors.Add((p.Name, sid, big.Count, big[0], big[Math.Clamp((int)(0.05 * (big.Count - 1)), 0, big.Count - 1)]));
}

Console.WriteLine($"\n=== Per-player floor on switches >{BigSwitchDeg:F0} deg (min 5 samples) ===");
if (floors.Count == 0)
{
    Console.WriteLine("  (no player has 5+ big switches yet — run more demos)");
}
else
{
    var med = floors.Select(f => f.fastest).OrderBy(x => x).ToList();
    float cluster = med[med.Count / 2];
    Console.WriteLine($"  cluster median of per-player fastest: {cluster:F0} ms   <- the human floor");
    Console.WriteLine($"  players: {floors.Count}\n");
    Console.WriteLine("  name                 big switches   fastest    p5      vs cluster");
    foreach (var f in floors.OrderBy(f => f.fastest).Take(20))
    {
        string flag = f.fastest < cluster * 0.5f ? "  <<< HALF the cluster floor" : "";
        if (refIds.Contains(f.sid)) flag += "  <-- reference";
        Console.WriteLine($"  {Trunc(f.name, 20),-20} {f.n,12}   {f.fastest,7:F0} {f.p5,7:F0}   {f.fastest / cluster,8:F2}x{flag}");
    }
}

if (tenure.Count > 0)
{
    var regulars = floors.Where(f => IsRegular(f.sid)).ToList();
    var dropins = floors.Where(f => !IsRegular(f.sid)).ToList();

    Console.WriteLine($"\n=== Floor by tenure (regular = {regularDemos}+ demos over {regularDays}+ days) ===");
    if (regulars.Count >= 3)
    {
        var rf = regulars.Select(f => f.fastest).OrderBy(x => x).ToList();
        float regFloor = rf[rf.Count / 2];
        Console.WriteLine($"  REGULARS ({regulars.Count}): median fastest = {regFloor:F0} ms, outright fastest = {rf[0]:F0} ms");
        Console.WriteLine($"    ^ these have survived years on the server unbanned. This is the human floor.");
        if (dropins.Count > 0)
        {
            var df = dropins.Select(f => f.fastest).OrderBy(x => x).ToList();
            Console.WriteLine($"  DROP-INS ({dropins.Count}): median fastest = {df[df.Count / 2]:F0} ms, outright fastest = {df[0]:F0} ms");
        }

        Console.WriteLine($"\n  Drop-ins at or below the regulars' floor (leads to review):");
        var leads = dropins.Where(f => f.fastest <= regFloor).OrderBy(f => f.fastest).Take(15).ToList();
        if (leads.Count == 0) Console.WriteLine("    (none)");
        foreach (var f in leads)
        {
            var t = tenure.GetValueOrDefault(f.sid);
            Console.WriteLine($"    {Trunc(f.name, 20),-20} fastest {f.fastest,5:F0} ms ({f.fastest / regFloor:F2}x floor), " +
                              $"{t.demos?.Count ?? 0} demo(s) over {SpanDays(t.first ?? "", t.last ?? "")} days");
        }
    }
    else Console.WriteLine($"  (only {regulars.Count} regulars have 5+ big switches - run more demos, or lower --regular-demos)");
}

Console.WriteLine("""

    A triggerbot fires the instant the crosshair touches, so its dwell median collapses toward
    ~20 ms and most of its shots land under 30 ms. Legit players have plenty of fast shots too
    (pre-aim), but their median sits in the hundreds. Compare medians, not minimums.

    The server is public, so the archive is NOT a clean negative set - cheaters may have played
    without anyone noticing. Trust the cluster, not the minimum, and treat anyone far below the
    cluster as a lead to review rather than a verdict.
    """);

return 0;

string Row(Player p)
{
    var sw = p.SwitchDegPerSec;
    string s50 = sw.Count >= 5 ? $"{Q(sw, 0.50),8:F0}" : "       -";
    string s99 = sw.Count >= 5 ? $"{Q(sw, 0.99),8:F0}" : "       -";
    string smax = sw.Count >= 5 ? $"{sw.Max(),8:F0}" : "       -";
    return $"  {Trunc(p.Name, 20),-20} {p.DwellMs.Count,5}  {s50}      {s99}       {smax} | " +
           $"{Q(p.DwellMs, 0.50),8:F0}   {p.DwellMs.Count(d => d < 30) * 100.0 / Math.Max(1, p.DwellMs.Count),8:F1}%";
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

internal sealed class Player
{
    public string Name = "";
    public readonly List<float> SwitchDegPerSec = new();
    public readonly List<(float deg, float ms)> Switches = new();
    public readonly List<float> DwellMs = new();
    public int Sweep, SweepHit;
}
