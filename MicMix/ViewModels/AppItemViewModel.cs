using System.Windows.Media;

namespace MicMix.ViewModels;

public sealed class AppItemViewModel : Observable
{
    private readonly Action<AppItemViewModel> _changed;
    private bool _enabled;
    private double _volumePercent;
    private double _level;
    private bool _isRunning;
    private bool _isPlaying;
    private string _status = "";
    private ImageSource? _icon;
    private string _displayName;

    public AppItemViewModel(string key, string displayName, bool enabled, float volume, Action<AppItemViewModel> changed)
    {
        Key = key;
        _displayName = displayName;
        _enabled = enabled;
        _volumePercent = volume * 100;
        _changed = changed;
    }

    public string Key { get; }

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>"Goes into my mic".</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            if (!value) Level = 0;
            Raise(nameof(ShowVoiceChatWarning));
            _changed(this);
        }
    }

    /// <summary>Discord and friends: allowed in the mic, but they'd echo everyone back to themselves.</summary>
    public bool IsVoiceChat => Apps.AppScanner.VoiceChats.Contains(Key);

    public bool ShowVoiceChatWarning => _enabled && IsVoiceChat;

    public double VolumePercent
    {
        get => _volumePercent;
        set { if (Set(ref _volumePercent, Math.Round(value))) _changed(this); }
    }

    public float Volume => (float)(_volumePercent / 100);

    public double Level { get => _level; set => Set(ref _level, value); }

    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Stable sort key so the list does not jump around: selected apps on top, then by name.</summary>
    public string SortKey => (Enabled ? "0" : "1") + DisplayName.ToLowerInvariant();

    internal void RaiseSortKey() => Raise(nameof(SortKey));
}
