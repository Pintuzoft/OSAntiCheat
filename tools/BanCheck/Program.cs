using System.Globalization;
using System.Text.Json;

// Joins the demo-replay output with Steam's ban API to produce labelled ground truth, then
// reports whether our suspicion scores actually SEPARATE banned players from everyone else.
//
// This is the validation study: if the banned and non-banned score distributions overlap, the
// detector doesn't work — no amount of threshold tuning fixes that, and we'd rather know.
//
// Get a free API key at https://steamcommunity.com/dev/apikey
// usage: osac-bancheck <replay.csv> --key <steamApiKey> [--out joined.csv]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: osac-bancheck <replay.csv> --key <steamApiKey> [--out joined.csv]");
    return 1;
}

string csvIn = args[0];
string? key = null, outPath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--key" && i + 1 < args.Length) key = args[i + 1];
    if (args[i] == "--out" && i + 1 < args.Length) outPath = args[i + 1];
}

if (key is null) { Console.Error.WriteLine("missing --key <steamApiKey>"); return 1; }
if (!File.Exists(csvIn)) { Console.Error.WriteLine($"no such file: {csvIn}"); return 1; }

// --- read baseline.csv: demo,steamId,name,peakScore,aliveMinutes,wallhackTrack,... ---
// peakScore is the wrong yardstick: it accumulates, so it mostly ranks playtime. What was
// actually validated against the three admin-banned cheaters is signals per alive-minute, so
// sum the signals and the exposure per player and compare rates.
var rows = new List<Row>();
var totalSignals = new Dictionary<ulong, int>();
var totalMinutes = new Dictionary<ulong, float>();
foreach (var line in File.ReadLines(csvIn).Skip(1))
{
    var f = SplitCsv(line);
    if (f.Count < 4) continue;
    if (!ulong.TryParse(f[1], out var sid) || sid == 0) continue;
    if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)) continue;
    rows.Add(new Row(f[0], sid, f[2], score));

    if (f.Count >= 6)
    {
        if (float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var mins))
            totalMinutes[sid] = totalMinutes.GetValueOrDefault(sid) + mins;
        if (int.TryParse(f[5], out var sig))
            totalSignals[sid] = totalSignals.GetValueOrDefault(sid) + sig;
    }
}

if (rows.Count == 0) { Console.Error.WriteLine("no usable rows"); return 1; }

// Keep each player's highest score across demos — one label per player.
var byPlayer = rows.GroupBy(r => r.SteamId)
    .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.PeakScore).First());
Console.WriteLine($"{rows.Count} player-sessions, {byPlayer.Count} unique SteamIDs");

// --- query Steam's ban API in batches of 100 ---
using var http = new HttpClient();
var bans = new Dictionary<ulong, BanInfo>();
var ids = byPlayer.Keys.ToList();

for (int i = 0; i < ids.Count; i += 100)
{
    var batch = ids.Skip(i).Take(100).ToList();
    string url = "https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/" +
                 $"?key={key}&steamids={string.Join(",", batch)}";
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        foreach (var p in doc.RootElement.GetProperty("players").EnumerateArray())
        {
            if (!ulong.TryParse(p.GetProperty("SteamId").GetString(), out var sid)) continue;
            bans[sid] = new BanInfo(
                p.GetProperty("VACBanned").GetBoolean(),
                p.GetProperty("NumberOfVACBans").GetInt32(),
                p.GetProperty("NumberOfGameBans").GetInt32(),
                p.GetProperty("DaysSinceLastBan").GetInt32());
        }
        Console.WriteLine($"  checked {Math.Min(i + 100, ids.Count)}/{ids.Count}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [!] batch at {i}: {ex.Message}");
    }
}

// --- the separation analysis ---
var banned = new List<Row>();
var clean = new List<Row>();
foreach (var (sid, row) in byPlayer)
{
    var b = bans.GetValueOrDefault(sid);
    if (b is not null && (b.VacBanned || b.GameBans > 0)) banned.Add(row);
    else clean.Add(row);
}

Console.WriteLine($"\n=== Labels ===");
Console.WriteLine($"  banned (VAC or game ban): {banned.Count}");
Console.WriteLine($"  no ban on record:         {clean.Count}");

