using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Linc.Desktop.Views.Tools;

/// <summary>Tools › Commands: search, fill in, run or copy an everyday adb command.</summary>
public sealed partial class CommandsSection : UserControl
{
    public CommandsToolViewModel ViewModel { get; } = App.Services.GetRequiredService<CommandsToolViewModel>();

    public CommandsSection()
    {
        InitializeComponent();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.CopyText;
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ViewModel.Copied();
    }

    /// <summary>Browse… on a "file on this PC" or "folder on this PC" parameter.</summary>
    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandParamItem item) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            if (item.IsFolder)
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFolderAsync() is { } folder) ViewModel.Picked(item, folder.Path);
            }
            else
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                picker.FileTypeFilter.Add(item.Param.Extension ?? "*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFileAsync() is { } file) ViewModel.Picked(item, file.Path);
            }
        }
        catch (Exception)
        {
            // A picker that can't open (rare) leaves the box for typing the path.
        }
    }
}
