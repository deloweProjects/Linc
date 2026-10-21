using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Linc.Desktop.Views.Tools;

public sealed partial class RescueSection : UserControl
{
    public RescueToolViewModel ViewModel { get; } = App.Services.GetRequiredService<RescueToolViewModel>();

    public RescueSection()
    {
        InitializeComponent();
    }

    // The PIN goes straight from the box to the phone and the box is emptied: the view model
    // never keeps it.
    private async void OnGetInClick(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        PinBox.Password = "";
        try
        {
            await ViewModel.GetInAsync(pin);
        }
        catch (Exception)
        {
            // GetInAsync reports its own failures; an async void must never throw into XAML.
        }
    }

    private void OnPinKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        OnGetInClick(sender, e);
    }
}
