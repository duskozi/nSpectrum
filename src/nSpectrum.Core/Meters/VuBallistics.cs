namespace nSpectrum.Core.Meters;

/// <summary>
/// Volume-unit meter dynamics (IEC 60268-17): the full-wave rectified signal drives a second-order
/// mechanical movement that reaches 99 % of a steady reading in 300 ms with about 1.25 % overshoot.
/// </summary>
/// <remarks>
/// The output is the averaged rectified value. A steady sine of peak A settles at 2A/π; use
/// <see cref="ToVu"/> to turn that into VU relative to a reference sine level.
/// Runs at audio rate (semi-implicit Euler, stable far beyond audio rates for ωn ≈ 15 rad/s).
/// </remarks>
public sealed class VuBallistics
{
    /// <summary>Damping ratio that gives 1.25 % overshoot (middle of the 1 to 1.5 % the standard allows).</summary>
    public static readonly double Damping = DampingForOvershoot(0.0125);

    /// <summary>Natural angular frequency (rad/s) that makes the step response first reach 99 % at 300 ms.</summary>
    public static readonly double NaturalFrequency = NormalisedRiseTime(Damping, 0.99) / 0.300;

    private readonly double _dt;
    private readonly double _k1, _k2;
    private double _y, _v;

    public VuBallistics(int sampleRate)
    {
        _dt = 1.0 / sampleRate;
        _k1 = NaturalFrequency * NaturalFrequency;
        _k2 = 2 * Damping * NaturalFrequency;
    }

    /// <summary>Current needle position (averaged rectified value, linear).</summary>
    public double Value => _y;

    public void Reset() { _y = 0; _v = 0; }

    /// <summary>Advances by one sample.</summary>
    public void Process(float sample)
    {
        double x = Math.Abs(sample);
        _v += (_k1 * (x - _y) - _k2 * _v) * _dt;
        _y += _v * _dt;
    }

    /// <summary>
    /// VU reading of a rectified value: 0 VU when a sine of peak level <paramref name="referenceDbfs"/>
    /// (AES17 dBFS, e.g. −18) is applied.
    /// </summary>
    public static double ToVu(double rectified, double referenceDbfs)
    {
        double reference = Math.Pow(10, referenceDbfs / 20) * 2 / Math.PI;
        return 20 * Math.Log10(Math.Max(rectified, 1e-12) / reference);
    }

    private static double DampingForOvershoot(double overshoot)
    {
        double l = -Math.Log(overshoot);
        return l / Math.Sqrt(Math.PI * Math.PI + l * l);
    }

    /// <summary>First time (in units of 1/ωn) the unit step response reaches <paramref name="level"/>.</summary>
    private static double NormalisedRiseTime(double zeta, double level)
    {
        double wd = Math.Sqrt(1 - zeta * zeta);
        double Step(double t) => 1 - Math.Exp(-zeta * t) * (Math.Cos(wd * t) + zeta / wd * Math.Sin(wd * t));
        // The response rises monotonically until the first peak at π/wd.
        double lo = 0, hi = Math.PI / wd;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Step(mid) < level) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
