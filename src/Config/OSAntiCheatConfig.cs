using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace OSAntiCheat.Config;

/// <summary>
/// Plugin configuration, auto-loaded by CounterStrikeSharp from a JSON file. Lets server
/// owners tune thresholds and toggle detectors without recompiling. Defaults are deliberately
/// conservative (log/notify only) — see TODO.md for the response policy.
/// </summary>
public sealed class OSAntiCheatConfig : BasePluginConfig
{
    // Bump when adding fields so CounterStrikeSharp merges them into an existing config file.
    public override int Version { get; set; } = 14;

    /// <summary>
    /// Include bots as detection subjects. Bots have perfect server-driven aim so they trip the
    /// detectors constantly — keep this false in production. Handy to flip on for a quick test
    /// that the pipeline fires (bots are always tracked as enemy TARGETS regardless).
    /// </summary>
    [JsonPropertyName("IncludeBots")]
    public bool IncludeBots { get; set; } = false;

    [JsonPropertyName("LogPath")]
    public string LogPath { get; set; } = "addons/counterstrikesharp/logs/osanticheat.jsonl";

    /// <summary>Also print alerts to online admins in chat. Off by default so the plugin runs
    /// silently — nobody on the server knows it's collecting data. Turn on for live moderation.</summary>
    [JsonPropertyName("NotifyAdminsInChat")]
    public bool NotifyAdminsInChat { get; set; } = false;

    /// <summary>Admin permission flag required to receive in-chat alerts.</summary>
    [JsonPropertyName("AdminChatFlag")]
    public string AdminChatFlag { get; set; } = "@css/generic";

    /// <summary>Log every raw detector signal to file (not just tier escalations). Gives the
    /// full signal distribution needed to calibrate thresholds against real data.</summary>
    [JsonPropertyName("LogAllSignals")]
    public bool LogAllSignals { get; set; } = true;

    // Fusion / scoring
    [JsonPropertyName("WatchThreshold")]
    public float WatchThreshold { get; set; } = 1.0f;

    [JsonPropertyName("ReviewThreshold")]
    public float ReviewThreshold { get; set; } = 2.5f;

    [JsonPropertyName("DecayTau")]
    public float DecayTau { get; set; } = 90f;

    [JsonPropertyName("CorroborationWindow")]
    public float CorroborationWindow { get; set; } = 10f;

    [JsonPropertyName("CorroborationBonus")]
    public float CorroborationBonus { get; set; } = 0.5f;

    // Detector toggles
    [JsonPropertyName("EnableAimbot")]
    public bool EnableAimbot { get; set; } = true;

    [JsonPropertyName("EnableTriggerbot")]
    public bool EnableTriggerbot { get; set; } = true;

    [JsonPropertyName("EnableSpinbot")]
    public bool EnableSpinbot { get; set; } = true;

    /// <summary>
    /// Whether to actually EXECUTE the action on a confirmed spinbot. Default false = DRY-RUN: a
    /// confirmed spin+HS logic-breach is always LOGGED (including what it *would* have run), but nothing
    /// is executed. Watch the log; once you've confirmed it only ever fires on real spinbots, set true
    /// to arm it. This is the one irreversible action — we never risk banning an innocent unseen. Only
    /// ever triggers on the beyond-human spin+HS signal, never the probabilistic axes. Loud response is
    /// fine here — a spinbot hides nothing, so an announce leaks no game-state.
    /// </summary>
    [JsonPropertyName("AutoActionSpinbot")]
    public bool AutoActionSpinbot { get; set; } = false;

    /// <summary>
    /// Command run on a confirmed spinbot. Placeholders: {slot} {userid} {steamid} {name}. Empty = log
    /// only (no action). Wire it to your ban system, e.g. "css_ban {steamid} 0 spinbot" or "kickid {userid}".
    /// </summary>
    public string SpinbotActionCommand { get; set; } = "";

    /// <summary>Optional public chat announce on a confirmed spinbot. {name} substituted. Empty = silent.</summary>
    public string SpinbotAnnounce { get; set; } = "";

