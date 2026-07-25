using System.Numerics;
using OSAntiCheat.Model;

namespace OSAntiCheat.Detection;

/// <summary>
/// Angle / vector helpers shared by the aim-based detectors. Uses Source-engine
/// conventions: yaw around Z (left/right), pitch up/down, degrees.
/// </summary>
public static class Geometry
{
    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>Unit forward direction the view angles point along (Source AngleVectors).</summary>
    public static Vector3 Forward(in ViewAngles a)
    {
        float p = a.Pitch * Deg2Rad;
        float y = a.Yaw * Deg2Rad;
        float cp = MathF.Cos(p);
        return new Vector3(cp * MathF.Cos(y), cp * MathF.Sin(y), -MathF.Sin(p));
    }

    /// <summary>Smallest signed difference between two yaw angles, wrapped to [-180, 180].</summary>
    public static float YawDelta(float fromYaw, float toYaw)
    {
        float d = (toYaw - fromYaw) % 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    /// <summary>Total angular distance between two view directions, in degrees.</summary>
    public static float AngleBetween(in ViewAngles a, in ViewAngles b)
    {
        var fa = Forward(a);
        var fb = Forward(b);
        float dot = Math.Clamp(Vector3.Dot(fa, fb), -1f, 1f);
        return MathF.Acos(dot) / Deg2Rad;
    }

    /// <summary>
    /// Angle (degrees) between where the shooter is looking and the direction to a target
    /// point. Zero means the crosshair is exactly on the point.
    /// </summary>
    public static float AimErrorTo(Vector3 eye, in ViewAngles aim, Vector3 target)
    {
        var toTarget = target - eye;
        if (toTarget.LengthSquared() < 1e-6f) return 0f;
        var dir = Vector3.Normalize(toTarget);
        float dot = Math.Clamp(Vector3.Dot(Forward(aim), dir), -1f, 1f);
        return MathF.Acos(dot) / Deg2Rad;
    }

    // Approximate hurtbox as a vertical capsule sampled at feet / chest / head (Z above feet).
    // Hitbox-agnostic on purpose: a cheat may lock to any body part, so we take the closest.
    private static readonly float[] BodyHeights = { 8f, 46f, 64f };

    /// <summary>
    /// Smallest aim error (degrees) from the shooter's aim to any part of an enemy's body,
    /// given the enemy's feet position. Uses the nearest hurtbox sample, never the head alone.
    /// </summary>
    public static float NearestBodyAimError(Vector3 eye, in ViewAngles aim, Vector3 enemyFeet)
    {
        float best = float.MaxValue;
        foreach (float h in BodyHeights)
        {
            float err = AimErrorTo(eye, aim, enemyFeet + new Vector3(0f, 0f, h));
            if (err < best) best = err;
        }
        return best;
    }
}
