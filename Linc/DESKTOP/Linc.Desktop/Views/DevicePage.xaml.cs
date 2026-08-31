using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;

namespace Linc.Desktop.Views;

public sealed partial class DevicePage : Page
{
    public DeviceViewModel ViewModel { get; }
    public MirrorViewModel Mirror { get; }
    public MirrorSettingsViewModel MirrorSettings { get; }
    public DesktopModeViewModel DesktopMode { get; }
    public DesktopModeSettingsViewModel DesktopModeSettings { get; }
    public DisplayControlViewModel DisplayControl { get; }

    public DevicePage()
    {
        ViewModel = App.Services.GetRequiredService<DeviceViewModel>();
        Mirror = App.Services.GetRequiredService<MirrorViewModel>();
        MirrorSettings = App.Services.GetRequiredService<MirrorSettingsViewModel>();
        DesktopMode = App.Services.GetRequiredService<DesktopModeViewModel>();
        DesktopModeSettings = App.Services.GetRequiredService<DesktopModeSettingsViewModel>();
        DisplayControl = App.Services.GetRequiredService<DisplayControlViewModel>();
        InitializeComponent();
    }

    private void RotationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not string mode)
        {
            return;
        }
        // Binding-driven update (a seed from the phone's status), not a user choice.
        if (mode == DisplayControl.RotationMode)
        {
            return;
        }
        DisplayControl.SetRotationCommand.Execute(mode);
    }

    private void BrightnessAutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }
        if (toggle.IsOn == (DisplayControl.BrightnessAuto == true))
        {
            return;
        }
        DisplayControl.SetBrightnessAutoCommand.Execute(toggle.IsOn);
    }

    /// <summary>
    /// Brightness slider drag — fires per pixel, so the wire send is debounced 250 ms by
    /// the VM. The OneWay binding only writes phone-side values back into the slider;
    /// drags flow through this handler to the debounce timer.
    /// </summary>
    private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var level = (int)e.NewValue;
        // Programmatic update from a status seed — do not echo it back to the phone.
        if (level == DisplayControl.BrightnessLevel)
        {
            return;
        }
        DisplayControl.SetBrightnessLevelCommand.Execute(level);
    }
}
