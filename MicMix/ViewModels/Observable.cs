using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MicMix.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Peak amplitude → 0..1 on a -60..0 dB scale, which is how meters read naturally.</summary>
    public static double PeakToMeter(float peak)
    {
        if (peak <= 0.001f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }
}

public sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}
