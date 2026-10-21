using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Tools;

/// <summary>Tools › System. Everything is in the view model.</summary>
public sealed partial class SystemSection : UserControl
{
    public SystemToolViewModel ViewModel { get; } = App.Services.GetRequiredService<SystemToolViewModel>();

    public SystemSection()
    {
        InitializeComponent();
    }
}
