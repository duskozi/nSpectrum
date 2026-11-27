using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace nSpectrum.Core.Dsp;

/// <summary>
/// Allocation-free real-input FFT for power-of-two sizes.
/// </summary>
/// <remarks>
/// The N real samples are packed into an N/2-point complex sequence z[m] = x[2m] + i·x[2m+1],
/// transformed with an iterative radix-2 decimation-in-time FFT (split real/imaginary arrays,
/// SIMD butterflies via <see cref="Vector{T}"/> once a stage is wide enough), and unpacked into
/// the N/2+1 non-negative-frequency bins of the real spectrum. All tables are built in the
/// constructor; <see cref="Forward"/> and <see cref="ForwardPower"/> never allocate.
/// An instance is not thread-safe: use one per DSP chain.
/// </remarks>
public sealed class RealFft
{
    public const int MinSize = 16;
    public const int MaxSize = 1 << 20;

    private readonly int _n;      // real size
    private readonly int _m;      // complex size = n / 2
    private readonly int[] _bitReverse;   // length m
    private readonly float[] _twRe;       // stage twiddles: stage with half-size h lives at [h, 2h)
    private readonly float[] _twIm;
    private readonly float[] _postRe;     // e^{-i 2π k / n}, k = 0..m
    private readonly float[] _postIm;
    private readonly float[] _re;         // work buffers, length m
    private readonly float[] _im;