void Dist(string label, List<Row> set)
{
    if (set.Count == 0) { Console.WriteLine($"  {label}: (none)"); return; }
    var s = set.Select(r => r.PeakScore).OrderBy(x => x).ToList();
    string Q(double q) => s[Math.Min(s.Count - 1, (int)(q * (s.Count - 1)))].ToString("F2");
    Console.WriteLine($"  {label,-22} n={set.Count,-5} median={Q(0.5)}  p90={Q(0.9)}  p99={Q(0.99)}  max={s[^1]:F2}");
}

Console.WriteLine($"\n=== Peak-score distribution (accumulates - mostly ranks playtime) ===");
Dist("banned", banned);
Dist("no ban on record", clean);

// The metric that was actually validated: signals per alive-minute.
void RateDist(string label, List<Row> set, Func<ulong, bool>? extra = null)
{
    var rates = new List<float>();
    float sig = 0, mins = 0;
    foreach (var r in set)
    {
        if (extra is not null && !extra(r.SteamId)) continue;
        float m = totalMinutes.GetValueOrDefault(r.SteamId);
        int g = totalSignals.GetValueOrDefault(r.SteamId);
        if (m < 30f) continue;           // need real exposure before a rate means anything
        rates.Add(g / m);
        sig += g; mins += m;
    }
    if (rates.Count == 0) { Console.WriteLine($"  {label,-34}: (none with 30+ min)"); return; }
    rates.Sort();
    string Q(double q) => rates[Math.Min(rates.Count - 1, (int)(q * (rates.Count - 1)))].ToString("F4");
    Console.WriteLine($"  {label,-34} n={rates.Count,-4} pooled={sig / Math.Max(1, mins):F4}/min  median={Q(0.5)}  p90={Q(0.9)}  max={rates[^1]:F4}");
}

Console.WriteLine($"\n=== wallhack.track signals per alive-minute (the validated metric, 30+ min only) ===");
RateDist("banned", banned);
RateDist("no ban on record", clean);

// A VAC ban can be 20 years old and from another game entirely - Steam's API won't say which.
// Only bans that landed after CS2 shipped can possibly relate to play in this archive.
const int Cs2EraDays = 1050;
Console.WriteLine($"\n=== Same, but only bans from the CS2 era (< {Cs2EraDays} days old) ===");
RateDist("banned (recent)", banned, sid => bans.TryGetValue(sid, out var b) && b.DaysSinceLastBan < Cs2EraDays);
RateDist("no ban on record", clean);

Console.WriteLine($"\n=== Banned players, by score ===");
foreach (var r in banned.OrderByDescending(r => r.PeakScore).Take(30))
{
    var b = bans[r.SteamId];
    Console.WriteLine($"  {r.PeakScore,5:F2}  {r.Name,-24} {r.SteamId}  " +
                      $"vac={b.VacBans} game={b.GameBans} daysSinceBan={b.DaysSinceLastBan}");
}

Console.WriteLine("""

    Reading this honestly:
      * A ban is a reliable POSITIVE label, but "no ban" is a noisy negative — plenty of
        cheaters are never caught, so some "clean" players may in fact be cheating.
      * Check daysSinceLastBan against the demo's date. A ban that landed AFTER the demo
        means the demo likely captures them cheating. A much older ban may be unrelated.
      * What matters is separation: if banned players score no higher than everyone else,
        the detector is not measuring cheating.
    """);

if (outPath is not null)
{
    using var w = new StreamWriter(outPath);
    w.WriteLine("steamId,name,peakScore,vacBanned,vacBans,gameBans,daysSinceLastBan");
    foreach (var (sid, r) in byPlayer.OrderByDescending(kv => kv.Value.PeakScore))
    {
        var b = bans.GetValueOrDefault(sid);
        w.WriteLine($"{sid},{Csv(r.Name)},{r.PeakScore.ToString("F3", CultureInfo.InvariantCulture)}," +
                    $"{b?.VacBanned.ToString() ?? ""},{b?.VacBans.ToString() ?? ""},{b?.GameBans.ToString() ?? ""},{b?.DaysSinceLastBan.ToString() ?? ""}");
    }
    Console.WriteLine($"Wrote {outPath}");
}

return 0;

static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

static List<string> SplitCsv(string line)
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
    return outp;
}

internal sealed record Row(string Demo, ulong SteamId, string Name, float PeakScore);
internal sealed record BanInfo(bool VacBanned, int VacBans, int GameBans, int DaysSinceLastBan);
