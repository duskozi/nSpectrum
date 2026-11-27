namespace nSpectrum.Core.Dsp;

/// <summary>
/// LED-style display ballistics, separate from measurement averaging: instant attack and a
/// release in dB per second, advanced by the real frame time. Works on dB values.
/// </summary>
public sealed class DisplayBallistics
{
    private float[] _state = [];
    private float[] _peak = [];
    private float[] _peakHold = [];

    /// <summary>Release speed of the bars in dB/s. 0 or infinity disables ballistics (bars follow input).</summary>
    public double ReleaseDbPerSecond { get; set; } = 60;

    /// <summary>Hold time of the peak caps in seconds.</summary>
    public double PeakHoldSeconds { get; set; } = 1.0;

    /// <summary>Fall speed of the peak caps in dB/s after the hold.</summary>
    public double PeakFallDbPerSecond { get; set; } = 15;

    public ReadOnlySpan<float> Bars => _state;
    public ReadOnlySpan<float> PeakCaps => _peak;

    /// <summary>Clears state and sizes buffers. Call on the render thread when the column count changes.</summary>
    public void Reset(int count, float floorDb)
    {
        if (_state.Length != count)
        {
            _state = new float[count];
            _peak = new float[count];
            _peakHold = new float[count];
        }
        Array.Fill(_state, floorDb);
        Array.Fill(_peak, floorDb);
        Array.Clear(_peakHold);
    }

    /// <summary>Advances by <paramref name="dtSeconds"/> toward <paramref name="targetDb"/>.</summary>
    public void Update(ReadOnlySpan<float> targetDb, double dtSeconds)
    {
        if (targetDb.Length != _state.Length) throw new ArgumentException("Call Reset with the new column count first.");
        float dt = (float)Math.Clamp(dtSeconds, 0, 0.25);
        bool follow = ReleaseDbPerSecond <= 0 || double.IsPositiveInfinity(ReleaseDbPerSecond);
        float fall = (float)(ReleaseDbPerSecond * dt);
        float peakFall = (float)(PeakFallDbPerSecond * dt);
        float hold = (float)PeakHoldSeconds;
        for (int i = 0; i < _state.Length; i++)
        {
            float t = targetDb[i];
            float s = follow ? t : Math.Max(t, _state[i] - fall);
            _state[i] = s;

            if (s >= _peak[i])
            {
                _peak[i] = s;
                _peakHold[i] = hold;
            }
            else if (_peakHold[i] > 0)
            {
                _peakHold[i] -= dt;
            }
            else
            {
                _peak[i] = Math.Max(s, _peak[i] - peakFall);
            }
        }
    }
}
