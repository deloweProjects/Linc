using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

/// <summary>The Clipboard page (1.0.0.11, D-083).</summary>
public sealed partial class ClipboardPage : Page
{
    public ClipboardViewModel ViewModel { get; }

    public ClipboardPage()
    {
        ViewModel = App.Services.GetRequiredService<ClipboardViewModel>();
        InitializeComponent();
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Clear the clipboard history?",
                Content = "Everything except pinned items is deleted from this PC. Your phone keeps its own history.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.ClearHistory();
            }
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "ClipboardPage.OnClearClick");
        }
    }
}
