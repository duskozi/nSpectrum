namespace nSpectrum.Core.Audio;

/// <summary>Receives interleaved 32-bit float samples on the source's capture thread.</summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> interleaved);

/// <summary>
/// A stream of interleaved float samples: a capture device, loopback, a file or a generator.
/// The data callback runs on the source's own thread and must return quickly; the pipeline only
/// copies samples into a lock-free ring buffer there.
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    AudioFormat Format { get; }

    /// <summary>
    /// True when the source goes quiet by delivering nothing (WASAPI loopback on a silent endpoint),
    /// so the pipeline must synthesise silence to keep the display falling.
    /// </summary>
    bool DeliversNothingWhenSilent { get; }

    /// <summary>Set before <see cref="Start"/>. Called on the capture thread.</summary>
    AudioDataHandler? DataAvailable { get; set; }

    /// <summary>Raised when the source stops by itself (device removed, end of file) or after an error.</summary>
    event EventHandler<Exception?>? Stopped;

    void Start();

    void Stop();
}
