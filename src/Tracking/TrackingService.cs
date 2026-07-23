using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using OSAntiCheat.Model;

namespace OSAntiCheat.Tracking;

/// <summary>
/// Samples every player's server-observable state once per server tick and stores it
/// in a per-player <see cref="PlayerTracker"/> ring buffer. This is the shared data
/// source all detectors read from — no detector touches engine entities directly.
/// </summary>
public sealed class TrackingService
{
    // ~2 seconds of history at 64 tick — enough for snap / tracking / reaction windows.
    private const int BufferCapacity = 128;

    private readonly Dictionary<int, PlayerTracker> _trackers = new();
    private int _sequence;

    public IReadOnlyDictionary<int, PlayerTracker> Trackers => _trackers;

    public PlayerTracker? For(int slot) =>
        _trackers.TryGetValue(slot, out var tracker) ? tracker : null;

    /// <summary>Registered as a per-tick listener; captures one sample per live player.</summary>
    public void OnTick()
    {
        _sequence++;
        float now = Server.CurrentTime;

        foreach (var controller in Utilities.GetPlayers())
        {
            if (!controller.IsValid || controller.IsHLTV) continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) continue;

            var origin = pawn.AbsOrigin;
            if (origin is null) continue;

            var angles = pawn.EyeAngles;
            var velocity = pawn.AbsVelocity;

            bool alive = pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
            bool onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;

            var sample = new TickSample(
                Sequence: _sequence,
                Time: now,
                Origin: new Vector3(origin.X, origin.Y, origin.Z),
                Angles: new ViewAngles(angles.X, angles.Y, angles.Z),
                Velocity: new Vector3(velocity.X, velocity.Y, velocity.Z),
                OnGround: onGround,
                Alive: alive);

            if (!_trackers.TryGetValue(controller.Slot, out var tracker))
            {
                tracker = new PlayerTracker(BufferCapacity, controller.Slot);
                _trackers[controller.Slot] = tracker;
            }
            tracker.Add(in sample);
        }
    }

    public void Remove(int slot) => _trackers.Remove(slot);

    public void Reset() => _trackers.Clear();
}
