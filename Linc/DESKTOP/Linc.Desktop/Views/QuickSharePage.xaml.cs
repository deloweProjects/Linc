using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Linc.Desktop.Views;

public sealed partial class QuickSharePage : Page
{
    public QuickShareViewModel ViewModel { get; }

    public QuickSharePage()
    {
        ViewModel = App.Services.GetRequiredService<QuickShareViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Opening the page is the moment someone wants to send: start looking straight away.
        if (!ViewModel.IsScanning)
        {
            _ = ViewModel.ScanCommand.ExecuteAsync(null);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.StopScan(); // the BLE wake beacon shouldn't outlive the page that wanted it
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await ViewModel.SendAsync(files.Select(f => f.Path).ToList());
        }
    }

    private void OnDeviceDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && ViewModel.CanSend)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Quick Share to {ViewModel.SelectedDevice?.Name}";
        }
    }

    private async void OnDeviceDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
        await ViewModel.SendAsync(paths);
    }
}
