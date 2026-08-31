using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

public sealed partial class OnboardingView : UserControl
{
    public OnboardingViewModel? ViewModel { get; private set; }

    public OnboardingView()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Visibility == Visibility.Visible)
        {
            ViewModel = App.Services.GetRequiredService<OnboardingViewModel>();
            ViewModel.CloseRequested += OnCloseRequested;
            Bindings.Update();
        }
        else
        {
            if (ViewModel != null)
            {
                ViewModel.CloseRequested -= OnCloseRequested;
                ViewModel.Dispose();
                ViewModel = null;
                Bindings.Update();
            }
        }
    }

    private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        switch (ViewModel.Step)
        {
            case OnboardingStep.Intro:
                ViewModel.StartCommand.Execute(null);
                break;
            case OnboardingStep.EnableDebugging:
                ViewModel.BeginDetectionCommand.Execute(null);
                break;
            case OnboardingStep.Done:
                OnCloseRequested();
                break;
            default:
                break;
        }
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        OnCloseRequested();
    }

    private void OnCloseRequested()
    {
        var shell = App.Services.GetRequiredService<AppShellViewModel>();
        shell.IsOnboarding = false;
    }
}
