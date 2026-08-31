using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Linc.Desktop.Views;

public sealed partial class FilesPage : Page
{
    public FilesViewModel ViewModel { get; }

    public FilesPage()
    {
        ViewModel = App.Services.GetRequiredService<FilesViewModel>();
        InitializeComponent();
    }

    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        _ = ViewModel.NavigateToSegmentAsync(e.Index);
    }

    // Built on open rather than data-bound so the menu always reflects the live roots (an SD card
    // can appear after connect) and so each pick always navigates — even to the location you're
    // already under, which a single-select dropdown can't do (M2a "Go to" menu).
    private void OnRootsFlyoutOpening(object sender, object e)
    {
        RootsFlyout.Items.Clear();
        foreach (var root in ViewModel.Roots)
        {
            var path = root.Path;
            var item = new MenuFlyoutItem { Text = root.Label };
            item.Click += (_, _) => _ = ViewModel.NavigateAsync(path);
            RootsFlyout.Items.Add(item);
        }
    }

    private async void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RemoteEntryVm entry)
        {
            await ViewModel.OpenEntryAsync(entry);
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Send to phone";
        }
    }

    private async void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<StorageFile>().Select(file => file.Path).ToList();
        if (paths.Count > 0)
        {
            await ViewModel.UploadFilesAsync(paths);
        }
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await ViewModel.UploadFilesAsync(files.Select(file => file.Path).ToList());
        }
    }

    /// <summary>Sends a PC file into the phone's Share tab (M19) — not the browsed folder.</summary>
    private async void OnShareToPhoneClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        ViewModel.ErrorMessage = null;
        try
        {
            await App.Services.GetRequiredService<Services.IShareService>()
                .SendFileToPhoneAsync(file.Path, CancellationToken.None);
        }
        catch (Services.LincException ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New folder", "Folder name");
        if (name is { Length: > 0 })
        {
            await ViewModel.CreateFolderAsync(name);
        }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var current = ViewModel.SelectedEntry;
        if (current is null)
        {
            return;
        }
        var name = await PromptAsync("Rename", "New name", current.Name);
        if (name is { Length: > 0 })
        {
            await ViewModel.RenameSelectedAsync(name);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var current = ViewModel.SelectedEntry;
        if (current is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"Delete {current.Name}?",
            Content = current.IsDirectory
                ? "The folder and everything inside it will be deleted from the phone. This can't be undone."
                : "The file will be deleted from the phone. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedAsync();
        }
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string initial = "")
    {
        var input = new TextBox { Text = initial, PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
}
