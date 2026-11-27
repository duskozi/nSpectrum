using System.Globalization;

namespace nSpectrum.Core.Dsp;

/// <summary>
/// An owned copy of one spectrum frame, safe to keep, export or compare after the triple buffer
/// has moved on.
/// </summary>
public sealed class SpectrumSnapshot
{
    public SpectrumSnapshot(SpectrumInfo info, float[] values, float[] peaks, double time, int averagedCount = 1)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (values.Length != info.BinCount) throw new ArgumentException("Bin count mismatch.", nameof(values));
        Info = info;
        Values = values;
        Peaks = peaks;
        Time = time;
        AveragedCount = averagedCount;
    }

    /// <summary>Copies a frame (call on the thread that owns the frame, i.e. the reader of its triple buffer).</summary>
    public static SpectrumSnapshot From(SpectrumFrame frame) =>
        new(frame.Info, (float[])frame.Values.Clone(), (float[])frame.Peaks.Clone(), frame.Time, frame.AveragedCount);

    public SpectrumInfo Info { get; }
    public float[] Values { get; }
    public float[] Peaks { get; }
    public double Time { get; }
    public int AveragedCount { get; }

    /// <summary>
    /// Writes the snapshot as CSV: comment lines with the analysis settings, then
    /// <c>frequency_hz,level_db[,peak_db]</c> per bin from <paramref name="minHz"/> to <paramref name="maxHz"/>.
    /// Invariant culture (dot decimals), so spreadsheets and scripts read it the same everywhere.
    /// </summary>
    public void WriteCsv(TextWriter w, double minHz = 0, double maxHz = double.MaxValue)
    {
        var ci = CultureInfo.InvariantCulture;
        var s = Info.Settings;
        bool peaks = s.PeakHold;
        w.WriteLine("# nSpectrum spectrum export");
        w.WriteLine(string.Create(ci, $"# sample_rate={Info.SampleRate} fft_size={Info.FftSize} window={s.Window} bin_width_hz={Info.BinWidth:0.######} enbw_hz={Info.EnbwHz:0.######}"));
        w.WriteLine(string.Create(ci, $"# reading={s.Scale} unit={Info.UnitLabel} weighting={s.Weighting} averaging={s.Averaging} averaged_spectra={AveragedCount} stream_time_s={Time:0.###}"));
        w.WriteLine(peaks ? "frequency_hz,level_db,peak_db" : "frequency_hz,level_db");
        for (int k = 0; k < Info.BinCount; k++)
        {
            double f = Info.BinFrequency(k);
            if (f < minHz || f > maxHz) continue;
            double level = Decibels.FromPower(Values[k]);
            if (peaks)
                w.WriteLine(string.Create(ci, $"{f:0.###},{level:0.###},{Decibels.FromPower(Peaks[k]):0.###}"));
            else
                w.WriteLine(string.Create(ci, $"{f:0.###},{level:0.###}"));
        }
    }
}
