using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Linc.Desktop.Views;

public sealed partial class SyncPage : Page
{
    public SyncViewModel ViewModel { get; }

    public SyncPage()
    {
        ViewModel = App.Services.GetRequiredService<SyncViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateLaneLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncViewModel.MessagesOn) or nameof(SyncViewModel.CallsOn))
        {
            UpdateLaneLayout();
        }
    }

    // Messages and Calls sit side by side; when only one lane is on, it spans both
    // columns so it fills the width. Visibility is handled by x:Bind in the XAML.
    private void UpdateLaneLayout()
    {
        bool messages = ViewModel.MessagesOn, calls = ViewModel.CallsOn;
        if (messages && calls)
        {
            Grid.SetColumn(MessagesCard, 0);
            Grid.SetColumnSpan(MessagesCard, 1);
            Grid.SetColumn(CallsCard, 1);
            Grid.SetColumnSpan(CallsCard, 1);
        }
        else if (messages)
        {
            Grid.SetColumn(MessagesCard, 0);
            Grid.SetColumnSpan(MessagesCard, 2);
        }
        else if (calls)
        {
            Grid.SetColumn(CallsCard, 0);
            Grid.SetColumnSpan(CallsCard, 2);
        }
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.SetFolderPcPath(folder.Path);
        }
    }
}
