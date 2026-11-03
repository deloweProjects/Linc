using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Appearance. Everything here is a plain binding to <see cref="SettingsViewModel"/>.</summary>
public sealed partial class AppearanceSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public AppearanceSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
