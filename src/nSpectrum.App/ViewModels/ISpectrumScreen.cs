using nSpectrum.Core.Dsp;
using nSpectrum.Wpf;

namespace nSpectrum.App.ViewModels;

/// <summary>
/// What the view model reads back from the spectrum screen: render statistics, the measured
/// peak tone and distortion, and a copy of the spectrum on screen for export.
/// </summary>
public interface ISpectrumScreen
{
    RenderStats Stats { get; }
    (ToneReading Reading, string Unit)? PeakTone { get; }
    DistortionReading? Distortion { get; }
    Task<SpectrumSnapshot?> CaptureAsync();
}