    /// <summary>
    /// Bone-lock aimbot: repeated head-CENTRE locks tighter than a human hand. A LOGIC-BREACH axis
    /// (beyond-human), validated skill-invariant vs tier-1 pros. On by default; the two knobs below
    /// set what counts as a lock and how many repeats before it speaks.
    /// </summary>
    [JsonPropertyName("EnableBoneLock")]
    public bool EnableBoneLock { get; set; } = true;

    /// <summary>Head-centre aim error (degrees) at fire that counts as a machine lock. One quant step ≈ 0.044°.</summary>
    public float BoneLockSpikeDeg { get; set; } = 0.05f;

    /// <summary>Repeated locks required before flagging — one exact hit is chance (~0.2%), never a lock.</summary>
    public int BoneLockMinSpikes { get; set; } = 3;

    [JsonPropertyName("EnableWallhack")]
    public bool EnableWallhack { get; set; } = true;

    /// <summary>
    /// The null test as a live detector — the one signal that separated verified cheaters from the
    /// regulars in offline replay. On by default. Calibrate with the threshold below.
    /// </summary>
    [JsonPropertyName("EnableNullTest")]
    public bool EnableNullTest { get; set; } = true;

    /// <summary>
    /// OFF by default: replaying 37 real player-sessions showed this fires on 100% of them
    /// (median 6 signals each), putting 76% over the Watch threshold. It measures "playing CS2",
    /// not cheating. Do not enable until it can be shown to separate cheaters from the population.
    /// </summary>
    [JsonPropertyName("EnableWallhackGaze")]
    public bool EnableWallhackGaze { get; set; } = false;

    // Detector sensitivity. Lower thresholds = more (and more false) hits — handy for
    // verifying the pipeline on a test server, then raise back for production.

    /// <summary>Sustained yaw rate (deg/s) above which a spin is suspected. Lower to trigger on fast turns.</summary>
    [JsonPropertyName("SpinbotMinRateDegPerSec")]
    public float SpinbotMinRateDegPerSec { get; set; } = 1000f;

    /// <summary>
    /// View speed (deg/s) at the shot above which the aim counts as still travelling rather than
    /// settled. At 64 tick a sample is 15.6ms, so 90 deg/s is 1.4 deg of travel between samples.
    /// </summary>
    [JsonPropertyName("AimbotMinViewRateDegPerSec")]
    public float AimbotMinViewRateDegPerSec { get; set; } = 90f;

    /// <summary>Mid-sweep shots needed before the hit ratio means anything. Lower to fire sooner on less evidence.</summary>
    [JsonPropertyName("AimbotMinSweepShots")]
    public int AimbotMinSweepShots { get; set; } = 20;

    /// <summary>
    /// Fraction of mid-sweep shots landing on a hurtbox before aimbot speaks. The default is the
    /// p99 of CS2CD's random matchmaking players, where it caught 15.4% of 254 verified cheaters
    /// at a 1.1% false-positive rate. Regulars on a long-running server are far better than random
    /// matchmaking players, so this is NOT yet calibrated for such a population — see TODO.md.
    /// </summary>
    [JsonPropertyName("AimbotMinSweepHitRate")]
    public float AimbotMinSweepHitRate { get; set; } = 0.161f;

    /// <summary>Reaction (ms) below which a shot-on-crossing looks like a triggerbot. Raise to flag slower shots.</summary>
    [JsonPropertyName("TriggerbotHumanFloorMs")]
    public float TriggerbotHumanFloorMs { get; set; } = 90f;

    /// <summary>Number of fast shots-on-crossing within the window before triggerbot speaks. Set to 1 for testing.</summary>
    [JsonPropertyName("TriggerbotMinShots")]
    public int TriggerbotMinShots { get; set; } = 4;

    /// <summary>Max aim error (deg) to an enemy to count as "aiming at" it for wallhack tracking.</summary>
    [JsonPropertyName("WallhackAimThresholdDeg")]
    public float WallhackAimThresholdDeg { get; set; } = 5f;

