using System.ComponentModel;
using System.IO;
using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Linc.Desktop.Views.Tools;

public sealed partial class CaptureSection : UserControl
{
    public CaptureToolViewModel ViewModel { get; } = App.Services.GetRequiredService<CaptureToolViewModel>();

    public CaptureSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ShowLast();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureToolViewModel.LastPath)) ShowLast();
    }

    private void ShowLast()
    {
        LastImage.Source = ViewModel.LastIsImage && File.Exists(ViewModel.LastPath)
            ? new BitmapImage(new Uri(ViewModel.LastPath))
            : null;
    }

    /// <summary>Copies the picture itself (and the file, for apps that take files) to the clipboard.</summary>
    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.LastPath;
        if (!File.Exists(path)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage();
            if (ViewModel.LastIsImage) package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            package.SetStorageItems([file]);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // The clipboard can be busy with another app; nothing to clean up.
        }
    }
}
