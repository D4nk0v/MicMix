using System.Windows;
using System.Windows.Controls;
using MicMix.ViewModels;

namespace MicMix.Views;

// Code-behind for the pages: each only forwards button clicks to the shared view model.

public partial class MixerView : UserControl
{
    public MixerView() => InitializeComponent();
    private MainViewModel Vm => (MainViewModel)DataContext;
    private void OpenSetup_Click(object sender, RoutedEventArgs e) => Vm.StartWizard();
    private void OpenEffects_Click(object sender, RoutedEventArgs e) => Vm.Page = ViewModels.Page.Effects;
}

public partial class VoiceView : UserControl
{
    public VoiceView() => InitializeComponent();
}

public partial class EffectsView : UserControl
{
    public EffectsView() => InitializeComponent();
    private MainViewModel Vm => (MainViewModel)DataContext;

    // Picking a preset is the intent to use it: switch the changer on rather than make the user hunt for the toggle.
    private void Preset_Click(object sender, RoutedEventArgs e) => Vm.VoiceFx.Enabled = true;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Vm.VoiceFx.Pitch = 0;
        Vm.VoiceFx.Robot = 0;
        Vm.VoiceFx.Choppy = 0;
        Vm.VoiceFx.Radio = false;
    }
}

public partial class StreamView : UserControl
{
    public StreamView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private MainViewModel Vm => (MainViewModel)DataContext;

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is HotkeyItemViewModel item) Vm.BeginCapture(item);
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e) => Vm.ResetHotkeys();
    private void RunSetup_Click(object sender, RoutedEventArgs e) => Vm.StartWizard();
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenDataFolder();
}

public partial class WizardView : UserControl
{
    public WizardView() => InitializeComponent();
    private MainViewModel Vm => (MainViewModel)DataContext;
    private void Install_Click(object sender, RoutedEventArgs e) => Vm.InstallVbCable();
    private void OpenPage_Click(object sender, RoutedEventArgs e) => Vm.OpenVbCablePage();
    private void Next_Click(object sender, RoutedEventArgs e) => Vm.WizardNext();
    private void Back_Click(object sender, RoutedEventArgs e) => Vm.WizardBack();
    private void Skip_Click(object sender, RoutedEventArgs e) => Vm.FinishWizard();
}
