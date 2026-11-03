using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Advanced: ADB, the shell box and logs.</summary>
public sealed partial class AdvancedSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public AdvancedSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
