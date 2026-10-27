using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.RemoveRequested += vm => _ = ConfirmRemoveAsync(vm);
        _cable = new VirtualCableSetup(App.Services.GetRequiredService<ILogService>(), PcVoiceAccess.CablePresent);
        Loaded += async (_, _) => await RefreshCableAsync();
    }

    /// <summary>The same question as the tab's X "Remove completely" (D-079).</summary>
    private async Task ConfirmRemoveAsync(KnownDeviceVm vm)
    {
        var dialog = new ContentDialog
        {
            Title = $"Remove {vm.Model}?",
            Content = $"Linc will unpair {vm.Model} and delete its saved copy on this PC. You'd pair it again to use it.\n\n" +
                "To just put it away, close its tab instead — it stays paired.",
            PrimaryButtonText = "Remove completely",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RemoveDevice(vm);
        }
    }

    // ---- phone as microphone (D-071) ----------------------------------------------

    private readonly VirtualCableSetup _cable;

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

    private async void OnCableInstallClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
                XamlRoot = Content.XamlRoot,
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
            AsyncVoidGuard.Report(ex, "SettingsPage.OnCableInstallClick");
        }
    }

    private async void OnCableRemoveClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
            AsyncVoidGuard.Report(ex, "SettingsPage.OnCableRemoveClick");
        }
    }

    /// <summary>
    /// "Clear history" confirmation gate (§2.4): the action must ask before deleting every stored
    /// notification. Matches the Files page delete dialog shape — the gate lives in the code-behind
    /// so the view model just runs the delete once the user confirms.
    /// </summary>
    private async void OnClearHistoryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Clear notification history?",
                Content = "Every stored notification, for all devices, will be deleted from this PC. This can't be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SettingsPage.OnClearHistoryClick");
        }
    }
}
