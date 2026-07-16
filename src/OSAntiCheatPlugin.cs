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

namespace OSAntiCheat;

/// <summary>
/// Entry point for the OSAntiCheat CounterStrikeSharp plugin.
///
/// Server-side, heuristic anticheat: we only observe what the server sees (positions,
/// view angles, shots, timing). Detectors measure independent axes; the fusion engine
/// triangulates them into a per-player suspicion score. v1 response is log + admin notice
/// only — never auto kick/ban. See TODO.md for the roadmap.
/// </summary>
public sealed class OSAntiCheatPlugin : BasePlugin, IPluginConfig<OSAntiCheatConfig>
{
    public override string ModuleName => "OSAntiCheat";
    public override string ModuleVersion => "0.6.1";
    public override string ModuleAuthor => "DreamHealer";
    public override string ModuleDescription => "Server-side heuristic anticheat for CS2";

    public OSAntiCheatConfig Config { get; set; } = new();

    private readonly TrackingService _tracking = new();
    private AimbotSweepDetector _aimbot = new();
    private TriggerbotDetector _triggerbot = new();
    private SpinbotDetector _spinbot = new();
    private WallhackDetector _wallhack = new();
    private WallhackGazeDetector _wallhackGaze = new();
    private NullTestDetector _nullTest = new();

    private SuspicionEngine _engine = new();
    private AlertSink? _alerts;

    private readonly Dictionary<int, float> _lastFire = new();
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
        _spinbot = new SpinbotDetector(config.SpinbotMinRateDegPerSec);
        _wallhack = new WallhackDetector(
            config.WallhackMinTrackSeconds, config.WallhackMinEnemyMoveUnits,
            config.WallhackMinBearingChangeDeg, config.WallhackFollowFraction,
            config.WallhackMinBearingRateDegPerSec);
        _wallhackGaze = new WallhackGazeDetector(
            config.WallhackGazeConeDeg, config.WallhackGazeTriggerScore,
            config.WallhackGazeRoundStartMultiplier);
        _nullTest = new NullTestDetector(config.NullTestMinObservations, config.NullTestMinZ);
    }

    public override void Load(bool hotReload)
    {
        _alerts = new AlertSink(Logger, Config.LogPath);
        _engine.TierRaised += OnTierRaised;

        Logger.LogInformation(
            "OSAntiCheat {Version} loaded (hotReload={HotReload})", ModuleVersion, hotReload);

        // Sample every player's state once per server tick — the data source for all detectors.
        RegisterListener<Listeners.OnTick>(_tracking.OnTick);

        // Shot-triggered detectors: aimbot sweep + triggerbot.
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);

        // Spinbot is time-windowed, not shot-triggered; poll it a few times a second.
        AddTimer(0.2f, PollSpinbot, TimerFlags.REPEAT);

        // Wallhack tracking needs finer resolution to follow a moving enemy through geometry.
        AddTimer(0.05f, PollWallhack, TimerFlags.REPEAT);

        // Track round start so the gaze detector can weight the "no legit info yet" window.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _roundStartTime = Server.CurrentTime;
            return HookResult.Continue;
        });

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
                _wallhack.Remove(player.Slot);
                _wallhackGaze.Remove(player.Slot);
                _nullTest.Remove(player.Slot);
                _lastFire.Remove(player.Slot);
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

        return HookResult.Continue;
    }

    private void PollSpinbot()
    {
        if (!Config.EnableSpinbot) return;
        foreach (var tracker in _tracking.Trackers.Values)
        {
            var player = Utilities.GetPlayerFromSlot(tracker.Slot);
            if (player is null) continue;
            if (player.IsBot && !Config.IncludeBots) continue; // skip bots unless testing
            Report(_spinbot, _spinbot.Inspect(tracker));
        }
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
                Report(_wallhack, _wallhack.Observe(slot, now, bestAim));
            if (Config.EnableWallhackGaze)
                Report(_wallhackGaze, _wallhackGaze.Observe(slot, now, bestGaze));
            if (Config.EnableNullTest)
                Report(_nullTest, _nullTest.Accumulate(slot, now, ntNowOnly, ntPastOnly));
        }
    }

    private void Report(IDetector detector, Signal? signal)
    {
        if (signal is not { } s) return;

        // Log every raw signal (for calibration) before fusing it into the score.
        if (Config.LogAllSignals)
        {
            var p = Utilities.GetPlayerFromSlot(s.PlayerSlot);
            _alerts?.LogSignal(s, p?.PlayerName, p?.SteamID.ToString());
        }

        _engine.Report(s, detector.Weight);
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

        // Always log (JSON-lines + console) — the durable record.
        _alerts?.Handle(alert, player?.PlayerName, player?.SteamID.ToString());

        // Additionally ping online admins in chat so they don't have to read logs.
        if (Config.NotifyAdminsInChat)
            NotifyAdmins(alert, player);
    }

    private void NotifyAdmins(SuspicionAlert alert, CCSPlayerController? subject)
    {
        string name = subject?.PlayerName ?? $"slot {alert.PlayerSlot}";
        string detectors = string.Join(", ", alert.RecentSignals
            .Select(s => s.Detector)
            .Distinct());

        string message =
            $" {ChatColors.Red}[OSAC]{ChatColors.Default} {ChatColors.Yellow}{alert.Tier}{ChatColors.Default}: " +
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
