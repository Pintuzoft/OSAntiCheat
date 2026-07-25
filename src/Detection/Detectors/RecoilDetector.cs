using OSAntiCheat.Tracking;

namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags anti-recoil (script / macro / mouse-firmware): recoil compensation that is TOO consistent.
/// A human's spray-to-spray control varies (irreducible motor noise); a script's counter-pattern is
/// identical every time. Measured server-side, so it catches the behaviour regardless of source.
///
/// A <see cref="DetectorKind.LogicBreach"/> axis: the human floor is ratio ~0.06 (measured over 17k
/// archive spray-sessions), a synthetic perfect script sits at ~0.00, and there is a GAP between — a
/// threshold ~0.04 sits below any human. Narrow but credible: catches naive scripts (low recall — a
/// script that injects >=0.15° jitter climbs into the human range and hides), never false-positives a
/// human (nobody reaches below the floor). Same spray metric as the offline DemoReplay tool.
/// </summary>
public sealed class RecoilDetector : IDetector
{
    public string Id => "anti-recoil";
    public float Weight => 1.6f;
    public DetectorKind Kind => DetectorKind.LogicBreach;

    private const float SprayGapSeconds = 0.13f; // a gap longer than this ends the spray
    private const int MinSprayShots = 6;         // a real spray, not a tap
    private const int CurveLen = 8;              // compare the first 8 shots of each spray
    private const int MaxSpraysKept = 12;        // rolling window of recent sprays per weapon
    private const float MinPullDeg = 2f;         // real recoil actually compensated (excludes tapping)

    private readonly float _maxRatio;            // below the human floor 0.06; default 0.04
    private readonly int _minSprays;             // sprays of one weapon needed before judging

    private readonly Dictionary<int, List<(float p, float y)>> _cur = new();     // current spray per slot
    private readonly Dictionary<int, string> _curWeapon = new();
    private readonly Dictionary<int, float> _lastFire = new();
    private readonly Dictionary<(int slot, string weapon), List<(float p, float y)[]>> _sprays = new();

    public RecoilDetector(float maxRatio = 0.04f, int minSprays = 4)
    {
        _maxRatio = maxRatio;
        _minSprays = Math.Max(2, minSprays);
    }

    public void Remove(int slot)
    {
        _cur.Remove(slot);
        _curWeapon.Remove(slot);
        _lastFire.Remove(slot);
        foreach (var key in _sprays.Keys.Where(k => k.slot == slot).ToList()) _sprays.Remove(key);
    }

    /// <summary>
    /// Called on a player's shot. Folds it into the current spray; when a spray completes (gap or
    /// weapon change), evaluates the weapon's recent sprays and returns a signal if the consistency
    /// is below the human motor-noise floor.
    /// </summary>
    public Signal? OnFire(PlayerTracker shooter, string weapon, float now)
    {
        int slot = shooter.Slot;
        weapon = (weapon ?? "").ToLowerInvariant();

        bool cont = _lastFire.TryGetValue(slot, out var lf) && now - lf < SprayGapSeconds &&
                    _curWeapon.GetValueOrDefault(slot) == weapon;
        _lastFire[slot] = now;

        Signal? signal = null;
        if (!cont)
        {
            signal = FlushAndEvaluate(slot, now); // the previous spray just ended
            _curWeapon[slot] = weapon;
            _cur[slot] = new List<(float, float)>();
        }
        if (shooter.TryLatest(out var s))
            _cur.GetValueOrDefault(slot)?.Add((s.Angles.Pitch, s.Angles.Yaw));
        return signal;
    }

    private Signal? FlushAndEvaluate(int slot, float now)
    {
        if (!_cur.TryGetValue(slot, out var curve) || curve.Count < MinSprayShots ||
            !_curWeapon.TryGetValue(slot, out var weapon))
        {
            _cur.Remove(slot);
            return null;
        }

        var key = (slot, weapon);
        if (!_sprays.TryGetValue(key, out var list)) _sprays[key] = list = new List<(float, float)[]>();
        list.Add(curve.ToArray());
        if (list.Count > MaxSpraysKept) list.RemoveAt(0);
        _cur.Remove(slot);

        if (list.Count < _minSprays) return null;

        int L = Math.Min(CurveLen, list.Min(s => s.Length));
        if (L < 2) return null;

        // Δ-from-start curves; yaw unwrapped so a spray crossing ±180° is fine.
        var curves = list.Select(s =>
        {
            var d = new (float p, float y)[L];
            for (int k = 0; k < L; k++)
            {
                float dp = s[k].p - s[0].p;
                float dy = s[k].y - s[0].y;
                dy -= 360f * MathF.Round(dy / 360f);
                d[k] = (dp, dy);
            }
            return d;
        }).ToList();

        float spreadSum = 0f, pullSum = 0f;
        int idxN = 0, nn = curves.Count;
        for (int k = 1; k < L; k++)
        {
            float mp = 0f, my = 0f;
            foreach (var c in curves) { mp += c[k].p; my += c[k].y; }
            mp /= nn; my /= nn;
            float v = 0f;
            foreach (var c in curves) { float dp = c[k].p - mp, dy = c[k].y - my; v += dp * dp + dy * dy; }
            spreadSum += MathF.Sqrt(v / nn);                 // how much the sprays differ at shot k
            pullSum += MathF.Sqrt(mp * mp + my * my);        // magnitude of the mean compensation at k
            idxN++;
        }
        if (idxN == 0) return null;

        float spread = spreadSum / idxN;
        float pull = pullSum / idxN;
        if (pull < MinPullDeg) return null;                  // barely compensated — tapping, not a spray

        // Normalise by how much was actually compensated: a script is tiny relative to its own pull.
        float ratio = spread / MathF.Max(pull, 0.5f);
        if (ratio >= _maxRatio) return null;                 // within human range — the floor holds

        float confidence = Math.Clamp(0.85f + 0.15f * (_maxRatio - ratio) / _maxRatio, 0.85f, 1f);
        return new Signal(
            Id, slot, now, confidence,
            $"recoil ratio {ratio:F3} over {list.Count} {weapon} sprays (human floor ~0.06) — beyond human");
    }
}
