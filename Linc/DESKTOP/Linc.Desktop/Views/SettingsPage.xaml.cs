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
