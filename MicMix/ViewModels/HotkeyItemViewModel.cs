using MicMix.Hotkeys;

namespace MicMix.ViewModels;

public sealed class HotkeyItemViewModel : Observable
{
    private bool _isCapturing;
    private bool _isTaken;

    public HotkeyItemViewModel(HotkeyBinding binding)
    {
        Binding = binding;
        Title = binding.Action switch
        {
            HotkeyAction.ToggleMicMute => "Выключить / включить мой голос",
            HotkeyAction.ToggleApps => "Убрать / вернуть программы",
            HotkeyAction.ToggleVoiceChain => "«Чистый голос» вкл / выкл",
            HotkeyAction.ToggleMonitor => "«Послушать себя» вкл / выкл",
            HotkeyAction.ToggleVoiceFx => "Войс-ченджер вкл / выкл",
            _ => binding.Action.ToString(),
        };
    }

    public HotkeyBinding Binding { get; }
    public string Title { get; }

    public string Text => _isCapturing ? "Нажми сочетание…" : Binding.ToString();

    public bool IsCapturing
    {
        get => _isCapturing;
        set { if (Set(ref _isCapturing, value)) Raise(nameof(Text)); }
    }

    /// <summary>Another program already owns this combo.</summary>
    public bool IsTaken { get => _isTaken; set => Set(ref _isTaken, value); }

    public void Changed() => Raise(nameof(Text));
}
