using System.Globalization;

// Synthetic anti-recoil validator. NO cheat is run: anti-recoil is deterministic (apply the inverse
// recoil each shot), so we model a scripted spray and push it through the SAME recoil-ratio metric
// DemoReplay computes on real demos, then compare to the human floor measured on 17k real sessions:
//   per-session min ratio ~0.06, per-player (median over >=2 sessions) floor ~0.21.
//
// The question this answers: is 0.06 the metric's MEASUREMENT floor (=> no script can separate, the
// axis is dead) or real human motor noise (=> a perfect/naive script sits BELOW it and IS catchable)?
// We read it off the sigma=0 row: a perfect script with realistic angle quantization.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const int Shots = 8;              // shots per spray analysed (matches DemoReplay RecoilCurveLen)
const int SpraysPerSession = 10;  // matches the well-sampled real sessions
const float TargetPull = 3.5f;    // mean compensation magnitude (deg), matches observed sessions

var rng = new Random(20260717);

// Base compensation a script applies every spray: cumulative pull, mostly down (pitch) with a small
// horizontal drift (yaw). The exact shape barely matters — the metric is spread/pull and both scale
// together; what matters is that a script REPEATS it identically. step tuned so mean|curve| ~= 3.5deg.
const float step = 0.82f;
var basePitch = new float[Shots];
var baseYaw = new float[Shots];
for (int k = 0; k < Shots; k++) { basePitch[k] = -step * k; baseYaw[k] = 0.30f * k; }

static float Gauss(Random r)
{
    double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
    return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
}
static float Quant(float x, float q) => q <= 0f ? x : MathF.Round(x / q) * q;

// One scripted session -> the recoil ratio, computed exactly as DemoReplay does.
float Ratio(float sigma, float q)
{
    var curves = new (float p, float y)[SpraysPerSession][];
    for (int s = 0; s < SpraysPerSession; s++)
    {
        // Each spray aims a DIFFERENT absolute direction, so the quantization grid lands differently
        // per spray — that, not the humanization noise, is where a PERFECT script's tiny cross-spray
        // spread comes from. The script's compensation itself is identical.
        float p0 = (float)(rng.NextDouble() * 360.0 - 180.0);
        float y0 = (float)(rng.NextDouble() * 360.0 - 180.0);
        var d = new (float p, float y)[Shots];
        float q0p = Quant(p0, q), q0y = Quant(y0, q);
        for (int k = 0; k < Shots; k++)
        {
            float pAbs = Quant(p0 + basePitch[k] + sigma * Gauss(rng), q);
            float yAbs = Quant(y0 + baseYaw[k] + sigma * Gauss(rng), q);
            float dy = (yAbs - q0y); dy -= 360f * MathF.Round(dy / 360f);
            d[k] = (pAbs - q0p, dy);
        }
        curves[s] = d;
    }

    float spreadSum = 0f, pullSum = 0f; int idxN = 0, n = SpraysPerSession;
    for (int k = 1; k < Shots; k++)
    {
        float mp = 0f, my = 0f;
        for (int s = 0; s < n; s++) { mp += curves[s][k].p; my += curves[s][k].y; }
        mp /= n; my /= n;
        float v = 0f;
        for (int s = 0; s < n; s++) { float dp = curves[s][k].p - mp, dy = curves[s][k].y - my; v += dp * dp + dy * dy; }
        spreadSum += MathF.Sqrt(v / n);
        pullSum += MathF.Sqrt(mp * mp + my * my);
        idxN++;
    }
    return (spreadSum / idxN) / MathF.Max(pullSum / idxN, 0.5f);
}

float RatioAvg(float sigma, float q, int sessions = 40)
{
    float sum = 0f; for (int i = 0; i < sessions; i++) sum += Ratio(sigma, q); return sum / sessions;
}

Console.WriteLine("Synthetic anti-recoil vs the recoil-ratio metric");
Console.WriteLine($"  {SpraysPerSession} sprays/session, {Shots} shots, mean pull ~{TargetPull:F1}deg");
Console.WriteLine("  HUMAN FLOOR (17k real sessions): per-session min ~0.06, per-player ~0.21\n");

float[] quants = { 0f, 0.005f, 0.02f, 0.05f };   // bracket the unknown CS2 angle-network step (deg)
float[] sigmas = { 0f, 0.05f, 0.10f, 0.15f, 0.20f, 0.30f, 0.50f };   // per-shot humanization noise (deg)

Console.Write("  humaniz.sigma |");
foreach (var q in quants) Console.Write($"   q={q:F3}");
Console.WriteLine("     <- angle quantization (deg)");
Console.WriteLine("  " + new string('-', 15 + quants.Length * 9));
foreach (var sig in sigmas)
{
    string tag = sig == 0f ? " (perfect)" : "";
    Console.Write($"  {sig,7:F2}{tag,-6} |");
    foreach (var q in quants) Console.Write($"  {RatioAvg(sig, q),6:F3}");
    Console.WriteLine();
}

Console.WriteLine("""

  Read it:
   * sigma=0 (perfect) row = identical compensation every spray. If its ratio is far BELOW 0.06,
     the human floor is real motor noise and a naive/unhumanized script IS separable (set a
     threshold in the gap). If it's near/above 0.06, 0.06 is the metric's own floor -> axis dead.
   * Down each column: the sigma where the ratio crosses 0.06 is how much per-shot noise a script
     must inject to hide inside the human range (the humanization cost).
   * The q columns bracket the real (unknown) CS2 angle step. Measure it from a demo to pick one.
""");
