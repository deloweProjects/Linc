using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Phones.</summary>
public sealed partial class DevicesSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public DevicesSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        // Only while shown: the view model is one for the app, and each Settings visit made a new
        // page that subscribed again, so a Remove asked as many times as Settings had been opened.
        Action<KnownDeviceVm> onRemove = vm => _ = ConfirmRemoveAsync(vm);
        Loaded += (_, _) => ViewModel.RemoveRequested += onRemove;
        Unloaded += (_, _) => ViewModel.RemoveRequested -= onRemove;
    }

    /// <summary>The same question as the tab's X "Remove completely" (D-079).</summary>
    private async Task ConfirmRemoveAsync(KnownDeviceVm vm)
    {
        try
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
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "DevicesSettings.ConfirmRemoveAsync");
        }
    }
}
