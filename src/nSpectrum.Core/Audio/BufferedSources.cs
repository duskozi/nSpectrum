using System.Diagnostics;
using nSpectrum.Core.Signals;

namespace nSpectrum.Core.Audio;

/// <summary>
/// Plays interleaved samples (e.g. a WAV file) as a real-time source on its own thread, looping.
/// Used for tests and demos without a sound card.
/// </summary>
public sealed class BufferAudioSource : IAudioSource
{
    private readonly float[] _samples;
    private Thread? _thread;
    private volatile bool _running;

    public BufferAudioSource(string name, AudioFormat format, float[] interleaved, bool loop = true)
    {
        format.Validate();
        Name = name;
        Format = format;
        _samples = interleaved;
        Loop = loop;
    }

    public static BufferAudioSource FromWav(string path, bool loop = true)
    {
        var (f, s) = WavFile.Read(path);
        return new BufferAudioSource(Path.GetFileName(path), f, s, loop);
    }

    public string Name { get; }
    public AudioFormat Format { get; }
    public bool Loop { get; }
    public bool DeliversNothingWhenSilent => false;
    public AudioDataHandler? DataAvailable { get; set; }
    public event EventHandler<Exception?>? Stopped;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "nSpectrum buffer source" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }

    private void Run()
    {
        int ch = Format.Channels;
        int chunkFrames = Format.SampleRate / 100; // 10 ms
        long start = Stopwatch.GetTimestamp();
        long sentFrames = 0;
        int pos = 0;
        int totalFrames = _samples.Length / ch;
        while (_running)
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            long due = (long)(elapsed * Format.SampleRate);
            while (sentFrames + chunkFrames <= due && _running)
            {
                int n = Math.Min(chunkFrames, totalFrames - pos);
                DataAvailable?.Invoke(_samples.AsSpan(pos * ch, n * ch));
                pos += n;
                sentFrames += n;
                if (pos >= totalFrames)
                {
                    if (!Loop) { _running = false; Stopped?.Invoke(this, null); return; }
                    pos = 0;
                }
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>A real-time source that plays a generator on every channel. Lets the app run without a device.</summary>
public sealed class GeneratorAudioSource : IAudioSource
{
    private readonly ISignalGenerator _generator;
    private readonly float[] _mono;
    private readonly float[] _interleaved;
    private Thread? _thread;
    private volatile bool _running;

    public GeneratorAudioSource(string name, ISignalGenerator generator, int channels = 2)
    {
        Name = name;
        _generator = generator;
        Format = new AudioFormat(generator.SampleRate, channels);
        Format.Validate();
        int chunk = generator.SampleRate / 100;
        _mono = new float[chunk];
        _interleaved = new float[chunk * channels];
    }

    public string Name { get; }
    public AudioFormat Format { get; }
    public bool DeliversNothingWhenSilent => false;
    public AudioDataHandler? DataAvailable { get; set; }
    public event EventHandler<Exception?>? Stopped { add { } remove { } }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "nSpectrum generator source" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
        // Stopped is for stopping by itself (see IAudioSource); a generator never does.
    }

    private void Run()
    {
        int ch = Format.Channels;
        long start = Stopwatch.GetTimestamp();
        long sent = 0;
        while (_running)
        {
            long due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * Format.SampleRate);
            while (sent + _mono.Length <= due && _running)
            {
                _generator.Generate(_mono);
                for (int f = 0, i = 0; f < _mono.Length; f++)
                    for (int c = 0; c < ch; c++) _interleaved[i++] = _mono[f];
                DataAvailable?.Invoke(_interleaved);
                sent += _mono.Length;
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose() => Stop();
}