    /// <summary>Seconds the aim must follow an unspotted enemy before it counts as tracking.</summary>
    [JsonPropertyName("WallhackMinTrackSeconds")]
    public float WallhackMinTrackSeconds { get; set; } = 0.4f;

    /// <summary>Units the enemy must move while tracked. The aim must also FOLLOW that movement,
    /// so a held angle an enemy crosses no longer flags (the main live-data false positive).</summary>
    [JsonPropertyName("WallhackMinEnemyMoveUnits")]
    public float WallhackMinEnemyMoveUnits { get; set; } = 0f;

    /// <summary>Degrees of bearing the enemy must sweep across the observer's view. Small arcs
    /// (5-15 deg) are crosshair micro-jitter, not tracking — keep this well above them.</summary>
    [JsonPropertyName("WallhackMinBearingChangeDeg")]
    public float WallhackMinBearingChangeDeg { get; set; } = 20f;

    /// <summary>Share of the enemy's bearing sweep the view must actually follow (0-1).</summary>
    [JsonPropertyName("WallhackFollowFraction")]
    public float WallhackFollowFraction { get; set; } = 0.5f;

    /// <summary>Minimum bearing sweep rate (deg/s), so a slow drift over many seconds doesn't count.</summary>
    [JsonPropertyName("WallhackMinBearingRateDegPerSec")]
    public float WallhackMinBearingRateDegPerSec { get; set; } = 10f;

    // Smart wallhack (gaze-follow) detector.

    /// <summary>Gaze cone (deg): how far off-centre an unspotted enemy can be to count as "glanced at".</summary>
    [JsonPropertyName("WallhackGazeConeDeg")]
    public float WallhackGazeConeDeg { get; set; } = 25f;

    /// <summary>Follow-score (seconds of weighted gaze-following) at which the detector emits. Lower = more sensitive.</summary>
    [JsonPropertyName("WallhackGazeTriggerScore")]
    public float WallhackGazeTriggerScore { get; set; } = 1.5f;

    /// <summary>Seconds after round start counted as the high-value "no legit info yet" window.</summary>
    [JsonPropertyName("WallhackRoundStartSeconds")]
    public float WallhackRoundStartSeconds { get; set; } = 20f;

    /// <summary>Weight multiplier for gaze-follow during the round-start window.</summary>
    [JsonPropertyName("WallhackGazeRoundStartMultiplier")]
    public float WallhackGazeRoundStartMultiplier { get; set; } = 2.0f;

    // Null test (wallhack.nulltest). Compares how often the crosshair sits on an unspotted enemy's
    // PRESENT position vs where that enemy was ~1.5s ago. Game sense correlates with the past too,
    // so the excess (present − past) is what isolates present-knowledge-while-unseen = wallhack.

    /// <summary>How far back the "past" control position is sampled, in seconds.</summary>
    [JsonPropertyName("NullTestLagSeconds")]
    public float NullTestLagSeconds { get; set; } = 1.5f;

    /// <summary>Max aim error (deg) to an unspotted enemy to count the crosshair as "on" it.</summary>
    [JsonPropertyName("NullTestAimDeg")]
    public float NullTestAimDeg { get; set; } = 5f;

    /// <summary>Discordant McNemar observations (present-hit-not-past PLUS past-hit-not-present)
    /// required before the z-score is trusted enough to emit. Guards against low-evidence noise.</summary>
    [JsonPropertyName("NullTestMinObservations")]
    public int NullTestMinObservations { get; set; } = 30;

    /// <summary>
    /// McNemar z-score at/above which the null test emits. z is a standardised statistic, so this
    /// is largely self-calibrating and server-independent: z≈3 ≈ 99.9% confidence the present-over-
    /// past asymmetry is not chance. Raise for fewer/stronger flags. A regular with no real effect
    /// keeps z≈0 however long they play, so this no longer confounds playtime the way raw excess did.
    /// </summary>
    [JsonPropertyName("NullTestMinZ")]
    public float NullTestMinZ { get; set; } = 3.0f;
}