    public RealFft(int size)
    {
        if (size < MinSize || size > MaxSize || !BitOperations.IsPow2(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, $"FFT size must be a power of two in [{MinSize}, {MaxSize}].");

        _n = size;
        _m = size / 2;
        int log2M = BitOperations.Log2((uint)_m);

        _bitReverse = new int[_m];
        for (int i = 0; i < _m; i++)
            _bitReverse[i] = (int)(ReverseBits((uint)i) >> (32 - log2M));

        _twRe = new float[_m];
        _twIm = new float[_m];
        for (int h = 1; h < _m; h <<= 1)
        {
            for (int j = 0; j < h; j++)
            {
                double a = -Math.PI * j / h;
                _twRe[h + j] = (float)Math.Cos(a);
                _twIm[h + j] = (float)Math.Sin(a);
            }
        }

        _postRe = new float[_m + 1];
        _postIm = new float[_m + 1];
        for (int k = 0; k <= _m; k++)
        {
            double a = -2.0 * Math.PI * k / _n;
            _postRe[k] = (float)Math.Cos(a);
            _postIm[k] = (float)Math.Sin(a);
        }

        _re = new float[_m];
        _im = new float[_m];
    }

    /// <summary>Real transform length N.</summary>
    public int Size => _n;

    /// <summary>Number of output bins, N/2 + 1 (DC to Nyquist).</summary>
    public int BinCount => _m + 1;

    /// <summary>
    /// Computes X[k] = Σ x[n]·e^{-i2πkn/N} for k = 0..N/2. Optionally multiplies the input by
    /// <paramref name="window"/> on the fly (pass an empty span for none).
    /// </summary>
    public void Forward(ReadOnlySpan<float> input, ReadOnlySpan<float> window, Span<float> outRe, Span<float> outIm)
    {
        if (outRe.Length < BinCount || outIm.Length < BinCount)
            throw new ArgumentException("Output spans must hold N/2 + 1 bins.");
        Transform(input, window);

        float[] re = _re, im = _im;
        outRe[0] = re[0] + im[0];
        outIm[0] = 0f;
        outRe[_m] = re[0] - im[0];
        outIm[_m] = 0f;
        for (int k = 1; k < _m; k++)
        {
            Unpack(re, im, k, out float xr, out float xi);
            outRe[k] = xr;
            outIm[k] = xi;
        }
    }

    /// <summary>
    /// Computes |X[k]|² for k = 0..N/2 without storing the complex spectrum.
    /// </summary>
    public void ForwardPower(ReadOnlySpan<float> input, ReadOnlySpan<float> window, Span<float> power)
    {
        if (power.Length < BinCount)
            throw new ArgumentException("Output span must hold N/2 + 1 bins.", nameof(power));
        Transform(input, window);

        float[] re = _re, im = _im;
        float dc = re[0] + im[0];
        float ny = re[0] - im[0];
        power[0] = dc * dc;
        power[_m] = ny * ny;
        for (int k = 1; k < _m; k++)
        {
            Unpack(re, im, k, out float xr, out float xi);
            power[k] = xr * xr + xi * xi;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Unpack(float[] re, float[] im, int k, out float xr, out float xi)
    {
        // Z = FFT of packed sequence. E = (Z[k] + conj Z[m-k]) / 2, O = (Z[k] - conj Z[m-k]) / (2i)
        // X[k] = E + W^k · O with W = e^{-i2π/N}.
        int j = _m - k;
        float ar = re[k], ai = im[k];
        float br = re[j], bi = -im[j];          // conj Z[m-k]
        float er = 0.5f * (ar + br), ei = 0.5f * (ai + bi);
        float dr = ar - br, di = ai - bi;       // Z[k] - conj Z[m-k]
        float or_ = 0.5f * di, oi = -0.5f * dr; // divide by 2i
        float wr = _postRe[k], wi = _postIm[k];
        xr = er + (wr * or_ - wi * oi);
        xi = ei + (wr * oi + wi * or_);
    }

    private void Transform(ReadOnlySpan<float> input, ReadOnlySpan<float> window)
    {
        if (input.Length < _n)
            throw new ArgumentException($"Input must hold {_n} samples.", nameof(input));
        if (!window.IsEmpty && window.Length < _n)
            throw new ArgumentException($"Window must hold {_n} samples.", nameof(window));

        float[] re = _re, im = _im;
        int[] rev = _bitReverse;

        // Pack, window and bit-reverse in a single pass.
        if (window.IsEmpty)
        {
            for (int i = 0; i < _m; i++)
            {
                int d = rev[i];
                re[d] = input[2 * i];
                im[d] = input[2 * i + 1];
            }
        }
        else
        {
            for (int i = 0; i < _m; i++)
            {
                int d = rev[i];
                re[d] = input[2 * i] * window[2 * i];
                im[d] = input[2 * i + 1] * window[2 * i + 1];
            }
        }

        int m = _m;

        // Stages 1 and 2 fused as radix-4 (h = 1, 2) when m >= 4.
        if (m >= 4)
        {
            for (int b = 0; b < m; b += 4)
            {
                float r0 = re[b], i0 = im[b], r1 = re[b + 1], i1 = im[b + 1];
                float r2 = re[b + 2], i2 = im[b + 2], r3 = re[b + 3], i3 = im[b + 3];
                // stage h=1
                float a0r = r0 + r1, a0i = i0 + i1, a1r = r0 - r1, a1i = i0 - i1;
                float a2r = r2 + r3, a2i = i2 + i3, a3r = r2 - r3, a3i = i2 - i3;
                // stage h=2: twiddles 1 and -i
                re[b] = a0r + a2r; im[b] = a0i + a2i;
                re[b + 2] = a0r - a2r; im[b + 2] = a0i - a2i;
                // a3 * (-i) = (a3i, -a3r)
                re[b + 1] = a1r + a3i; im[b + 1] = a1i - a3r;
                re[b + 3] = a1r - a3i; im[b + 3] = a1i + a3r;
            }
        }
        else if (m == 2)
        {
            float r0 = re[0], i0 = im[0];
            re[0] = r0 + re[1]; im[0] = i0 + im[1];
            re[1] = r0 - re[1]; im[1] = i0 - im[1];
        }

        int vc = Vector<float>.Count;
        bool simd = Vector.IsHardwareAccelerated;
        for (int h = 4; h < m; h <<= 1)
        {
            int span = h << 1;
            if (simd && h >= vc)
            {
                Span<Vector<float>> vRe = MemoryMarshal.Cast<float, Vector<float>>(re.AsSpan());
                Span<Vector<float>> vIm = MemoryMarshal.Cast<float, Vector<float>>(im.AsSpan());
                ReadOnlySpan<Vector<float>> vTwRe = MemoryMarshal.Cast<float, Vector<float>>(_twRe.AsSpan(h, h));
                ReadOnlySpan<Vector<float>> vTwIm = MemoryMarshal.Cast<float, Vector<float>>(_twIm.AsSpan(h, h));
                int hv = h / vc;
                for (int b = 0; b < m; b += span)
                {
                    int top = b / vc, bot = (b + h) / vc;
                    for (int j = 0; j < hv; j++)
                    {
                        Vector<float> wr = vTwRe[j], wi = vTwIm[j];
                        Vector<float> xr = vRe[bot + j], xi = vIm[bot + j];
                        Vector<float> tr = wr * xr - wi * xi;
                        Vector<float> ti = wr * xi + wi * xr;
                        Vector<float> ur = vRe[top + j], ui = vIm[top + j];
                        vRe[top + j] = ur + tr; vIm[top + j] = ui + ti;
                        vRe[bot + j] = ur - tr; vIm[bot + j] = ui - ti;
                    }
                }
            }
            else
            {
                for (int b = 0; b < m; b += span)
                {
                    for (int j = 0; j < h; j++)
                    {
                        float wr = _twRe[h + j], wi = _twIm[h + j];
                        int t = b + j, u = t + h;
                        float xr = re[u], xi = im[u];
                        float tr = wr * xr - wi * xi;
                        float ti = wr * xi + wi * xr;
                        float ur = re[t], ui = im[t];
                        re[t] = ur + tr; im[t] = ui + ti;
                        re[u] = ur - tr; im[u] = ui - ti;
                    }
                }
            }
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }
}
