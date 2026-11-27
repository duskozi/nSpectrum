using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace nSpectrum.App.ViewModels;

/// <summary>Base for view models: raises <see cref="PropertyChanged"/> when a property gets a new value.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Stores the value and raises <see cref="PropertyChanged"/>; false when it was already equal.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
