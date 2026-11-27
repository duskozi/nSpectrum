using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace nSpectrum.Capture;

public enum DeviceKind
{
    /// <summary>A capture endpoint: microphone, line in, interface input.</summary>
    Input,
    /// <summary>A render endpoint captured through WASAPI loopback (what you hear).</summary>
    Loopback,
}

/// <summary>An endpoint the user can pick as a source.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, DeviceKind Kind, bool IsDefault, int SampleRate, int Channels)
{
    public string DisplayName => Kind == DeviceKind.Loopback ? $"Loopback: {Name}" : Name;
    public override string ToString() => DisplayName + (IsDefault ? " (default)" : "");
}

/// <summary>Lists WASAPI endpoints and reports device changes (unplug, default change, format change).</summary>
public sealed class AudioDevices : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Notifier _notifier;

    public AudioDevices()
    {
        _notifier = new Notifier(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifier);
    }

    /// <summary>Raised on a COM thread when endpoints appear, disappear or change. Marshal to the UI yourself.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AudioDeviceInfo> List()
    {
        var result = new List<AudioDeviceInfo>();
        Add(DataFlow.Capture, DeviceKind.Input, result);
        Add(DataFlow.Render, DeviceKind.Loopback, result);
        return result;
    }

    private void Add(DataFlow flow, DeviceKind kind, List<AudioDeviceInfo> into)
    {
        // Every MMDevice and AudioClient wraps a COM object. Release them here instead of leaving
        // them to the finalizer: device notifications come in bursts and each one re-lists.
        string? defaultId = null;
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using MMDevice def = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
        }
        catch { /* no default endpoint */ }

        var found = new List<AudioDeviceInfo>();
        foreach (MMDevice d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (d)
            {
                int rate = 0, ch = 0;
                try
                {
                    using AudioClient client = d.AudioClient;
                    var mix = client.MixFormat;
                    rate = mix.SampleRate;
                    ch = mix.Channels;
                }
                catch { /* endpoint busy or in exclusive use */ }
                string name;
                try { name = d.FriendlyName; } catch { name = "Unknown device"; }
                found.Add(new AudioDeviceInfo(d.ID, name, kind, d.ID == defaultId, rate, ch));
            }
        }
        // Default first, then by name; stable for the UI.
        found.Sort((a, b) => a.IsDefault != b.IsDefault ? b.IsDefault.CompareTo(a.IsDefault) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        into.AddRange(found);
    }

    /// <summary>Mix format (rate, channels) of a render or capture endpoint.</summary>
    internal (int SampleRate, int Channels) MixFormat(string id)
    {
        using MMDevice d = _enumerator.GetDevice(id);
        using AudioClient c = d.AudioClient;
        var f = c.MixFormat;
        return (f.SampleRate, f.Channels);
    }

    internal MMDevice Get(string id) => _enumerator.GetDevice(id);

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifier); } catch { }
        _enumerator.Dispose();
    }

    private sealed class Notifier(AudioDevices owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.Changed?.Invoke(owner, EventArgs.Empty);
        public void OnDeviceAdded(string pwstrDeviceId) => owner.Changed?.Invoke(owner, EventArgs.Empty);
        public void OnDeviceRemoved(string deviceId) => owner.Changed?.Invoke(owner, EventArgs.Empty);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
