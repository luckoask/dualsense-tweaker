using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DualSenseTweaker.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public void RefreshTranslations() => OnPropertyChanged(string.Empty);
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
