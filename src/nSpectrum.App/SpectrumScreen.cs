using nSpectrum.App.ViewModels;
using nSpectrum.Core.Dsp;
using nSpectrum.Wpf;

namespace nSpectrum.App;

/// <summary>Lets the view model read the spectrum screen without knowing about the Direct3D view.</summary>
internal sealed class SpectrumScreen(SpectrumView view) : ISpectrumScreen
{
    public RenderStats Stats => view.Stats;
    public (ToneReading Reading, string Unit)? PeakTone => view.PeakTone;
    public DistortionReading? Distortion => view.Distortion;
    public Task<SpectrumSnapshot?> CaptureAsync() => view.CaptureAsync();
}
