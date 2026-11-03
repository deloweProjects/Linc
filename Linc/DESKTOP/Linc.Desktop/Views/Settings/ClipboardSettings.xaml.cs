using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Clipboard.</summary>
public sealed partial class ClipboardSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public ClipboardSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
