using System.Numerics;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags a player whose aim TRACKS a moving unspotted enemy through geometry — the crosshair
/// actually follows the enemy's angular movement, not merely contains it in a cone.
///
/// LOS comes from CS2's spotted system (a client wallhack never sets server spotted state).
/// The critical discriminator, learned from live data: requiring only that the enemy moves
/// inside the aim cone flags the whole server, because holding a common angle while an enemy
/// walks past behind the wall satisfies it. So we additionally require the observer's VIEW to
/// have followed the enemy's bearing change — a held (static) angle an enemy crosses does not
/// qualify. One signal per continuous track; the fusion engine accumulates repeats.
/// </summary>
public sealed class WallhackDetector : IDetector
{
    public string Id => "wallhack.track";
    public float Weight => 1.0f;

    /// <summary>The unspotted enemy an observer is aiming at, supplied by the plugin per poll.</summary>
    public readonly record struct WallTarget(
        int EnemyId, Vector3 EnemyPos, float AimErrDeg, float ViewYaw, float EnemyBearingYaw);

    private const float AimRefDeg = 5f;    // confidence scaling
    private const float MinYawRate = 0.1f; // deg/poll below which nothing is really moving

    private readonly float _minTrackSeconds;
    private readonly float _minEnemyMoveUnits;
    private readonly float _minBearingChangeDeg;  // enemy must sweep a real arc, not drift
    private readonly float _followFraction;       // view must follow >= this share of the sweep
    private readonly float _minBearingRateDeg;    // and sweep it at a real rate, not over a minute

    private readonly Dictionary<int, ObserverState> _state = new();

    // Defaults come from a parameter sweep against demos containing three admin-banned
    // cheaters plus their matches' legit players — not from guesswork. See tools/Sweep.
    public WallhackDetector(
        float minTrackSeconds = 0.4f, float minEnemyMoveUnits = 0f,
        float minBearingChangeDeg = 20f, float followFraction = 0.5f,
        float minBearingRateDegPerSec = 10f)
    {
        _minTrackSeconds = MathF.Max(0.05f, minTrackSeconds);
        _minEnemyMoveUnits = MathF.Max(0f, minEnemyMoveUnits);
        _minBearingChangeDeg = MathF.Max(0f, minBearingChangeDeg);
        _followFraction = Math.Clamp(followFraction, 0f, 1f);
        _minBearingRateDeg = MathF.Max(0f, minBearingRateDegPerSec);
    }

    public void Remove(int slot) => _state.Remove(slot);
    public void Reset() => _state.Clear();

    public Signal? Observe(int observerSlot, float now, WallTarget? target)
    {
        if (!_state.TryGetValue(observerSlot, out var st))
            _state[observerSlot] = st = new ObserverState();

        if (target is not { } t || t.EnemyId != st.EnemyId)
        {
            st.EnemyId = target?.EnemyId ?? -1;
            st.StartTime = now;
            st.LastPos = target?.EnemyPos ?? default;
            st.LastViewYaw = target?.ViewYaw ?? 0f;
            st.LastBearingYaw = target?.EnemyBearingYaw ?? 0f;
            st.HasLast = target is not null;
            st.PathLength = 0f;
            st.Followed = 0f;
            st.TotalBearing = 0f;
            st.Fired = false;
            return null;
        }

        st.PathLength += Vector3.Distance(st.LastPos, t.EnemyPos);
        st.LastPos = t.EnemyPos;

        if (st.HasLast)
        {
            float viewDelta = Geometry.YawDelta(st.LastViewYaw, t.ViewYaw);
            float bearingDelta = Geometry.YawDelta(st.LastBearingYaw, t.EnemyBearingYaw);
            st.TotalBearing += MathF.Abs(bearingDelta);
            // Credit the view for following only when it moves the same way as the bearing.
            if (MathF.Abs(bearingDelta) >= MinYawRate &&
                MathF.Sign(viewDelta) == MathF.Sign(bearingDelta))
            {
                st.Followed += MathF.Min(MathF.Abs(viewDelta), MathF.Abs(bearingDelta));
            }
        }
        st.LastViewYaw = t.ViewYaw;
        st.LastBearingYaw = t.EnemyBearingYaw;
        st.HasLast = true;

        float duration = now - st.StartTime;
        // The enemy must sweep a real arc, at a real rate, and the view must follow most of it.
        // (Live data: 5-15 deg arcs are crosshair micro-jitter, and a 30 deg sweep spread over
        // 49 s is a slow drift — neither is tracking.)
        float bearingRate = duration > 0f ? st.TotalBearing / duration : 0f;
        bool followsMovement =
            st.TotalBearing >= _minBearingChangeDeg &&
            bearingRate >= _minBearingRateDeg &&
            st.Followed >= _followFraction * st.TotalBearing;

        if (st.Fired || duration < _minTrackSeconds ||
            st.PathLength < _minEnemyMoveUnits || !followsMovement)
            return null;

        st.Fired = true;
        float confidence = Math.Clamp(1f - t.AimErrDeg / AimRefDeg, 0.4f, 1f);
        return new Signal(
            Id, observerSlot, now, confidence,
            $"aim followed an unspotted enemy through geometry for {duration * 1000f:F0}ms " +
            $"(enemy moved {st.PathLength:F0}u, view followed {st.Followed:F0}° of {st.TotalBearing:F0}° " +
            $"bearing, aimErr {t.AimErrDeg:F1}°)");
    }

    private sealed class ObserverState
    {
        public int EnemyId = -1;
        public float StartTime;
        public Vector3 LastPos;
        public float LastViewYaw;
        public float LastBearingYaw;
        public bool HasLast;
        public float PathLength;
        public float Followed;
        public float TotalBearing;
        public bool Fired;
    }
}
