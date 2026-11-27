namespace nSpectrum.Core.Dsp;

public enum FrequencyAxisKind { Logarithmic, Linear }

/// <summary>How many bins landing in one display column are combined.</summary>
public enum ColumnReduction
{
    /// <summary>Largest bin (peak view). Keeps tones at their level however many bins share a column.</summary>
    Max,
    /// <summary>Sum of bin powers (energy view). Matches a band level when columns are wide.</summary>
    PowerSum,
}

/// <summary>
/// Precomputed mapping from FFT bins to display columns. Rebuilt only when the column count,
/// frequency range or FFT changes; <see cref="Map"/> itself allocates nothing.
/// </summary>
/// <remarks>
/// Where several bins fall in one column they are reduced by max or power sum, never sampled.
/// Where one bin spans several columns (the low end of a log axis), each column shows the bin
/// whose span contains the column centre: true bin steps, no invented detail.
/// </remarks>
public sealed class FrequencyAxis
{
    private readonly int[] _first;  // first bin of each column
    private readonly int[] _last;   // last bin (inclusive)

    public FrequencyAxis(int columns, double minHz, double maxHz, FrequencyAxisKind kind, double binWidth, int binCount)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        if (!(minHz >= 0) || !(maxHz > minHz)) throw new ArgumentOutOfRangeException(nameof(maxHz));
        if (kind == FrequencyAxisKind.Logarithmic && !(minHz > 0)) throw new ArgumentOutOfRangeException(nameof(minHz));

        Columns = columns;
        MinHz = minHz;
        MaxHz = maxHz;
        Kind = kind;
        BinWidth = binWidth;
        BinCount = binCount;
        _first = new int[columns];
        _last = new int[columns];

        for (int c = 0; c < columns; c++)
        {
            double f0 = FrequencyAt((double)c / columns);
            double f1 = FrequencyAt((double)(c + 1) / columns);
            // A bin belongs to the column that contains its centre frequency k·bw.
            int b0 = (int)Math.Ceiling(f0 / binWidth);
            int b1 = (int)Math.Ceiling(f1 / binWidth) - 1;
            if (b1 < b0)
            {
                double fc = FrequencyAt((c + 0.5) / columns);
                b0 = b1 = (int)Math.Round(fc / binWidth);
            }
            _first[c] = Math.Clamp(b0, 0, binCount - 1);
            _last[c] = Math.Clamp(b1, 0, binCount - 1);
        }
    }

    public int Columns { get; }
    public double MinHz { get; }
    public double MaxHz { get; }
    public FrequencyAxisKind Kind { get; }
    public double BinWidth { get; }
    public int BinCount { get; }

    /// <summary>Frequency at normalised position x in [0, 1].</summary>
    public double FrequencyAt(double x) => Kind == FrequencyAxisKind.Logarithmic
        ? MinHz * Math.Pow(MaxHz / MinHz, x)
        : MinHz + (MaxHz - MinHz) * x;

    /// <summary>Normalised position in [0, 1] of <paramref name="hz"/>.</summary>
    public double PositionOf(double hz) => Kind == FrequencyAxisKind.Logarithmic
        ? Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz)
        : (hz - MinHz) / (MaxHz - MinHz);

    public int FirstBin(int column) => _first[column];
    public int LastBin(int column) => _last[column];

    /// <summary>Reduces linear bin values into <paramref name="columns"/> (linear).</summary>
    public void Map(ReadOnlySpan<float> bins, Span<float> columns, ColumnReduction reduction)
    {
        if (bins.Length < BinCount) throw new ArgumentException("Bin count mismatch.", nameof(bins));
        if (columns.Length < Columns) throw new ArgumentException("Column count mismatch.", nameof(columns));
        for (int c = 0; c < Columns; c++)
        {
            int a = _first[c], b = _last[c];
            float acc = bins[a];
            if (reduction == ColumnReduction.Max)
            {
                for (int k = a + 1; k <= b; k++) if (bins[k] > acc) acc = bins[k];
            }
            else
            {
                for (int k = a + 1; k <= b; k++) acc += bins[k];
            }
            columns[c] = acc;
        }
    }

    private static readonly double[] GridMultiples = [1, 2, 5];

    /// <summary>ISO-style grid frequencies (1, 2, 5 per decade) inside the range.</summary>
    public static IEnumerable<double> GridFrequencies(double minHz, double maxHz)
    {
        for (double dec = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(minHz, 1)))); dec <= maxHz; dec *= 10)
        {
            foreach (double m in GridMultiples)
            {
                double f = dec * m;
                if (f >= minHz && f <= maxHz) yield return f;
            }
        }
    }

    /// <summary>Compact label: 20, 200, 2k, 20k, 1k5.</summary>
    public static string Label(double hz)
    {
        if (hz >= 1000)
        {
            // Round to tenths of a kHz first, so 1950 Hz reads "2k" and never "1k10".
            long tenths = (long)Math.Round(hz / 100);
            return tenths % 10 == 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{tenths / 10}k")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{tenths / 10}k{tenths % 10}");
        }
        return hz.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
