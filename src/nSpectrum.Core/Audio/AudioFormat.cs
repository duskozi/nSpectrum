namespace nSpectrum.Core.Audio;

/// <summary>Format of the 32-bit float, interleaved stream a source delivers.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public const int MaxChannels = 8;

    public void Validate()
    {
        if (SampleRate < 8000 || SampleRate > 768_000) throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, null);
        if (Channels < 1 || Channels > MaxChannels) throw new ArgumentOutOfRangeException(nameof(Channels), Channels, $"1 to {MaxChannels} channels are supported.");
    }

    public override string ToString() => $"{SampleRate / 1000.0:0.###} kHz, {Channels} ch";
}
