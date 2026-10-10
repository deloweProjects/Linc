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
        _cable = new VirtualCableSetup(App.Services.GetRequiredService<ILogService>(), PcVoiceAccess.CablePresent);
        Loaded += async (_, _) => await RefreshCableAsync();
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
        CableStatus.Text = "Setting up… approve Windows' prompt, then click Install Driver in VB-Audio's setup.";
        var result = await Task.Run(() => _cable.InstallAsync());
        await RefreshCableAsync(result.Ok ? null : result.Message);
    }

    private async void OnCableRemoveClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CableRemoveButton.IsEnabled = false;
        CableStatus.Text = "Approve Windows' prompt, then click Remove Driver in VB-Audio's setup.";
        var result = await Task.Run(() => _cable.UninstallAsync());
        await RefreshCableAsync(result.Message);
    }

    /// <summary>
    /// "Clear history" confirmation gate (§2.4): the action must ask before deleting every stored
    /// notification. Matches the Files page delete dialog shape — the gate lives in the code-behind
    /// so the view model just runs the delete once the user confirms.
    /// </summary>
    private async void OnClearHistoryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
}
