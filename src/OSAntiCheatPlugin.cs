using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using OSAntiCheat.Config;
using OSAntiCheat.Detection;
using OSAntiCheat.Detection.Detectors;
using OSAntiCheat.Model;
using OSAntiCheat.Tracking;
using OSAntiCheat.Visibility;

namespace OSAntiCheat;

/// <summary>
/// Entry point for the OSAntiCheat CounterStrikeSharp plugin.
///
/// Server-side, heuristic anticheat: we only observe what the server sees (positions,
/// view angles, shots, timing). Detectors measure independent axes; the fusion engine
/// triangulates them into a per-player suspicion score, split into two response tiers:
/// LOGIC-BREACH (beyond-human: spinbot, bone-lock, anti-recoil — auto-action-eligible) and
/// REVIEW (improbable: human-judged, never auto). Auto-action (default: kick + announce) fires
/// ONLY on signals carrying a deterministic edge — spin-hs-kill and fake-pitch, both physically
/// impossible for a real client and measured-zero on 321k archive kills + the live deployment —
/// never the probabilistic axes. Every signal logs tick + map + wall-clock so a reviewer can
/// find the demo and the moment. See TODO.md for the roadmap.
/// </summary>
public sealed class OSAntiCheatPlugin : BasePlugin, IPluginConfig<OSAntiCheatConfig>
{
    public override string ModuleName => "OSAntiCheat";
    // Read from the assembly (which gets it from <Version> in the csproj) so the announced version
    // can never drift from the released one again — a hardcoded "0.7.0" here survived two releases
    // and made a correctly deployed 0.9.2 introduce itself as 0.7.0.
    public override string ModuleVersion =>
        typeof(OSAntiCheatPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public override string ModuleAuthor => "DreamHealer";
    public override string ModuleDescription => "Server-side heuristic anticheat for CS2";

    public OSAntiCheatConfig Config { get; set; } = new();

    private readonly TrackingService _tracking = new();
    private AimbotSweepDetector _aimbot = new();
    private TriggerbotDetector _triggerbot = new();
    private SpinbotDetector _spinbot = new();
    private BoneLockDetector _boneLock = new();
    private RecoilDetector _recoil = new();
    private SilentAimDetector _silent = new();
    private AntiAimDetector _antiAim = new();
    private SnapDetector _snap = new();
    private NameChangeDetector _nameChange = new();
    private WallhackDetector _wallhack = new();
    private WallhackGazeDetector _wallhackGaze = new();
    private NullTestDetector _nullTest = new();

    private SuspicionEngine _engine = new();
    private AlertSink? _alerts;

    // Geometric LOS gate: current map's CS2FOW bake + per-(observer, enemy) blocking-packet
    // cache so near-stationary pairs skip the BVH descent (see docs/visibility-oracle.md).
    private readonly MapBakeService _bake = new();
    private readonly Dictionary<(int Observer, int Enemy), uint> _geoCache = new();

    private readonly Dictionary<int, float> _lastFire = new();
    private readonly Dictionary<string, DetectorKind> _detectorKinds = new(); // detector id -> response class
    private const float BurstWindowSeconds = 0.25f; // shots closer than this count as one burst
    private float _roundStartTime; // server time of the latest round start

    public void OnConfigParsed(OSAntiCheatConfig config)
    {
        Config = config;
        _engine = new SuspicionEngine(new SuspicionConfig
        {
            WatchThreshold = config.WatchThreshold,
            ReviewThreshold = config.ReviewThreshold,
            DecayTau = config.DecayTau,
            CorroborationWindow = config.CorroborationWindow,
            CorroborationBonus = config.CorroborationBonus,
        });

        // Build detectors with the configured sensitivity so thresholds are tunable per server.
        _aimbot = new AimbotSweepDetector(
            config.AimbotMinViewRateDegPerSec, config.AimbotMinSweepShots, config.AimbotMinSweepHitRate);
        _triggerbot = new TriggerbotDetector(config.TriggerbotHumanFloorMs, config.TriggerbotMinShots);
        _spinbot = new SpinbotDetector(config.SpinbotMinRateDegPerSec, config.SpinbotMinSpinHsKills);
        // (SpinbotMinRateDegPerSec is the per-tick spin floor; the continuous-turn gate is internal.)
        _boneLock = new BoneLockDetector(config.BoneLockSpikeDeg, config.BoneLockMinSpikes);
        _recoil = new RecoilDetector(config.RecoilMaxRatio, config.RecoilMinSprays);
        _silent = new SilentAimDetector(config.SilentAimOffDeg, config.SilentAimMinHits);
        _antiAim = new AntiAimDetector(config.AntiAimPitchDeg, config.AntiAimJitterDeg, config.AntiAimJitterFlips);
        _snap = new SnapDetector(config.SnapExactDeg, config.SnapOffFloorDeg, config.SnapMinSnaps);
        _nameChange = new NameChangeDetector(config.NameChangeMinChanges, config.NameChangeWindowSeconds);
        _wallhack = new WallhackDetector(
            config.WallhackMinTrackSeconds, config.WallhackMinEnemyMoveUnits,
            config.WallhackMinBearingChangeDeg, config.WallhackFollowFraction,
            config.WallhackMinBearingRateDegPerSec);
        _wallhackGaze = new WallhackGazeDetector(
            config.WallhackGazeConeDeg, config.WallhackGazeTriggerScore,
            config.WallhackGazeRoundStartMultiplier);
        _nullTest = new NullTestDetector(
            config.NullTestMinObservations, config.NullTestMinZ,
            weight: config.NullTestWeight,
            minPopObservations: config.NullTestMinPopObservations);
    }

    public override void Load(bool hotReload)
    {
        // A relative LogPath used to resolve against the SERVER's cwd (bin/linuxsteamrt64/) and
        // quietly build an addons/ tree there — the v0.7.0 deploy found the log by accident. A
        // relative path now lands in counterstrikesharp/logs/ next to this plugin, wherever the
        // server was started from; absolute paths are honoured as-is.
        string logPath = Config.LogPath;
        if (!Path.IsPathRooted(logPath))
            logPath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "logs", Path.GetFileName(logPath)));
        _alerts = new AlertSink(Logger, logPath);
        _engine.TierRaised += OnTierRaised;

