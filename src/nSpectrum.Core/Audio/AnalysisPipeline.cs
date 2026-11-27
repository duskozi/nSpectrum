using System.Diagnostics;
using nSpectrum.Core.Concurrency;
using nSpectrum.Core.Dsp;

namespace nSpectrum.Core.Audio;

/// <summary>
/// Something that wants the raw interleaved samples of a pipeline on its DSP worker thread, e.g. a
/// level meter, a phase meter or a second spectrum display with its own analyzers.
/// </summary>
/// <remarks>Called on the worker thread for every block, including synthesised silence. Must not block or allocate.</remarks>
public interface IAudioTap
{
    /// <summary>Interleaved samples of one block, <paramref name="channels"/> per frame.</summary>
    void Process(ReadOnlySpan<float> interleaved, int channels);
}

/// <summary>One analyzer of a pipeline: which signal, analysed how.</summary>
public sealed record AnalyzerSpec(ChannelRoute Route, SpectrumSettings Settings);

/// <summary>
/// Runs the DSP for one source: a lock-free ring buffer filled by the capture thread, and a
/// worker thread that routes channels into one <see cref="SpectrumAnalyzer"/> per spec.
/// </summary>
/// <remarks>
/// <para>Producer side (<see cref="Write"/>) is called on the capture thread and only copies.</para>
/// <para>Configuration changes (<see cref="Configure"/>) build new analyzers on the caller's
/// thread and swap them in atomically; the worker never allocates.</para>
/// <para>For tests and files, <see cref="ProcessDirect"/> runs the same routing and analyzers
/// synchronously without a thread.</para>
/// </remarks>
public sealed class AnalysisPipeline : IDisposable
{
    /// <summary>Frames read from the ring per worker iteration.</summary>
    public const int BlockFrames = 512;

    private readonly SpscRingBuffer<float> _ring;
    private readonly AutoResetEvent _dataReady = new(false);
    private readonly float[] _block;
    private readonly float[] _mono;
    private volatile Stage _stage;
    private volatile IAudioTap[] _taps = [];
    private readonly Lock _tapLock = new();
    private Thread? _worker;
    private volatile bool _running;
    private long _dspTicks;
    private long _lastDataTimestamp;

    private sealed record Stage(AnalyzerSpec[] Specs, SpectrumAnalyzer[] Analyzers);

    public AnalysisPipeline(AudioFormat format, IEnumerable<AnalyzerSpec> specs, double bufferSeconds = 1.0)
    {
        format.Validate();
        Format = format;
        _ring = new SpscRingBuffer<float>((int)(format.SampleRate * bufferSeconds) * format.Channels);
        _block = new float[BlockFrames * format.Channels];
        _mono = new float[BlockFrames];
        _stage = Build(specs);
    }

    public AudioFormat Format { get; }

    /// <summary>When true and no data arrives for a while, the worker feeds silence (loopback sources).</summary>
    public bool SynthesizeSilence { get; set; }

