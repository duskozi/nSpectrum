namespace nSpectrum.App.ViewModels;

/// <summary>One entry of an options list: the label shown (and saved) and the value it stands for.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
