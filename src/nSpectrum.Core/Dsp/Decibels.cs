using System.Numerics.Tensors;

namespace nSpectrum.Core.Dsp;

/// <summary>Conversions between linear power-like values and decibels.</summary>
public static class Decibels
{
    /// <summary>Smallest linear value converted; anything below reads as this (-300 dB).</summary>
    public const float FloorLinear = 1e-30f;

    public static double FromPower(double value) => 10 * Math.Log10(Math.Max(value, FloorLinear));

    public static double ToPower(double db) => Math.Pow(10, db / 10);

    /// <summary>
    /// dB = 10·log10(max(value, floor)), vectorised. <paramref name="destination"/> may alias
    /// <paramref name="values"/>.
    /// </summary>
    public static void FromPower(ReadOnlySpan<float> values, Span<float> destination)
    {
        TensorPrimitives.Max(values, FloorLinear, destination);
        TensorPrimitives.Log10(destination, destination);
        TensorPrimitives.Multiply(destination, 10f, destination);
    }
}
