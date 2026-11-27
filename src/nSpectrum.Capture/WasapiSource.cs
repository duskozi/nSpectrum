using NAudio.CoreAudioApi;
using NAudio.Wave;
using nSpectrum.Core.Audio;

namespace nSpectrum.Capture;

/// <summary>
/// WASAPI capture (shared mode) or loopback as an <see cref="IAudioSource"/>. Converts whatever
/// the endpoint delivers to interleaved 32-bit float at the capture boundary, at the device's
/// own mix rate (never assumes 48 kHz).
/// </summary>
public sealed class WasapiSource : IAudioSource
{
    private readonly WasapiCapture _capture;
    private readonly WaveFormat _waveFormat;
    private float[] _convert = new float[4096];
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;

    private readonly MMDevice _device;

    private WasapiSource(AudioDeviceInfo info, WasapiCapture capture, MMDevice device)
    {
        _device = device;
        Info = info;
        _capture = capture;
        _waveFormat = capture.WaveFormat;
        Format = new AudioFormat(_waveFormat.SampleRate, Math.Min(_waveFormat.Channels, AudioFormat.MaxChannels));
        _bytesPerSample = _waveFormat.BitsPerSample / 8;
        _isFloat = _waveFormat.Encoding == WaveFormatEncoding.IeeeFloat
            || (_waveFormat is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
    }

    /// <summary>Opens a capture or loopback endpoint in shared mode.</summary>
    public static WasapiSource Open(AudioDevices devices, AudioDeviceInfo info)
    {
        MMDevice device = devices.Get(info.Id);
        try
        {
            WasapiCapture capture = info.Kind == DeviceKind.Loopback
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20) { ShareMode = AudioClientShareMode.Shared };
            return new WasapiSource(info, capture, device);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public AudioDeviceInfo Info { get; }
    public string Name => Info.DisplayName;
    public AudioFormat Format { get; }
    public bool DeliversNothingWhenSilent => Info.Kind == DeviceKind.Loopback;
    public AudioDataHandler? DataAvailable { get; set; }
    public event EventHandler<Exception?>? Stopped;

    /// <summary>Channels the device actually has (may exceed the 8 analysed).</summary>
    public int DeviceChannels => _waveFormat.Channels;

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int devCh = _waveFormat.Channels;
        int frames = e.BytesRecorded / (_bytesPerSample * devCh);
        if (frames == 0) return;
        int ch = Format.Channels;
        int needed = frames * ch;
        if (_convert.Length < needed) _convert = new float[needed * 2]; // grows once, then stable

        ReadOnlySpan<byte> src = e.Buffer.AsSpan(0, e.BytesRecorded);
        Span<float> dst = _convert.AsSpan(0, needed);

        if (_isFloat && _bytesPerSample == 4 && devCh == ch)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src)[..needed].CopyTo(dst);
        }
        else
        {
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < ch; c++)
                {
                    int o = (f * devCh + c) * _bytesPerSample;
                    dst[f * ch + c] = ReadSample(src.Slice(o, _bytesPerSample));
                }
            }
        }
        DataAvailable?.Invoke(dst);
    }

    private float ReadSample(ReadOnlySpan<byte> b)
    {
        if (_isFloat)
            return _bytesPerSample == 4 ? BitConverter.ToSingle(b) : (float)BitConverter.ToDouble(b);
        return _bytesPerSample switch
        {
            1 => (b[0] - 128) / 128f, // 8-bit PCM is unsigned
            2 => BitConverter.ToInt16(b) / 32768f,
            3 => (b[0] | (b[1] << 8) | ((sbyte)b[2] << 16)) / 8388608f,
            4 => (float)(BitConverter.ToInt32(b) / 2147483648.0),
            _ => 0f,
        };
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _device.Dispose();
    }
}
