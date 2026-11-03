using Linc.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Finding your phone: what Bluetooth hears, home networks and the home rule, refreshed every 3 s while shown.</summary>
public sealed partial class FindingSettings : UserControl
{
    private readonly NearbyRules _nearby;
    private readonly IBlePresenceService _ble;
    private readonly IDeviceRegistry _registry;
    private bool _showingNearby;

    public FindingSettings()
    {
        InitializeComponent();
        _nearby = App.Services.GetRequiredService<NearbyRules>();
        _ble = App.Services.GetRequiredService<IBlePresenceService>();
        _registry = App.Services.GetRequiredService<IDeviceRegistry>();
        Action onNearby = () => DispatcherQueue.TryEnqueue(() => ShowNearby());
        var nearbyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        nearbyTimer.Tick += (_, _) => ShowNearby();
        Loaded += (_, _) => { _nearby.Changed += onNearby; ShowNearby(); nearbyTimer.Start(); };
        Unloaded += (_, _) => { _nearby.Changed -= onNearby; nearbyTimer.Stop(); };
    }

    /// <summary>The line on the Settings home card: what Bluetooth hears and where this PC is.</summary>
    internal static string Summary(IBlePresenceService ble, NearbyRules nearby)
    {
        var bluetooth = !ble.IsScanning ? "Bluetooth off"
            : ble.LastStatus is { } heard && DateTimeOffset.UtcNow - heard.At < TimeSpan.FromMinutes(2) ? "Phone heard over Bluetooth"
            : "Listening over Bluetooth";
        return nearby.AtHome ? $"{bluetooth} · At home" : bluetooth;
    }

    private void ShowNearby(string? bluetoothMessage = null)
    {
        _showingNearby = true;
        NearbyBluetooth.Text = bluetoothMessage ?? (_ble.IsScanning
            ? (_ble.IsAdvertising ? "Bluetooth: listening for the phone, and the phone can hear this PC." : "Bluetooth: listening for the phone.")
            : "Bluetooth: off or unavailable on this PC, so the phone isn't heard. Linc tries again every 15 s.");
        NearbyBluetoothButton.Visibility = _ble.IsScanning ? Visibility.Collapsed : Visibility.Visible;
        NearbyPhone.Text = _ble.LastStatus is { } heard
            ? $"The phone, {Ago(heard.At)}: {BlePresenceService.Describe(heard.Flags)}."
            : "The phone hasn't been heard over Bluetooth yet. On the phone: Linc › Settings › Finding your PC › Bluetooth helper (needs Nearby devices).";
        var current = NearbyRules.CurrentNetworks();
        var home = _nearby.AtHome;
        var learnt = _nearby.LinkedNetworks();
        NearbyHome.Text = current.Count == 0 ? "Home networks: this PC isn't on a network right now."
            : home ? $"Home: you're on {string.Join(", ", current)}" + (current.Any(c => learnt.Contains(c, StringComparer.OrdinalIgnoreCase)) ? ", where the phone has linked before." : ", marked as home.")
            : $"This PC is on {string.Join(", ", current)}" + (_registry.HomeNetworks.Count > 0 ? $" (home is {string.Join(", ", _registry.HomeNetworks)})." : ". Mark it as home to use the home rule below.");
        NearbyHomeButton.Content = home ? "Not home" : "This is home";
        // Learnt places can't be un-marked here (they come back on the next link); marked ones can.
        NearbyHomeButton.Visibility = home && !current.Any(c => _registry.HomeNetworks.Contains(c, StringComparer.OrdinalIgnoreCase))
            ? Visibility.Collapsed : Visibility.Visible;
        NearbyHomeButton.IsEnabled = current.Count > 0;
        NearbyBluetoothAtHome.IsOn = _registry.BluetoothAtHome;
        _showingNearby = false;
    }

    private static string Ago(DateTimeOffset at)
    {
        var seconds = (DateTimeOffset.UtcNow - at).TotalSeconds;
        return seconds < 10 ? "just now" : seconds < 90 ? $"{seconds:0} s ago" : $"{seconds / 60:0} min ago";
    }

    private async void OnNearbyBluetoothClick(object sender, RoutedEventArgs e)
    {
        try
        {
            NearbyBluetoothButton.IsEnabled = false;
            var result = await _nearby.TurnPcBluetoothOnAsync();
            NearbyBluetoothButton.IsEnabled = true;
            ShowNearby(result);
        }
        catch (Exception ex)
        {
            NearbyBluetoothButton.IsEnabled = true;
            AsyncVoidGuard.Report(ex, "FindingSettings.OnNearbyBluetoothClick");
        }
    }

    private void OnNearbyHomeClick(object sender, RoutedEventArgs e)
    {
        _nearby.MarkCurrentAsHome(!_nearby.AtHome);
        ShowNearby();
    }

    private void OnNearbyBluetoothAtHomeToggled(object sender, RoutedEventArgs e)
    {
        if (_showingNearby || NearbyBluetoothAtHome.IsOn == _registry.BluetoothAtHome) return;
        _registry.SaveBluetoothAtHome(NearbyBluetoothAtHome.IsOn);
        if (NearbyBluetoothAtHome.IsOn) _nearby.Recheck();
    }
}