    /// <summary>Gap without data after which silence is synthesised.</summary>
    public TimeSpan SilenceAfter { get; set; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Runs on the worker thread once at start, e.g. to join MMCSS "Pro Audio" on Windows.</summary>
    public Action? WorkerStarted { get; set; }

    /// <summary>Raised (on the configuring thread) after <see cref="Configure"/> swapped analyzers.</summary>
    public event EventHandler? Reconfigured;

    /// <summary>
    /// Raised on the worker thread when an analyzer or tap threw. The worker stops (the app keeps
    /// running) instead of taking the whole process down with an unhandled thread exception.
    /// </summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>The exception that stopped the worker, or null.</summary>
    public Exception? Fault { get; private set; }

    public IReadOnlyList<SpectrumAnalyzer> Analyzers => _stage.Analyzers;

    public IReadOnlyList<AnalyzerSpec> Specs => _stage.Specs;

    /// <summary>Samples dropped because the worker fell behind.</summary>
    public long Overruns => _ring.Overflowed;

    /// <summary>Total CPU-side time spent in DSP so far.</summary>
    public TimeSpan DspTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _dspTicks));

    /// <summary>Replaces the analyzers. Allocates on the calling thread, swaps atomically.</summary>
    public void Configure(IEnumerable<AnalyzerSpec> specs)
    {
        _stage = Build(specs);
        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a tap. Thread-safe; takes effect from the next block.</summary>
    public void AddTap(IAudioTap tap)
    {
        lock (_tapLock)
        {
            if (Array.IndexOf(_taps, tap) >= 0) return;
            _taps = [.. _taps, tap];
        }
    }

    /// <summary>Removes a tap. Thread-safe; the worker may still finish the block in progress.</summary>
    public void RemoveTap(IAudioTap tap)
    {
        lock (_tapLock) _taps = [.. _taps.Where(t => t != tap)];
    }

    public IReadOnlyList<IAudioTap> Taps => _taps;

    private Stage Build(IEnumerable<AnalyzerSpec> specs)
    {
        AnalyzerSpec[] list = [.. specs];
        var analyzers = new SpectrumAnalyzer[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            if (!list[i].Route.IsValidFor(Format.Channels))
                throw new ArgumentException($"Route {list[i].Route} is not valid for {Format.Channels} channel(s).");
            analyzers[i] = new SpectrumAnalyzer(list[i].Settings, Format.SampleRate);
        }
        return new Stage(list, analyzers);
    }

    /// <summary>Producer side (capture thread): copies interleaved samples into the ring and wakes the worker.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        // Only whole frames go in, so the channel interleave can never slip.
        int ch = Format.Channels;
        int free = _ring.FreeCount;
        int n = Math.Min(interleaved.Length - interleaved.Length % ch, free - free % ch);
        if (n < interleaved.Length) _ring.ReportDropped(interleaved.Length - n);
        if (n > 0) _ring.Write(interleaved[..n]);
        Volatile.Write(ref _lastDataTimestamp, Stopwatch.GetTimestamp());
        _dataReady.Set();
    }

    /// <summary>Runs routing and analysis synchronously on the calling thread (tests, files).</summary>
    public void ProcessDirect(ReadOnlySpan<float> interleaved)
    {
        int ch = Format.Channels;
        int step = BlockFrames * ch;
        for (int off = 0; off < interleaved.Length; off += step)
        {
            int len = Math.Min(step, interleaved.Length - off);
            len -= len % ch;
            ProcessBlock(interleaved.Slice(off, len));
        }
    }

    private void ProcessBlock(ReadOnlySpan<float> block)
    {
        long t0 = Stopwatch.GetTimestamp();
        Stage stage = _stage;
        int frames = block.Length / Format.Channels;
        Span<float> mono = _mono.AsSpan(0, frames);
        for (int i = 0; i < stage.Analyzers.Length; i++)
        {
            stage.Specs[i].Route.Extract(block, Format.Channels, mono);
            stage.Analyzers[i].Process(mono);
        }
        IAudioTap[] taps = _taps;
        for (int i = 0; i < taps.Length; i++) taps[i].Process(block, Format.Channels);
        Interlocked.Add(ref _dspTicks, Stopwatch.GetTimestamp() - t0);
    }

    public void Start()
    {
        if (_running) return;
        if (_worker is not null) throw new InvalidOperationException("The previous worker has not stopped yet.");
        Fault = null;
        _running = true;
        Volatile.Write(ref _lastDataTimestamp, Stopwatch.GetTimestamp());
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "nSpectrum DSP", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    public void Stop()
    {
        if (_worker is null) return;
        _running = false;
        _dataReady.Set();
        // The worker only waits in 10 ms slices, so it ends promptly; keep the thread (and the
        // event it waits on) until it has, so Dispose never pulls the event out from under it.
        if (_worker.Join(2000)) _worker = null;
    }

    private void WorkerLoop()
    {
        try
        {
            RunWorker();
        }
        catch (Exception ex)
        {
            _running = false;
            Fault = ex;
            Faulted?.Invoke(this, ex);
        }
    }

    private void RunWorker()
    {
        try { WorkerStarted?.Invoke(); } catch { /* priority boost is optional */ }
        int ch = Format.Channels;
        long silenceCredit = 0; // frames of silence owed
        long lastSilenceCheck = Stopwatch.GetTimestamp();
        bool inSilence = false;
        while (_running)
        {
            int available = _ring.Count;
            if (available < ch)
            {
                _dataReady.WaitOne(10);
                if (SynthesizeSilence)
                {
                    long now = Stopwatch.GetTimestamp();
                    long last = Volatile.Read(ref _lastDataTimestamp);
                    if (Stopwatch.GetElapsedTime(last, now) > SilenceAfter && _ring.Count == 0)
                    {
                        // On entering silence, owe the whole gap since the last real data (not just
                        // the last poll), so the stream clock does not lose SilenceAfter each time.
                        long from = inSilence ? lastSilenceCheck : last;
                        inSilence = true;
                        silenceCredit += (long)(Stopwatch.GetElapsedTime(from, now).TotalSeconds * Format.SampleRate);
                        silenceCredit = Math.Min(silenceCredit, Format.SampleRate); // never more than 1 s at once
                        while (silenceCredit > 0)
                        {
                            int n = (int)Math.Min(silenceCredit, BlockFrames);
                            Span<float> z = _block.AsSpan(0, n * ch);
                            z.Clear();
                            ProcessBlock(z);
                            silenceCredit -= n;
                        }
                    }
                    else
                    {
                        silenceCredit = 0;
                        inSilence = false;
                    }
                    lastSilenceCheck = now;
                }
                continue;
            }

            int toRead = Math.Min(available - available % ch, _block.Length);
            int got = _ring.Read(_block.AsSpan(0, toRead));
            if (got > 0) ProcessBlock(_block.AsSpan(0, got - got % ch));
            lastSilenceCheck = Stopwatch.GetTimestamp();
            inSilence = false;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_worker is null) _dataReady.Dispose(); // else leave it to the finalizer: the worker is still using it
    }
}
