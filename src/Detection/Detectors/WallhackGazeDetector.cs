namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// The "smart", soft counterpart to <see cref="WallhackDetector"/>. Instead of a precise aim
/// lock, it flags a player whose *gaze follows* an unspotted enemy's movement through geometry —
/// glancing at and tracking enemies you can't legitimately see.
///
/// Core discriminator: the player's view yaw must move IN STEP with the enemy's bearing change
/// (they sweep to follow the enemy), not merely point in the enemy's general direction (which a
/// learned common-angle would also do). A follow-score accumulates with each matching poll and
/// decays over time; round-start polls (no one spotted, no sounds, no callouts yet) are weighted
/// higher. Emits when the score crosses a threshold; the fusion engine accumulates repeats.
/// </summary>
public sealed class WallhackGazeDetector : IDetector
{
    public string Id => "wallhack.gaze";
    public float Weight => 1.0f;

    /// <summary>Per-poll observation: the unspotted enemy the observer's gaze is nearest to (or none).</summary>
    public readonly record struct GazeSample(
        int EnemyId,          // -1 / null target => no unspotted enemy in the gaze cone
        float AngleOffDeg,    // 3D angle between view and the enemy
        float ViewYaw,        // observer's view yaw (deg)
        float EnemyBearingYaw,// yaw from observer to the enemy (deg)
        bool RoundStart);     // within the round-start window

    private const float DecayTau = 3.0f;      // seconds; follow-score fades toward 0
    private const float MinYawRate = 0.15f;   // deg/poll; below this nothing is really moving
    private const float RateTolerance = 2.5f; // deg; how closely view rate must match bearing rate

    private readonly float _coneDeg;
    private readonly float _triggerScore;
    private readonly float _roundStartMultiplier;

    private readonly Dictionary<int, State> _state = new();

    public WallhackGazeDetector(
        float coneDeg = 25f, float triggerScore = 0.6f, float roundStartMultiplier = 2.0f)
    {
        _coneDeg = MathF.Max(1f, coneDeg);
        _triggerScore = MathF.Max(0.1f, triggerScore);
        _roundStartMultiplier = MathF.Max(1f, roundStartMultiplier);
    }

    public void Remove(int slot) => _state.Remove(slot);
    public void Reset() => _state.Clear();

    public Signal? Observe(int slot, float now, GazeSample? sample)
    {
        if (!_state.TryGetValue(slot, out var st))
            _state[slot] = st = new State { LastUpdate = now };

        // Decay the accumulated follow-score toward zero.
        float dt = now - st.LastUpdate;
        if (dt > 0f) st.Score *= MathF.Exp(-dt / DecayTau);
        st.LastUpdate = now;

        // No unspotted enemy in the cone, or gaze switched enemies → break the follow continuity.
        if (sample is not { } s || s.EnemyId != st.EnemyId)
        {
            st.EnemyId = sample?.EnemyId ?? -1;
            st.LastViewYaw = sample?.ViewYaw ?? 0f;
            st.LastBearingYaw = sample?.EnemyBearingYaw ?? 0f;
            st.HasLast = sample is not null;
            return null;
        }

        if (st.HasLast)
        {
            float viewDelta = Geometry.YawDelta(st.LastViewYaw, s.ViewYaw);
            float bearingDelta = Geometry.YawDelta(st.LastBearingYaw, s.EnemyBearingYaw);

            // The enemy must actually be moving in bearing, and the view must move WITH it.
            if (MathF.Abs(bearingDelta) >= MinYawRate &&
                MathF.Abs(viewDelta) >= MinYawRate &&
                MathF.Sign(viewDelta) == MathF.Sign(bearingDelta))
            {
                float rateMatch = Math.Clamp(
                    1f - MathF.Abs(viewDelta - bearingDelta) / RateTolerance, 0f, 1f);
                float attention = Math.Clamp(1f - s.AngleOffDeg / _coneDeg, 0f, 1f);
                float mult = s.RoundStart ? _roundStartMultiplier : 1f;
                // Scale by elapsed time so the score is in "seconds of weighted following".
                st.Score += rateMatch * attention * mult * MathF.Max(dt, 0f);
            }
        }

        st.LastViewYaw = s.ViewYaw;
        st.LastBearingYaw = s.EnemyBearingYaw;
        st.HasLast = true;

        if (st.Score < _triggerScore) return null;

        float confidence = Math.Clamp(st.Score / (2f * _triggerScore), 0.3f, 1f);
        st.Score = 0f; // consume; the fusion engine accumulates repeated emissions
        return new Signal(
            Id, slot, now, confidence,
            $"gaze followed an unspotted enemy's movement through geometry" +
            (s.RoundStart ? " (round start)" : ""));
    }

    private sealed class State
    {
        public int EnemyId = -1;
        public float LastViewYaw;
        public float LastBearingYaw;
        public bool HasLast;
        public float Score;
        public float LastUpdate;
    }
}
