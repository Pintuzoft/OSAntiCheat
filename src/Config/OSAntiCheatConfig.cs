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
    // Bump when adding fields. WARNING (observed live, v23→v24 2026-08-15 04:46): CounterStrikeSharp
    // DOES rewrite the config file on a version bump — every hand-edited value resets to the defaults
    // below. Pinned server values must therefore live in OSAntiCheat.local.json (the overlay, v0.9.97),
    // which regeneration never touches. After any schema-bump release: verify the overlay file exists
    // and that the load log shows its keys applied.
    public override int Version { get; set; } = 26;

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

    /// <summary>Minimum seconds between two Watch-tier admin chat notices, across all players —
    /// keeps the chat readable when an axis fires broadly. Per player it is stricter still: one
    /// Watch and one Review notice per map, ever. Review notices bypass this window (rare and
    /// serious), auto-action notices are never throttled, and the JSONL log always gets every
    /// alert. 0 disables the window.</summary>
    [JsonPropertyName("AdminChatWatchQuietSeconds")]
    public float AdminChatWatchQuietSeconds { get; set; } = 60f;

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
    /// Which edges may trigger the auto-action. Available: "spin-hs-kill" (headshot landed while
    /// >360° of continuous rotation is still whirling at the kill tick, repeated), "fake-pitch"
    /// (view pitch past the engine's server-side ±89° clamp for consecutive ticks), "name-churn"
    /// (repeated in-game renames — nick-changer; the one non-physics edge, gated at measured-zero:
    /// no honest player held two names within a session across 910 logged sessions, remove it here
    /// if your population jokes with renames), "blind-hs-burst" (≥ KillBurstMinKills headshot kills
    /// inside KillBurstWindowSeconds on DISTINCT enemies the killer never once saw this map — the
    /// wall+aim ace signature; 2 bursts of ≥4 in 321k archive kills, both confirmed cheaters).
    /// The poll-based continuous-spin and yaw-jitter
    /// signals carry no edge by design — strong, but log+fusion-only until they earn the same bar.
    /// </summary>
    [JsonPropertyName("AutoActionEdges")]
    public string[] AutoActionEdges { get; set; } = { "spin-hs-kill", "fake-pitch", "name-churn", "blind-hs-burst" };

    /// <summary>
    /// Command run on a confirmed edge. Placeholders: {slot} {userid} {steamid} {name} {detector}
    /// {edge}. Empty = log only. Default kicks; escalate to your ban system (e.g.
    /// "css_ban {steamid} 0 cheating") only after watching the kick log for a while — a wrong kick
    /// costs a reconnect, a wrong ban a player. Keep the message BLUNT: the player and everyone
    /// watching should understand it was a cheat detection, not some vague technical hiccup.
    /// </summary>
    [JsonPropertyName("AutoActionCommand")]
    public string AutoActionCommand { get; set; } =
        "kickid {userid} [OSAC] CHEAT DETECTED: {detector} - kicked by anticheat";

    /// <summary>Optional public chat announce when the action runs. Same placeholders as
    /// <see cref="AutoActionCommand"/>. Empty = silent. Kept detector-neutral: "input impossible
    /// for a human" was accurate for the physics edges but not for name-churn — the message must
    /// never overclaim, or the first arguable kick costs the system its credibility.</summary>
    [JsonPropertyName("AutoActionAnnounce")]
    public string AutoActionAnnounce { get; set; } =
        "[OSAC] CHEAT DETECTED — {name} was kicked ({detector})";

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
    /// Bunnyhop/strafe script (movement.airgain): horizontal speed gained WHILE AIRBORNE across a
    /// chain of jumps. The engine clamps takeoff speed but air-strafe physics after the clamp is
    /// shared — a bot syncing strafe+yaw per tick pumps back ~50–100 u/s per hop (C9: +71 windowed median,
    /// honest windowed max +21). Surf is structurally excluded (a ramp ride is one
    /// long airborne phase, not a chain of jump arcs). The stack's only movement axis — fully
    /// independent corroboration for the wall/aim cheats bhop ships with.
    /// </summary>
    [JsonPropertyName("EnableAirGain")]
    public bool EnableAirGain { get; set; } = true;

    /// <summary>Chained arcs needed in the window before the axis says anything. Four-arc windows
    /// reach +33.5 median honestly (one lucky downhill run); five-arc windows top out at +21.0.</summary>
    public int AirGainMinArcs { get; set; } = 5;

    /// <summary>Median air gain (u/s per hop) for the fusion whisper. Honest windowed max: 21.0.</summary>
    public float AirGainSignalMedianGain { get; set; } = 25f;

    /// <summary>Median per-arc peak speed (u/s) the whisper also demands. Sprint cap is 250: a
    /// script bhops to go FASTER than running, so a chain whose peaks stay under sprint speed is a
    /// hand losing speed to its landings and strafing some back — verified human on a live FP
    /// (2026-08-15 de_vandal: median gain +37 but median peak 216, an R1 slow-hopping).</summary>
    public float AirGainSignalMinPeakSpeed { get; set; } = 250f;

    /// <summary>Chained arcs needed for the auto-action edge (a downhill burst dies by hop three).</summary>
    public int AirGainEdgeMinArcs { get; set; } = 5;

    /// <summary>Median air gain (u/s per hop) for the edge — ~3× the honest maximum ever measured.</summary>
    public float AirGainEdgeMedianGain { get; set; } = 40f;

    /// <summary>Median per-arc peak speed (u/s) the edge also demands. Sprint cap is 250.</summary>
    public float AirGainEdgeMinPeakSpeed { get; set; } = 300f;

    /// <summary>
    /// Arm the freeze response. False (default) = LATENT: the edge still fires, fuses, alerts red
    /// and hands admins the evidence pair + SteamID, and the action log records the DRY-RUN — but
    /// nobody actually freezes. Same ladder the kick edges climbed (they shipped dry-run too, v0.9.8):
    /// let the first REAL bhop script walk into the trap, verify the would-have-frozen record is
    /// clean, then arm. Regular folk must never be the test.
    /// </summary>
    public bool AirGainFreezeArmed { get; set; } = false;

    /// <summary>
    /// Freeze response for the airgain edge: the pawn freezes IN PLACE (mid-air included) and is
    /// disarmed. Negative = REST OF THE ROUND (default; the next respawn thaws — and until then the
    /// frozen cheat is a free target). Positive = that many seconds, then thaw. 0 disables the
    /// freeze — the edge then goes through the generic <see cref="AutoActionCommand"/> path instead
    /// (requires "airgain-chain" in <see cref="AutoActionEdges"/>).
    /// </summary>
    public float AirGainFreezeSeconds { get; set; } = -1f;

    /// <summary>Public chat line when the freeze lands. Playful on purpose (the owner's voice) but
    /// never overclaiming: "impossible acceleration" is literally the measurement. Placeholders:
    /// {name} {steamid} {detector}. Empty = silent.</summary>
    public string AirGainFreezeAnnounce { get; set; } =
        " [OSAC] SPEEDING TICKET: {name} — impossible mid-air acceleration (bunnyhop script). Parked as a statue until the round ends.";

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

    /// <summary>
    /// Nick-changer detection: repeated in-game renames within a rolling window. Demo-measured on
    /// the captured cheater (2026-08-04): an animated marquee nick, 614 changes in ~8.5 min
    /// (~1.3/s); every other player in the demo had zero. Honest mid-match renames happen — but as
    /// isolated events, so the gate is RATE-based: 3 inside 20 s is ~2 s of marquee and
    /// unreachable by hand.
    /// </summary>
    [JsonPropertyName("EnableNameChange")]
    public bool EnableNameChange { get; set; } = true;

    /// <summary>Name changes within the window before the name-churn edge fires.</summary>
    [JsonPropertyName("NameChangeMinChanges")]
    public int NameChangeMinChanges { get; set; } = 3;

    /// <summary>Rolling window (seconds) the rename count is evaluated over. 20 s + 3 changes =
    /// a rate no manual renamer reaches (three deliberate Steam renames take far longer), while
    /// the measured cheat marquee crosses it within ~2 s.</summary>
    [JsonPropertyName("NameChangeWindowSeconds")]
    public float NameChangeWindowSeconds { get; set; } = 20f;

    /// <summary>
    /// Blind headshot burst (wallhack.killburst): repeated headshot kills on DISTINCT enemies the
    /// killer has never once had in their spotted mask this map, inside a rolling window. The
    /// wall+aim ace signature (C8, 2026-08-07: six scout HS in 12.1 s running from spawn zoomed —
    /// missed by every parked/step/lateral-tracking axis because an intercept course produces none
    /// of those shapes). Validated on 321,423 archive kills: ≥4 occurs exactly twice, both
    /// confirmed cheaters; the honest tail ends at 3 (pistol-round openings, when the sight
    /// history is empty for everyone).
    /// </summary>
    [JsonPropertyName("EnableKillBurst")]
    public bool EnableKillBurst { get; set; } = true;

    /// <summary>Distinct never-seen headshot victims inside the window before the blind-hs-burst
    /// edge fires. Honest archive maximum: 3 (pistol rounds). Do not lower to 3.</summary>
    [JsonPropertyName("KillBurstMinKills")]
    public int KillBurstMinKills { get; set; } = 4;

    /// <summary>Rolling window (seconds) the blind-HS burst is evaluated over. The measured C8 ace
    /// put its 4th distinct blind HS 4.8 s after the first; both archive cheater bursts fit well
    /// inside 15 s.</summary>
    [JsonPropertyName("KillBurstWindowSeconds")]
    public float KillBurstWindowSeconds { get; set; } = 15f;

    /// <summary>
    /// Aim drift (aim.drift): fraction of the player's moving aim steps (>0.1°, nearest enemy
    /// within 15°) that REDUCE the error toward that enemy, tested per-lobby (two-proportion z vs
    /// everyone else on the map). Population-read on 305 honest sessions / 23 demos: median
    /// 51.1%, absolute max 56.6%, per-lobby-z max 2.79 — while C8 (soft aim) sat at 59.6%
    /// (z=+4.40) and C3 (multihack) at 56.0% (z=+4.03), both above every honest session.
    /// Behavioural fusion axis, no edge: it can never kick; it raises the suspicion score so a
    /// threshold crossing surfaces as the Watch-tier admin notice. Abstains when the lobby
    /// baseline is thin. Blind to silent aim by construction (the view never moves).
    /// </summary>
    [JsonPropertyName("EnableAimDrift")]
    public bool EnableAimDrift { get; set; } = true;

    /// <summary>Moving engaged aim steps required before the drift rate is trusted (~30–60 s of
    /// active combat; binomial SE at 500 is ±2.2%). No kills are needed — the step is the vote.</summary>
    [JsonPropertyName("AimDriftMinSteps")]
    public int AimDriftMinSteps { get; set; } = 500;

    /// <summary>Per-lobby z at/above which the detector emits. Honest corpus max: 2.79 — keep ≥3.</summary>
    [JsonPropertyName("AimDriftMinZ")]
    public float AimDriftMinZ { get; set; } = 3.0f;

    /// <summary>Rest-of-lobby steps required before the baseline is trusted; below it the
    /// detector abstains entirely (a thin lobby is not a null).</summary>
    [JsonPropertyName("AimDriftMinPopSteps")]
    public int AimDriftMinPopSteps { get; set; } = 3000;

    /// <summary>Fusion weight. Corroborating axis — same tier as the null test, never a verdict.</summary>
    [JsonPropertyName("AimDriftWeight")]
    public float AimDriftWeight { get; set; } = 0.5f;

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

    // Geometric gate (CS2FOW .bvh8 bakes; see docs/visibility-oracle.md). On by default since
    // v0.9.96+: live-validated across 27 maps (2026-08, bake-on-load held throughout). With no
    // bake for the current map the gate simply doesn't apply — the detector falls back to
    // spotted-only behaviour, never off.

    /// <summary>Require wallhack.track candidates to be provably occluded by static geometry AND
    /// unspotted by the observer's entire team. Measured (21 demos, 313 sessions): legit noise
    /// 65→15 sessions, best-sampled cheater kept at 4.3x the highest legit rate.</summary>
    [JsonPropertyName("WallhackGeoGate")]
    public bool WallhackGeoGate { get; set; } = true;

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

    /// <summary>
    /// Minimum players on teams (bots counted only when IncludeBots) before the information axes
    /// (wallhack.nulltest, aim.drift) sample at all. In small lobbies both legs of these tests are
    /// structurally biased: with one or two enemies a regular's pre-aim knowledge is near-perfect
    /// (audited 2026-08-13: two 3-player matches gave every participant nulltest z=5–7 live and
    /// symmetric pre-visible convergences in replay), and the "rest of population" baseline is one
    /// or two peers — too thin to mean anything even after it clears the observation floors.
    /// Samples taken below this line are not just muted, they are never collected, so a lobby that
    /// later grows starts its evidence clean. Weapon-axes (snap, recoil, killburst…) are untouched:
    /// their physics does not change with lobby size.
    /// </summary>
    [JsonPropertyName("InfoAxesMinPlayers")]
    public int InfoAxesMinPlayers { get; set; } = 6;
}
