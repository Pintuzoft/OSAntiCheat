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
    // Bump when adding fields. NOTE: CounterStrikeSharp does NOT rewrite an existing config file on
    // a version bump — missing keys simply deserialize to the defaults below (so new detectors are
    // active even under a stale file). To get the file itself current: unload the plugin, move the
    // json away, load again — it regenerates at this version with every key present.
    public override int Version { get; set; } = 18;

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
    /// Detectors that run in SHADOW mode: they still compute and LOG every raw signal (so we collect
    /// what they would fire on, for later analysis / a future model), but never fuse into the suspicion
    /// score or trigger an alert. Better than disabling — a muted detector gathers no data; a shadowed
    /// one gathers everything without polluting the score. Default = the falsified/unvalidated axes.
    /// </summary>
    public string[] ShadowDetectors { get; set; } =
    {
        "wallhack.track", "wallhack.gaze", "aimbot.sweep", "triggerbot", "wallhack.nulltest",
    };

    /// <summary>
    /// Whether to EXECUTE <see cref="AutoActionCommand"/> when a signal carrying an auto-action edge
    /// fires. False = DRY-RUN: the confirmed breach is still logged durably (including what it *would*
    /// have run). Default true as of v0.9.8: the two default edges are physically impossible for a
    /// real client AND measured-zero across 321k archive kills + the live deployment window, and the
    /// action is a kick — cheap to undo if a bug ever surfaces, unlike a ban. Only signals carrying an
    /// edge in <see cref="AutoActionEdges"/> can ever trigger this; the probabilistic axes cannot.
    /// Loud response is fine here — a spinbot hides nothing, so an announce leaks no game-state.
    /// </summary>
    [JsonPropertyName("AutoActionEnabled")]
    public bool AutoActionEnabled { get; set; } = true;

    /// <summary>
    /// Which deterministic edges may trigger the auto-action. Available: "spin-hs-kill" (headshot
    /// landed while >360° of continuous rotation is still whirling at the kill tick, repeated),
    /// "fake-pitch" (view pitch past the engine's server-side ±89° clamp for consecutive ticks).
    /// The poll-based continuous-spin and yaw-jitter signals carry no edge by design — strong, but
    /// they stay log+fusion-only until they earn the same validation.
    /// </summary>
    [JsonPropertyName("AutoActionEdges")]
    public string[] AutoActionEdges { get; set; } = { "spin-hs-kill", "fake-pitch" };

    /// <summary>
    /// Command run on a confirmed edge. Placeholders: {slot} {userid} {steamid} {name}. Empty = log
    /// only. Default kicks; escalate to your ban system (e.g. "css_ban {steamid} 0 cheating") only
    /// after watching the kick log for a while — a wrong kick costs a reconnect, a wrong ban a player.
    /// </summary>
    [JsonPropertyName("AutoActionCommand")]
    public string AutoActionCommand { get; set; } = "kickid {userid} [OSAC] impossible input signature";

    /// <summary>Optional public chat announce when the action runs. {name} substituted. Empty = silent.</summary>
    [JsonPropertyName("AutoActionAnnounce")]
    public string AutoActionAnnounce { get; set; } =
        "[OSAC] {name} kicked — physically impossible input (spin/anti-aim signature)";

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

    /// <summary>
    /// Anti-recoil (script/macro/firmware): recoil compensation too consistent to be human. A
    /// LOGIC-BREACH axis — the human floor is ratio ~0.06 (17k archive sprays), so the threshold below
    /// sits in the gap under any human. Narrow (misses humanised scripts) but never false-positives.
    /// </summary>
    [JsonPropertyName("EnableRecoil")]
    public bool EnableRecoil { get; set; } = true;

    /// <summary>Recoil consistency ratio below which it's a machine. Human floor ~0.06; default 0.04.</summary>
    public float RecoilMaxRatio { get; set; } = 0.04f;

    /// <summary>Sprays of one weapon required before judging — a stable estimate, not one lucky spray.</summary>
    public int RecoilMinSprays { get; set; } = 4;

    /// <summary>
    /// Silent aim: a bullet REGISTERED damage while the replicated view pointed ≥ SilentAimOffDeg
    /// away from every position the victim held in the lag-comp window (~250 ms). A LOGIC-BREACH
    /// axis — the shot direction and the view direction disagree, which honest play cannot produce
    /// on a burst opener (recoil comp only skews mid-spray, and those are skipped). Archive-typed
    /// on two banned cheaters (spin-silent and frozen-view psilent); honest tail at 15° = 0.19 %
    /// of headshot kills, so three repeats sit beyond ~1e-8.
    /// </summary>
    [JsonPropertyName("EnableSilentAim")]
    public bool EnableSilentAim { get; set; } = true;

    /// <summary>View-to-victim floor (degrees) for a registered hit to count. Measured honest
    /// burst-opener maximum: 8.0° (21-demo hurts sweep, n=3486, zero events ≥10°).</summary>
    public float SilentAimOffDeg { get; set; } = 10f;

    /// <summary>Off-view hits required before flagging — one can be jump-spread luck or interp noise.</summary>
    public int SilentAimMinHits { get; set; } = 3;

    /// <summary>
    /// Anti-aim (defensive angle desync): pitch past the engine's ±89° clamp ("standing on their
    /// head"), or sustained sign-alternating yaw jitter. A LOGIC-BREACH axis on the player's OWN
    /// angles. Measured honest population: pitch parks at exactly 89.00 and never beyond (CS2
    /// clamps server-side — the gate is a free tripwire for anything that bypasses the clamp);
    /// honest jitter maximum is ONE alternation, the gate needs six. Value here is corroboration:
    /// anti-aim ships in the same rage packages as spin/silent aim.
    /// </summary>
    [JsonPropertyName("EnableAntiAim")]
    public bool EnableAntiAim { get; set; } = true;

    /// <summary>Pitch beyond this = past the engine clamp (89.0 exactly is honest; margin for float noise).</summary>
    public float AntiAimPitchDeg { get; set; } = 89.5f;

    /// <summary>Tick-to-tick yaw delta (degrees) that counts as a jitter jerk.</summary>
    public float AntiAimJitterDeg { get; set; } = 45f;

    /// <summary>Consecutive sign-alternations required. Honest max measured: 1.</summary>
    public int AntiAimJitterFlips { get; set; } = 6;

    /// <summary>
    /// Aimbot snap (pull-to-head): ≥ SnapOffFloorDeg off a head one tick before the shot →
    /// ≤ SnapExactDeg on its CENTRE at the shot. A LOGIC-BREACH conjunction validated on the
    /// archive: 0 of 318k kills and 0 per-shot events in two years of demos; the human tails end
    /// at 4.53° approach / 0.153° landing, so both knobs have real margin — do not tighten.
    /// Runs on every shot including mid-spray (the classic spray-aimbot pulls one bullet).
    /// </summary>
    [JsonPropertyName("EnableSnap")]
    public bool EnableSnap { get; set; } = true;

    /// <summary>Head-centre error at the shot that counts as machine precision. One quant step ≈ 0.044°.</summary>
    public float SnapExactDeg { get; set; } = 0.05f;

    /// <summary>Off the head one tick before = acquired this tick, not held/tracked. Closest human approach: 4.53°.</summary>
    public float SnapOffFloorDeg { get; set; } = 5f;

    /// <summary>Repeated pulls required — a single one could be tick-aliasing.</summary>
    public int SnapMinSnaps { get; set; } = 2;

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

    /// <summary>Headshot kills landed mid-spin before the spin-hs-kill edge fires. The first is always
    /// silent at 2 (a lucky HS mid-trickshot-360 is a fluke, not a bot); a spinbot re-qualifies every
    /// engagement, so 2 costs it exactly two kills. Raise for more margin, lower to 1 only if you
    /// accept that a genuine spinning trickshot headshot (rare but real) triggers the action.</summary>
    [JsonPropertyName("SpinbotMinSpinHsKills")]
    public int SpinbotMinSpinHsKills { get; set; } = 2;

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
    /// so a held angle an enemy crosses no longer flags (the main live-data false positive).
    /// A near-stationary enemy is excluded because bearing is observer-relative: the observer's
    /// OWN movement generates a bearing sweep past a standing enemy, which is not tracking.
    /// 100 measured on the 21-demo corpus: legit noise −23% while every cheater signal survives;
    /// 150+ starts eating true positives.</summary>
    [JsonPropertyName("WallhackMinEnemyMoveUnits")]
    public float WallhackMinEnemyMoveUnits { get; set; } = 100f;

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

    // Geometric gate (CS2FOW .bvh8 bakes; see docs/visibility-oracle.md). Off by default until
    // live-validated. With no bake for the current map the gate simply doesn't apply — the
    // detector falls back to spotted-only behaviour, never off.

    /// <summary>Require wallhack.track candidates to be provably occluded by static geometry AND
    /// unspotted by the observer's entire team. Measured (21 demos, 313 sessions): legit noise
    /// 65→15 sessions, best-sampled cheater kept at 4.3x the highest legit rate.</summary>
    [JsonPropertyName("WallhackGeoGate")]
    public bool WallhackGeoGate { get; set; } = false;

    /// <summary>Directory holding <c>&lt;map&gt;.bvh8</c> bakes. Relative paths resolve against the
    /// plugin module directory; empty disables bake loading entirely.</summary>
    [JsonPropertyName("BakesDir")]
    public string BakesDir { get; set; } = "../../bakes";

    /// <summary>Enemy speed (u/s) at or below which it emits no footsteps. A geo-gated signal on
    /// such a silent enemy has no legitimate information channel at all (~1% of legit sessions);
    /// its confidence is scaled by <see cref="WallhackGeoQuietBoost"/>.</summary>
    [JsonPropertyName("WallhackGeoQuietSpeedUnits")]
    public float WallhackGeoQuietSpeedUnits { get; set; } = 120f;

    /// <summary>Confidence multiplier (clamped to 1.0) for geo-gated signals on a silent enemy.</summary>
    [JsonPropertyName("WallhackGeoQuietBoost")]
    public float WallhackGeoQuietBoost { get; set; } = 1.5f;

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

    /// <summary>
    /// Discordant McNemar observations (present-hit-not-past PLUS past-hit-not-present) required
    /// before the z-score is trusted enough to emit. The 20 Hz polls are heavily autocorrelated —
    /// one engagement yields a run of all-present-only samples, so z is meaningless at small
    /// counts: 5 days of live shadow data (2026-07-31→08-04) showed legit regulars hitting 97–100%
    /// present-rate on 30-sample bursts (z≈9 inside 20 seconds). At 400 the burst noise has washed
    /// out; the one confirmed cheater in that window reached 400+ discordant samples within ~4
    /// minutes of joining, so the evidence gate costs little detection latency.
    /// </summary>
    [JsonPropertyName("NullTestMinObservations")]
    public int NullTestMinObservations { get; set; } = 400;

    /// <summary>
    /// Fusion weight for the null test — deliberately below 1.0 because the axis corroborates
    /// rather than convicts: skilled players genuinely aim where unseen enemies ARE (sound,
    /// game sense), so a universal positive present-bias exists in the honest population. On the
    /// 5-day live window, weight 0.5 with MinObservations 400 put exactly one session at Review —
    /// the confirmed strafe.one cheater (simulated peak 3.09) — and zero of the 96 legit sessions
    /// the previous defaults would have flagged.
    /// </summary>
    [JsonPropertyName("NullTestWeight")]
    public float NullTestWeight { get; set; } = 0.5f;

    /// <summary>
    /// McNemar z-score at/above which the null test emits. z is a standardised statistic, so this
    /// is largely self-calibrating and server-independent: z≈3 ≈ 99.9% confidence the present-over-
    /// past asymmetry is not chance. Raise for fewer/stronger flags. A regular with no real effect
    /// keeps z≈0 however long they play, so this no longer confounds playtime the way raw excess did.
    /// </summary>
    [JsonPropertyName("NullTestMinZ")]
    public float NullTestMinZ { get; set; } = 3.0f;

    /// <summary>
    /// Rest-of-population discordant observations required before the population-relative gate
    /// engages. Live data showed absolute z is map-dependent (night-variant maps inflate the whole
    /// population), so a player must ALSO stand out from the concurrent map population (two-
    /// proportion z ≥ NullTestMinZ) once this much peer evidence exists. Below it, the absolute
    /// test runs alone. The gate only ever suppresses emissions — never adds them.
    /// </summary>
    [JsonPropertyName("NullTestMinPopObservations")]
    public int NullTestMinPopObservations { get; set; } = 200;
}
