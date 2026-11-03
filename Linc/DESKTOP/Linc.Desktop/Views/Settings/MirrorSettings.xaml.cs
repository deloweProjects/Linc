using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Mirror and sound. The VB-CABLE set-up moved here from the old single Settings page as it was.</summary>
public sealed partial class MirrorSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    private readonly VirtualCableSetup _cable;

    public MirrorSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        _cable = new VirtualCableSetup(App.Services.GetRequiredService<ILogService>(), PcVoiceAccess.CablePresent);
        Loaded += async (_, _) =>
        {
            try
            {
                await RefreshCableAsync();
            }
            catch (Exception ex)
            {
                AsyncVoidGuard.Report(ex, "MirrorSettings.Loaded");
            }
        };
    }

    private void OnOpenDeviceClick(object sender, RoutedEventArgs e) => (App.MainWindowInstance as MainWindow)?.ShowPage("Device");

    // ---- phone as microphone (D-071) ----------------------------------------------

    private async Task RefreshCableAsync(string? message = null)
    {
        bool present = await Task.Run(PcVoiceAccess.CablePresent); // COM: off the UI thread
        bool busy = _cable.IsBusy;
        CableStatus.Text = message ?? (busy ? "Setting up…" : present
            ? "Ready — the phone can be this PC's microphone."
            : "Not set up. Linc sets it up the first time you use your phone as the microphone, or here.");
        CableInstallButton.IsEnabled = !busy && !present;
        CableRemoveButton.IsEnabled = !busy && present;
    }

    private async void OnCableInstallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Set up the phone as a microphone?",
                Content = $"Linc will download {VirtualCablePackage.DisplayName} {VirtualCablePackage.Version} from vb-audio.com, check it, and open VB-Audio's own installer: approve Windows' prompt, then click \"Install Driver\". VB-CABLE is donationware — see vb-audio.com for its terms.",
                PrimaryButtonText = "Set up",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            CableInstallButton.IsEnabled = false;
            CableStatus.Text = "Downloading VB-CABLE…";
            var progress = new Progress<int>(p => CableStatus.Text = p < 100
                ? $"Downloading VB-CABLE… {p} %"
                : "Approve Windows' prompt, then click Install Driver in VB-Audio's setup.");
            var result = await Task.Run(() => _cable.InstallAsync(progress));
            await RefreshCableAsync(result.Ok ? null : result.Message);
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "MirrorSettings.OnCableInstallClick");
        }
    }

    private async void OnCableRemoveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CableRemoveButton.IsEnabled = false;
            CableStatus.Text = "Approve Windows' prompt, then click Remove Driver in VB-Audio's setup.";
            var result = await Task.Run(() => _cable.UninstallAsync());
            await RefreshCableAsync(result.Message);
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "MirrorSettings.OnCableRemoveClick");
        }
    }
}
