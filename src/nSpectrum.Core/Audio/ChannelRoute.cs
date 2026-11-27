namespace nSpectrum.Core.Audio;

public enum ChannelRouteKind
{
    /// <summary>One channel of the source, by index (0 = left, 1 = right).</summary>
    Channel,
    /// <summary>M = (L + R) / 2.</summary>
    Mid,
    /// <summary>S = (L - R) / 2.</summary>
    Side,
}

/// <summary>Which signal of a multichannel source an analyzer looks at.</summary>
public readonly record struct ChannelRoute(ChannelRouteKind Kind, int Index = 0)
{
    public static ChannelRoute Left => new(ChannelRouteKind.Channel, 0);
    public static ChannelRoute Right => new(ChannelRouteKind.Channel, 1);
    public static ChannelRoute Mid => new(ChannelRouteKind.Mid);
    public static ChannelRoute Side => new(ChannelRouteKind.Side);
    public static ChannelRoute Channel(int index) => new(ChannelRouteKind.Channel, index);

    public bool IsValidFor(int channels) => Kind switch
    {
        ChannelRouteKind.Channel => Index >= 0 && Index < channels,
        _ => channels >= 1,
    };

    /// <summary>
    /// Extracts the routed mono signal from interleaved <paramref name="interleaved"/> into
    /// <paramref name="mono"/> (length = frames). For a mono source, Mid is the channel and Side is silence.
    /// </summary>
    public void Extract(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        int frames = interleaved.Length / channels;
        if (mono.Length < frames) throw new ArgumentException("Destination too small.", nameof(mono));
        switch (Kind)
        {
            case ChannelRouteKind.Channel:
                if (channels == 1) { interleaved[..frames].CopyTo(mono); return; }
                for (int f = 0, i = Index; f < frames; f++, i += channels) mono[f] = interleaved[i];
                return;
            case ChannelRouteKind.Mid:
                if (channels == 1) { interleaved[..frames].CopyTo(mono); return; }
                for (int f = 0, i = 0; f < frames; f++, i += channels) mono[f] = 0.5f * (interleaved[i] + interleaved[i + 1]);
                return;
            case ChannelRouteKind.Side:
                if (channels == 1) { mono[..frames].Clear(); return; }
                for (int f = 0, i = 0; f < frames; f++, i += channels) mono[f] = 0.5f * (interleaved[i] - interleaved[i + 1]);
                return;
        }
    }

    public override string ToString() => Kind switch
    {
        ChannelRouteKind.Channel when Index == 0 => "L",
        ChannelRouteKind.Channel when Index == 1 => "R",
        ChannelRouteKind.Channel => $"Ch {Index + 1}",
        ChannelRouteKind.Mid => "Mid",
        _ => "Side",
    };
}