        // Map each detector to its response class so an alert can be labelled by the highest tier
        // that contributed (a LogicBreach signal makes the whole alert "beyond human").
        foreach (IDetector d in new IDetector[]
                 { _aimbot, _triggerbot, _spinbot, _boneLock, _recoil, _silent, _antiAim, _snap, _nameChange, _wallhack, _wallhackGaze, _nullTest })
            _detectorKinds[d.Id] = d.Kind;

        Logger.LogInformation(
            "OSAntiCheat {Version} loaded (hotReload={HotReload})", ModuleVersion, hotReload);

        // Sample every player's state once per server tick — the data source for all detectors.
        RegisterListener<Listeners.OnTick>(_tracking.OnTick);

        // Shot-triggered detectors: aimbot sweep + triggerbot + bone-lock.
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);

        // Kill-triggered: the spin+HS signature (a headshot landed mid-spin). Spinbots get banned
        // fast and rarely leave a readable demo, so the LIVE module is the only place to catch them.
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        // Hurt-triggered: silent aim (a bullet registered while the view pointed away). Hurt-anchored
        // rather than kill-anchored — a cheater lands many more hurts than kills, so the repetition
        // gate closes in a fraction of the time.
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        // Spinbot is time-windowed, not shot-triggered; poll it a few times a second.
        AddTimer(0.2f, PollSpinbot, TimerFlags.REPEAT);

        // Wallhack tracking needs finer resolution to follow a moving enemy through geometry.
        AddTimer(0.05f, PollWallhack, TimerFlags.REPEAT);

        // Nick-changer: renaming every round defeats kick-by-name (live capture 2026-08-04) —
        // repeated renames inside the window are a population-measured-zero edge.
        RegisterEventHandler<EventPlayerChangename>((@event, _) =>
        {
            var player = @event.Userid;
            if (!Config.EnableNameChange || player is null || !player.IsValid) return HookResult.Continue;
            if (player.IsBot && !Config.IncludeBots) return HookResult.Continue;
            Report(_nameChange, _nameChange.OnNameChange(player.Slot, Server.CurrentTime));
            return HookResult.Continue;
        });

        // Track round start so the gaze detector can weight the "no legit info yet" window.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _roundStartTime = Server.CurrentTime;
            return HookResult.Continue;
        });

        // Geometric gate: load the current map's bake in the background at every map change.
        // Missing/invalid bake => geo gating silently inactive (spotted-only behaviour remains).
        RegisterListener<Listeners.OnMapStart>(LoadBakeForMap);
        if (hotReload) LoadBakeForMap(Server.MapName);

        // Diagnostic: confirms on a live server that the sampler reads correct engine data.
        AddCommand("css_osac_debug", "Dump the caller's latest tracked sample", OnDebugCommand);

        // Diagnostic: validates the spotted-system approach for the future wallhack detector.
        // Aim at an enemy (through a wall in an sv_cheats test) and run it to see if the server
        // considers that enemy "spotted" by you.
        AddCommand("css_osac_los", "Report aim error + spotted state to the nearest enemy", OnLosCommand);

        // Drop a player's history when they leave so slots don't leak or get reused stale.
        RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var player = @event.Userid;
            if (player is not null && player.IsValid)
            {
                _tracking.Remove(player.Slot);
                _engine.Remove(player.Slot);
                _aimbot.Remove(player.Slot);
                _triggerbot.Remove(player.Slot);
                _spinbot.Remove(player.Slot);
                _boneLock.Remove(player.Slot);
                _recoil.Remove(player.Slot);
                _silent.Remove(player.Slot);
                _antiAim.Remove(player.Slot);
                _snap.Remove(player.Slot);
                _nameChange.Remove(player.Slot);
                _wallhack.Remove(player.Slot);
                _wallhackGaze.Remove(player.Slot);
                _nullTest.Remove(player.Slot);
                _lastFire.Remove(player.Slot);
                int gone = player.Slot;
                foreach (var pair in _geoCache.Keys.Where(k => k.Observer == gone || k.Enemy == gone).ToList())
                    _geoCache.Remove(pair);
            }
            return HookResult.Continue;
        });
    }

    public override void Unload(bool hotReload)
    {
        _engine.TierRaised -= OnTierRaised;
        _tracking.Reset();
        _engine.Reset();
        Logger.LogInformation("OSAntiCheat {Version} unloaded", ModuleVersion);
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        // Skip bots: they aren't cheaters and their instant server-driven aim trips these constantly.
        // (IncludeBots can re-enable them as subjects for quick pipeline testing.)
        if (shooter is null || !shooter.IsValid) return HookResult.Continue;
        if (shooter.IsBot && !Config.IncludeBots) return HookResult.Continue;

        var shooterTracker = _tracking.For(shooter.Slot);
        if (shooterTracker is null) return HookResult.Continue;

        var enemies = EnemyTrackersOf(shooter);
        if (enemies.Count == 0) return HookResult.Continue;

        float now = Server.CurrentTime;

        // Spray gating: only the first shot of a burst has a meaningful "reaction". Later shots
        // in continuous fire coincide with crossings by chance and look like triggerbots.
        bool burstContinuation =
            _lastFire.TryGetValue(shooter.Slot, out float last) && now - last < BurstWindowSeconds;
        _lastFire[shooter.Slot] = now;

        // Both want the burst's first shot only. The sweep detector needs it because a spray's
        // later bullets aren't fresh decisions - the trigger was already held when the crosshair
        // crossed someone - and counting each one would read a single pull as many.
        if (Config.EnableAimbot && !burstContinuation)
            Report(_aimbot, _aimbot.OnFire(shooterTracker, enemies, now));

        if (Config.EnableTriggerbot && !burstContinuation)
            Report(_triggerbot, _triggerbot.OnFire(shooterTracker, enemies, now));

        // Bone-lock: head-centre precision at the shot. First-of-burst + on-target, same as the
        // others; the detector applies its own degenerate-range guard and repetition gate.
        if (Config.EnableBoneLock && !burstContinuation)
            Report(_boneLock, _boneLock.OnFire(shooterTracker, enemies, now));

        // Anti-recoil: fold EVERY shot into the spray (not first-of-burst — the whole curve matters).
        if (Config.EnableRecoil)
            Report(_recoil, _recoil.OnFire(shooterTracker, @event.Weapon ?? "", now));

        // Snap runs on EVERY shot, mid-burst included — the spray-aimbot pulls only one shot in
        // the spray onto the head, which lives exactly where the burst-gated detectors never look.
        if (Config.EnableSnap)
            Report(_snap, _snap.OnFire(shooterTracker, enemies, now));

        // Silent-aim burst bookkeeping: every fire, so its hurt handler can tell burst openers
        // (recoil-free, admissible) from spray continuations (recoil-confounded, skipped).
        _silent.NoteFire(shooter.Slot, now);

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!Config.EnableSilentAim) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker is null || !attacker.IsValid || victim is null || !victim.IsValid) return HookResult.Continue;
        if (attacker.Slot == victim.Slot || attacker.Team == victim.Team) return HookResult.Continue;
        if (attacker.IsBot && !Config.IncludeBots) return HookResult.Continue;

        // Bullet damage only: grenades/fire/knife/taser hit legitimately without the view on the
        // victim, and world/fall damage has no aim at all. (Shotguns are excluded in the detector.)
        string weapon = (@event.Weapon ?? "").ToLowerInvariant();
        if (weapon.Length == 0 || weapon.Contains("grenade") || weapon.Contains("molotov") ||
            weapon.Contains("inferno") || weapon.Contains("knife") || weapon.Contains("bayonet") ||
            weapon.Contains("taser") || weapon.Contains("world") || weapon.Contains("flash") ||
            weapon.Contains("decoy") || weapon.Contains("c4")) return HookResult.Continue;

        var attackerTracker = _tracking.For(attacker.Slot);
        var victimTracker = _tracking.For(victim.Slot);
        if (attackerTracker is null || victimTracker is null) return HookResult.Continue;

        Report(_silent, _silent.OnHurt(attackerTracker, victimTracker, weapon, @event.DmgHealth, Server.CurrentTime));
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (attacker is null || !attacker.IsValid) return HookResult.Continue;
        if (attacker.IsBot && !Config.IncludeBots) return HookResult.Continue;
        if (!Config.EnableSpinbot) return HookResult.Continue;

        var tracker = _tracking.For(attacker.Slot);
        if (tracker is not null)
            Report(_spinbot, _spinbot.OnKill(tracker, @event.Headshot, Server.CurrentTime));
        return HookResult.Continue;
    }

    /// <summary>
    /// Response to a signal carrying a deterministic logic-breach edge (spin-hs-kill, fake-pitch).
    /// Runs <see cref="OSAntiCheatConfig.AutoActionCommand"/> when armed (<see cref="OSAntiCheatConfig.AutoActionEnabled"/>);
    /// otherwise logs what it WOULD have run (dry-run). Either way the action decision is durably
    /// logged, so the audit trail exists whether or not anything executed.
    /// </summary>
    private void Enforce(Signal signal, string edge)
    {
        if (Array.IndexOf(Config.AutoActionEdges, edge) < 0) return;

        var suspect = Utilities.GetPlayerFromSlot(signal.PlayerSlot);
        // Never act on bots (test subjects under IncludeBots) or a player already gone.
        if (suspect is null || !suspect.IsValid || suspect.IsBot) return;

        string Fill(string template) => template
            .Replace("{slot}", suspect.Slot.ToString())
            .Replace("{userid}", suspect.UserId?.ToString() ?? "")
            .Replace("{steamid}", suspect.SteamID.ToString())
            .Replace("{name}", suspect.PlayerName ?? "?")
            .Replace("{detector}", signal.Detector)
            .Replace("{edge}", edge);

        string cmd = string.IsNullOrWhiteSpace(Config.AutoActionCommand) ? "" : Fill(Config.AutoActionCommand);

        bool executed = Config.AutoActionEnabled && cmd.Length > 0;
        _alerts?.LogAction(signal, edge, executed ? cmd : $"DRY-RUN: {cmd}",
            suspect.PlayerName, suspect.SteamID.ToString(), Server.MapName);

        // Admins always get the full, unambiguous picture in chat — who, which cheat, the exact
        // evidence, and the SteamID they need to escalate to a ban. This bypasses NotifyAdminsInChat
        // (that flag gates fusion *suspicions*; an action the plugin took on their server is never
        // something admins should have to discover in a log file).
        NotifyAdminsOfAction(suspect, signal, executed);

        if (executed)
        {
            Logger.LogWarning("[OSAC] AUTO-ACTION [{Edge}] — {Name} ({SteamId}) running '{Cmd}' :: {Reason}",
                edge, suspect.PlayerName, suspect.SteamID, cmd, signal.Reason);
            if (!string.IsNullOrEmpty(Config.AutoActionAnnounce))
                Server.PrintToChatAll(Fill(Config.AutoActionAnnounce));
            Server.ExecuteCommand(cmd);
        }
        else
        {
            Logger.LogWarning("[OSAC] AUTO-ACTION [{Edge}] confirmed (DRY-RUN, no action taken) — {Name} " +
                "({SteamId}) would run '{Cmd}' :: {Reason}",
                edge, suspect.PlayerName, suspect.SteamID, cmd.Length > 0 ? cmd : "(no command configured)",
                signal.Reason);
        }
    }

    /// <summary>
    /// Two-line admin chat notice for an auto-action: no jargon in line one (CHEAT KICKED + name +
    /// cheat type + SteamID, everything needed to permaban), raw detector evidence in line two.
    /// </summary>
    private void NotifyAdminsOfAction(CCSPlayerController suspect, Signal signal, bool executed)
    {
        string what = signal.Detector switch
        {
            "spinbot" => "SPINBOT",
            "antiaim" => "ANTI-AIM",
            "namechanger" => "NICK-CHANGER",
            _ => signal.Detector.ToUpperInvariant(),
        };
        string verdict = executed ? "CHEAT KICKED" : "CHEAT CONFIRMED (dry-run, NOT kicked)";
        string line1 =
            $" {ChatColors.Red}[OSAC] {verdict}{ChatColors.Default}: " +
            $"{ChatColors.Green}{suspect.PlayerName ?? "?"}{ChatColors.Default} — {what} " +
            $"(steamid {suspect.SteamID})";
        string line2 =
            $" {ChatColors.Red}[OSAC]{ChatColors.Default} evidence: {signal.Reason}";

        foreach (var admin in Utilities.GetPlayers())
        {
            if (!admin.IsValid || admin.IsBot) continue;
            if (!AdminManager.PlayerHasPermissions(admin, Config.AdminChatFlag)) continue;
            admin.PrintToChat(line1);
            admin.PrintToChat(line2);
        }
    }

    private void PollSpinbot()
    {
        if (!Config.EnableSpinbot && !Config.EnableAntiAim) return;
        float now = Server.CurrentTime;
        foreach (var tracker in _tracking.Trackers.Values)
        {
            var player = Utilities.GetPlayerFromSlot(tracker.Slot);
            if (player is null) continue;
            if (player.IsBot && !Config.IncludeBots) continue; // skip bots unless testing
            if (Config.EnableSpinbot) Report(_spinbot, _spinbot.Inspect(tracker));
            // Anti-aim rides the same cadence: OnPoll consumes each buffered tick exactly once
            // (sequence-tracked), so a 0.2 s poll over the ~2 s ring buffer misses nothing.
            if (Config.EnableAntiAim) Report(_antiAim, _antiAim.OnPoll(tracker, now));
        }
    }

    private void LoadBakeForMap(string mapName)
    {
        _geoCache.Clear();
        // Null-test evidence is per-map by design: the population baseline that gates emissions
        // measures THIS map's spotted-system behaviour, and per-player counts contaminated by a
        // map artifact (night maps) must not follow players to the next map.
        _nullTest.Reset();
        if (string.IsNullOrWhiteSpace(Config.BakesDir))
        {
            _bake.Clear();
            return;
        }
        string dir = Path.IsPathRooted(Config.BakesDir)
            ? Config.BakesDir
            : Path.GetFullPath(Path.Combine(ModuleDirectory, Config.BakesDir));
        _bake.LoadFor(mapName, dir, line => Logger.LogInformation("[geo] {Line}", line));
    }

    private void PollWallhack()
    {
        if (!Config.EnableWallhack && !Config.EnableWallhackGaze && !Config.EnableNullTest) return;

        float now = Server.CurrentTime;
        float aimThreshold = Config.WallhackAimThresholdDeg;
        float gazeCone = Config.WallhackGazeConeDeg;
        float nullLag = Config.NullTestLagSeconds;
        float nullAimDeg = Config.NullTestAimDeg;
        bool roundStart = now - _roundStartTime < Config.WallhackRoundStartSeconds;

        foreach (var observer in Utilities.GetPlayers())
        {
            if (!observer.IsValid || !observer.PawnIsAlive) continue;
            if (observer.IsBot && !Config.IncludeBots) continue;

            var team = observer.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) continue;

            var pawn = observer.PlayerPawn.Value;
            if (pawn is null || pawn.AbsOrigin is null) continue;

            var eye = new Vector3(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + 64f);
            var angles = new ViewAngles(pawn.EyeAngles.X, pawn.EyeAngles.Y, pawn.EyeAngles.Z);
            int slot = observer.Slot;

            // Nearest unspotted enemy by crosshair (hard lock) and by gaze cone (soft follow).
            WallhackDetector.WallTarget? bestAim = null;
            float bestAimErr = aimThreshold;
            bool bestTeamUnspotted = false;
            float bestEnemySpeed = 0f;
            WallhackGazeDetector.GazeSample? bestGaze = null;
            float bestGazeErr = gazeCone;
            // Null test: McNemar discordant tally over ALL unspotted enemies this poll. Only the
            // samples where present and past DISAGREE carry information — present-hit-not-past (b)
            // vs past-hit-not-present (c). Concordant samples (both/neither) are skipped.
            int ntNowOnly = 0, ntPastOnly = 0;

            foreach (var enemy in Utilities.GetPlayers())
            {
                if (!enemy.IsValid || enemy.Slot == slot || !enemy.PawnIsAlive) continue;
                if (enemy.Team == team) continue;
                if (enemy.Team != CsTeam.Terrorist && enemy.Team != CsTeam.CounterTerrorist) continue;

                var ep = enemy.PlayerPawn.Value;
                if (ep is null || ep.AbsOrigin is null) continue;

                var mask = ep.EntitySpottedState.SpottedByMask;
                bool spottedByObserver = (mask[slot / 32] & (1u << (slot % 32))) != 0;
                if (spottedByObserver) continue; // legitimately seen — not a wallhack candidate

                var feet = new Vector3(ep.AbsOrigin.X, ep.AbsOrigin.Y, ep.AbsOrigin.Z);
                float err = Geometry.NearestBodyAimError(eye, angles, feet);
                float bearingYaw = MathF.Atan2(feet.Y - eye.Y, feet.X - eye.X) * (180f / MathF.PI);

                if (err < bestAimErr)
                {
                    bestAimErr = err;
                    bestAim = new WallhackDetector.WallTarget(enemy.Slot, feet, err, angles.Yaw, bearingYaw);
                    // Unspotted by EVERYONE => no teammate radar dot to legitimately follow.
                    bestTeamUnspotted = true;
                    for (int i = 0; i < mask.Length; i++)
                        if (mask[i] != 0) { bestTeamUnspotted = false; break; }
                    var v = ep.AbsVelocity;
                    bestEnemySpeed = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                }
                if (err < bestGazeErr)
                {
                    bestGazeErr = err;
                    bestGaze = new WallhackGazeDetector.GazeSample(
                        enemy.Slot, err, angles.Yaw, bearingYaw, roundStart);
                }

                // Null test: only when we can place where this enemy was ~1.5s ago.
                if (Config.EnableNullTest &&
                    _tracking.For(enemy.Slot) is { } enemyTracker &&
                    TryPastOrigin(enemyTracker, now, nullLag, out var pastFeet))
                {
                    bool onNow = err <= nullAimDeg;
                    bool onPast = Geometry.NearestBodyAimError(eye, angles, pastFeet) <= nullAimDeg;
                    if (onNow && !onPast) ntNowOnly++;
                    else if (onPast && !onNow) ntPastOnly++;
                }
            }

            if (Config.EnableWallhack)
            {
                var target = bestAim;
                bool quiet = false;
                // Geometric gate (measured stack, TODO.md "GEO-GATE-EXPERIMENTET"): the candidate
                // must be provably occluded by static geometry AND unseen by the observer's whole
                // team. Applies only when a bake is loaded — otherwise behaviour is unchanged.
                if (target is { } t && Config.WallhackGeoGate && _bake.Current is { } bake)
                {
                    var pairKey = (slot, t.EnemyId);
                    uint cached = _geoCache.GetValueOrDefault(pairKey, Bvh8Map.InvalidRef);
                    bool occluded = BodyOcclusion.AllSamplesBlocked(bake, eye, t.EnemyPos, ref cached);
                    _geoCache[pairKey] = cached;
                    if (!occluded || !bestTeamUnspotted)
                        target = null;
                    else
                        // No footsteps either => no legitimate information channel at all
                        // (~1% of legit sessions in the A/B sweep) — boost, never gate.
                        quiet = bestEnemySpeed <= Config.WallhackGeoQuietSpeedUnits;
                }
                var signal = _wallhack.Observe(slot, now, target);
                if (signal is { } s && Config.WallhackGeoGate && _bake.Current is not null)
                    signal = s with
                    {
                        Confidence = quiet ? MathF.Min(1f, s.Confidence * Config.WallhackGeoQuietBoost) : s.Confidence,
                        Reason = s.Reason + (quiet ? " [geo: occluded+team-unseen+silent]" : " [geo: occluded+team-unseen]"),
                    };
                Report(_wallhack, signal);
            }
            if (Config.EnableWallhackGaze)
                Report(_wallhackGaze, _wallhackGaze.Observe(slot, now, bestGaze));
            if (Config.EnableNullTest)
                Report(_nullTest, _nullTest.Accumulate(slot, now, ntNowOnly, ntPastOnly));
        }
    }

    private void Report(IDetector detector, Signal? signal)
    {
        if (signal is not { } s) return;

        // Stamp the server tick centrally so every logged signal carries a demo_gototick target — the
        // reviewer parses the recorded demo and jumps straight to the moment. Approx = time × tickrate
        // (seconds since map start ≈ demo tick when the server records from map start); refine to the
        // exact engine tick if it drifts against a real demo.
        s = s with { Tick = (int)MathF.Round(Server.CurrentTime * 64f) };

        // Shadow detectors run + LOG (we collect what they'd fire on) but never fuse — so a falsified
        // axis gathers data without polluting the score or raising an alert.
        bool shadow = Config.ShadowDetectors is { } sh && Array.IndexOf(sh, detector.Id) >= 0;

        // Log every raw signal (always for shadow detectors; otherwise when LogAllSignals is on).
        if (shadow || Config.LogAllSignals)
        {
            var p = Utilities.GetPlayerFromSlot(s.PlayerSlot);
            _alerts?.LogSignal(s, p?.PlayerName, p?.SteamID.ToString(), Server.MapName);
        }

        if (shadow) return;              // observe-only: never fuse, alert, or act
        _engine.Report(s, detector.Weight);

        // Deterministic edges (spin-hs-kill, fake-pitch) are the only signals that can trigger the
        // auto-action; everything else fuses toward human review and nothing more.
        if (s.Edge is { } edge) Enforce(s, edge);
    }

    /// <summary>
    /// The player's feet position ~<paramref name="lag"/> seconds ago, for the null test's control.
    /// Picks the buffered sample closest to that time; rejects it if none is within 0.5s or the
    /// player was dead then. The tracker holds ~2s, so a 1.5s lag sits inside the window.
    /// </summary>
    private static bool TryPastOrigin(PlayerTracker tracker, float now, float lag, out Vector3 origin)
    {
        origin = default;
        float target = now - lag;
        bool found = false;
        float bestDelta = float.MaxValue;
        TickSample best = default;
        for (int i = 0; i < tracker.Count; i++)
        {
            var s = tracker[i];
            float d = MathF.Abs(s.Time - target);
            if (d < bestDelta) { bestDelta = d; best = s; found = true; }
        }
        if (!found || !best.Alive || bestDelta > 0.5f) return false;
        origin = best.Origin;
        return true;
    }

    /// <summary>Live trackers of players on the opposing team to <paramref name="shooter"/>.</summary>
    private List<PlayerTracker> EnemyTrackersOf(CCSPlayerController shooter)
    {
        var result = new List<PlayerTracker>();
        var team = shooter.Team;
        if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) return result;

        foreach (var other in Utilities.GetPlayers())
        {
            if (!other.IsValid || other.Slot == shooter.Slot) continue;
            if (other.Team == team) continue;
            if (other.Team != CsTeam.Terrorist && other.Team != CsTeam.CounterTerrorist) continue;

            var tracker = _tracking.For(other.Slot);
            if (tracker is not null) result.Add(tracker);
        }
        return result;
    }

    private void OnLosCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            command.ReplyToCommand("[OSAC] Run this in-game while aiming at an enemy.");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || pawn.AbsOrigin is null)
        {
            command.ReplyToCommand("[OSAC] No pawn.");
            return;
        }

        var eye = new System.Numerics.Vector3(
            pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + 64f);
        var angles = new Model.ViewAngles(pawn.EyeAngles.X, pawn.EyeAngles.Y, pawn.EyeAngles.Z);

        CCSPlayerController? best = null;
        float bestErr = float.MaxValue;
        var team = player.Team;
        foreach (var enemy in Utilities.GetPlayers())
        {
            if (!enemy.IsValid || enemy.Slot == player.Slot) continue;
            if (enemy.Team == team) continue;
            if (enemy.Team != CsTeam.Terrorist && enemy.Team != CsTeam.CounterTerrorist) continue;

            var ep = enemy.PlayerPawn.Value;
            if (ep is null || ep.AbsOrigin is null) continue;

            var feet = new System.Numerics.Vector3(ep.AbsOrigin.X, ep.AbsOrigin.Y, ep.AbsOrigin.Z);
            float err = Detection.Geometry.NearestBodyAimError(eye, angles, feet);
            if (err < bestErr) { bestErr = err; best = enemy; }
        }

        if (best?.PlayerPawn.Value is not { } bestPawn)
        {
            command.ReplyToCommand("[OSAC] No enemy found.");
            return;
        }

        var spottedState = bestPawn.EntitySpottedState;
        bool spottedByAnyone = spottedState.Spotted;
        int slot = player.Slot;
        bool spottedByYou =
            (spottedState.SpottedByMask[slot / 32] & (1u << (slot % 32))) != 0;

        string report =
            $"nearest enemy {best!.PlayerName}: aimErr={bestErr:F1}° " +
            $"spottedByYou={spottedByYou} spottedByAnyone={spottedByAnyone}";
        command.ReplyToCommand($"[OSAC] {report}");
        // Also log so it lands in the file (handy for pasting the result back).
        Logger.LogInformation("[OSAC] los ({Caller}): {Report}", player.PlayerName, report);
    }

    private void OnDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            command.ReplyToCommand("[OSAC] Run this in-game — it reports your own tracked sample.");
            return;
        }

        var tracker = _tracking.For(player.Slot);
        if (tracker is null || !tracker.TryLatest(out var s))
        {
            command.ReplyToCommand("[OSAC] No samples yet for you.");
            return;
        }

        float score = _engine.ScoreOf(player.Slot, Server.CurrentTime);
        string report =
            $"buffer={tracker.Count}/{tracker.Capacity} seq={s.Sequence} " +
            $"alive={s.Alive} onGround={s.OnGround} score={score:F2} " +
            $"pos=({s.Origin.X:F0},{s.Origin.Y:F0},{s.Origin.Z:F0}) " +
            $"ang=(p{s.Angles.Pitch:F1},y{s.Angles.Yaw:F1}) vel={s.Velocity.Length():F0}u/s";
        command.ReplyToCommand($"[OSAC] {report}");
        // Also log so it lands in the file (handy for pasting the result back).
        Logger.LogInformation("[OSAC] debug ({Caller}): {Report}", player.PlayerName, report);
    }

    private void OnTierRaised(SuspicionAlert alert)
    {
        var player = Utilities.GetPlayerFromSlot(alert.PlayerSlot);
        if (player is not null && player.IsBot && !Config.IncludeBots) return; // no bot alerts in production

        // Response class = the highest-tier detector that contributed. One LogicBreach signal
        // (beyond-human) makes the whole alert auto-eligible; otherwise it's a review flag.
        var responseClass = alert.RecentSignals.Any(
            s => _detectorKinds.GetValueOrDefault(s.Detector) == DetectorKind.LogicBreach)
            ? DetectorKind.LogicBreach : DetectorKind.Behavioural;

        // Always log (JSON-lines + console) — the durable record.
        _alerts?.Handle(alert, player?.PlayerName, player?.SteamID.ToString(), responseClass);

        // Additionally ping online admins in chat so they don't have to read logs.
        if (Config.NotifyAdminsInChat)
            NotifyAdmins(alert, player, responseClass);
    }

    private void NotifyAdmins(SuspicionAlert alert, CCSPlayerController? subject,
        DetectorKind responseClass = DetectorKind.Behavioural)
    {
        string name = subject?.PlayerName ?? $"slot {alert.PlayerSlot}";
        string detectors = string.Join(", ", alert.RecentSignals
            .Select(s => s.Detector)
            .Distinct());

        // Beyond-human breaches scream (red); improbable review flags whisper "could be luck" (yellow).
        bool breach = responseClass == DetectorKind.LogicBreach;
        string label = breach ? "LOGIC BREACH" : "review — could be luck";
        char labelColor = breach ? ChatColors.Red : ChatColors.Yellow;

        string message =
            $" {ChatColors.Red}[OSAC]{ChatColors.Default} {labelColor}{label}{ChatColors.Default}: " +
            $"{ChatColors.Green}{name}{ChatColors.Default} " +
            $"(score {alert.Score:F1}) — {detectors}";

        foreach (var admin in Utilities.GetPlayers())
        {
            if (!admin.IsValid || admin.IsBot) continue;
            if (!AdminManager.PlayerHasPermissions(admin, Config.AdminChatFlag)) continue;
            admin.PrintToChat(message);
        }
    }
}
