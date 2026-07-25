using System.Numerics;

namespace OSAntiCheat.Model;

/// <summary>View angles in degrees. Pitch = up/down, Yaw = left/right, Roll = tilt.</summary>
public readonly record struct ViewAngles(float Pitch, float Yaw, float Roll);

/// <summary>
/// One immutable snapshot of a player's server-observable state at a single tick.
///
/// Stores ONLY primitives / value types — never engine handles or entity pointers —
/// so the ring buffer stays valid across ticks, respawns and entity lifecycles.
/// This is the sole data source every detector reads from.
/// </summary>
public readonly record struct TickSample(
    int Sequence,       // monotonically increasing sample index
    float Time,         // server time in seconds
    Vector3 Origin,     // world position (feet)
    ViewAngles Angles,  // eye angles
    Vector3 Velocity,   // absolute velocity
    bool OnGround,
    bool Alive);
