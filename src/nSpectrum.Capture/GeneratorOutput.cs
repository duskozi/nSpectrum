using NAudio.CoreAudioApi;
using NAudio.Wave;
using nSpectrum.Core.Signals;

namespace nSpectrum.Capture;

public enum GeneratorSignal { Sine, LogSweep, WhiteNoise, PinkNoise }

/// <summary>Plays a test signal to a render endpoint through WASAPI shared mode, at the device's mix rate.</summary>
public sealed class GeneratorOutput : IDisposable
{
    private readonly WasapiOut _out;
    private readonly MMDevice _device;
    private readonly Provider _provider;

    public GeneratorOutput(AudioDevices devices, string renderDeviceId, GeneratorSignal signal, double frequency, double levelDbfs)
    {
        var (fs, channels) = devices.MixFormat(renderDeviceId);
        if (!(frequency > 0)) frequency = 1000;
        frequency = Math.Min(frequency, fs / 2.0 * 0.999);
        ISignalGenerator gen = signal switch
        {
            GeneratorSignal.Sine => new SineGenerator(fs, frequency, Level.SinePeak(levelDbfs)),
            GeneratorSignal.LogSweep => new LogSweepGenerator(fs, 20, Math.Min(20000, fs / 2.0), 10, Level.SinePeak(levelDbfs)),
            GeneratorSignal.WhiteNoise => new WhiteNoiseGenerator(fs, Level.Rms(levelDbfs)),
            GeneratorSignal.PinkNoise => new PinkNoiseGenerator(fs, Level.Rms(levelDbfs)),
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };
        _provider = new Provider(gen, channels);
        _device = devices.Get(renderDeviceId);
        try
        {
            _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, 50);
            _out.Init(_provider);
        }
        catch
        {
            _out?.Dispose();
            _device.Dispose();
            throw;
        }
    }

    public void Start() => _out.Play();

    public void Stop() => _out.Stop();

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
        _device.Dispose();
    }

    private sealed class Provider(ISignalGenerator generator, int channels) : ISampleProvider
    {
        private float[] _mono = new float[4096];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(generator.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            generator.Generate(_mono.AsSpan(0, frames));
            for (int f = 0, i = offset; f < frames; f++)
                for (int c = 0; c < channels; c++) buffer[i++] = _mono[f];
            return frames * channels;
        }
    }
}
