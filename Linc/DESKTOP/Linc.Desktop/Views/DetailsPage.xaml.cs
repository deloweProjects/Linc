using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

public sealed partial class DetailsPage : Page
{
    public DetailsViewModel ViewModel { get; }

    public DetailsPage()
    {
        ViewModel = App.Services.GetRequiredService<DetailsViewModel>();
        InitializeComponent();
    }
}
