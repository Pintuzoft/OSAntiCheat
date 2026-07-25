using System.IO.Compression;
using DemoFile;
using DemoFile.Game.Cs;

// Everything one player did in a demo: chat, connects, every shot, every hurt
// (team damage included), deaths either way. usage: DemoInspect <demo.gz> <steamId>
var path = args[0];
var target = ulong.Parse(args[1]);

await using var fs = File.OpenRead(path);
await using Stream input = path.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
var ms = new MemoryStream();
await input.CopyToAsync(ms);
ms.Position = 0;

var demo = new CsDemoParser();
string Who(CCSPlayerController? p) => p is null ? "?" : $"{p.PlayerName}({(p.SteamID == target ? "TARGET" : p.SteamID.ToString())})";
bool Is(CCSPlayerController? p) => p?.SteamID == target;
string? targetName = null;

demo.Source1GameEvents.PlayerConnectFull += e => { if (Is(e.Player)) { targetName = e.Player!.PlayerName; Console.WriteLine($"t{demo.CurrentDemoTick.Value} CONNECT {Who(e.Player)}"); } };
demo.Source1GameEvents.PlayerDisconnect += e => { if (Is(e.Player)) Console.WriteLine($"t{demo.CurrentDemoTick.Value} DISCONNECT {Who(e.Player)} reason={e.Reason}"); };
demo.Source1GameEvents.PlayerTeam += e => { if (Is(e.Player)) Console.WriteLine($"t{demo.CurrentDemoTick.Value} TEAM {Who(e.Player)} -> team {e.Team}"); };

demo.BaseUserMessageEvents.UserMessageSayText2 += e =>
    Console.WriteLine($"t{demo.CurrentDemoTick.Value} CHAT {e.Param1}: {e.Param2}");
demo.BaseUserMessageEvents.UserMessageSayText += e =>
    Console.WriteLine($"t{demo.CurrentDemoTick.Value} SAY {e.Text}");

demo.Source1GameEvents.WeaponFire += e => { if (Is(e.Player)) Console.WriteLine($"t{demo.CurrentDemoTick.Value} FIRE {e.Weapon}"); };
demo.Source1GameEvents.PlayerHurt += e =>
{
    if (!Is(e.Attacker) && !Is(e.Player)) return;
    bool tk = e.Attacker is not null && e.Player is not null && e.Attacker.Team == e.Player.Team && e.Attacker != e.Player;
    Console.WriteLine($"t{demo.CurrentDemoTick.Value} HURT {Who(e.Attacker)} -> {Who(e.Player)} dmg={e.DmgHealth} w={e.Weapon}{(tk ? "  [TEAM DAMAGE]" : "")}");
};
demo.Source1GameEvents.PlayerDeath += e =>
{
    if (!Is(e.Attacker) && !Is(e.Player)) return;
    bool tk = e.Attacker is not null && e.Player is not null && e.Attacker.Team == e.Player.Team && e.Attacker != e.Player;
    Console.WriteLine($"t{demo.CurrentDemoTick.Value} DEATH {Who(e.Attacker)} killed {Who(e.Player)} w={e.Weapon}{(tk ? "  [TEAMKILL]" : "")}");
};

var reader = DemoFileReader.Create(demo, ms);
await reader.ReadAllAsync();
Console.WriteLine($"done. target name: {targetName ?? "(never fully connected?)"}");
