using nSpectrum.Capture;
using nSpectrum.Core.Audio;

namespace nSpectrum.App.ViewModels;

/// <summary>An entry of the SOURCE list: a Windows audio device, or one of the internal test signals.</summary>
public sealed record SourceItem(string Label, AudioDeviceInfo? Device, Func<IAudioSource>? Internal)
{
    public override string ToString() => Label;
}
