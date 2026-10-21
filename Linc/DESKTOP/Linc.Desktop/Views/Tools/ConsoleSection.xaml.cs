using System.ComponentModel;
using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Linc.Desktop.Views.Tools;

public sealed partial class ConsoleSection : UserControl
{
    public ConsoleToolViewModel ViewModel { get; } = App.Services.GetRequiredService<ConsoleToolViewModel>();

    public ConsoleSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ScrollToEnd();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsoleToolViewModel.Output)) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        OutputScroll.UpdateLayout();
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>Enter runs; ↑ and ↓ walk the history, with the caret at the end.</summary>
    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (ViewModel.RunCommand.CanExecute(null)) ViewModel.RunCommand.Execute(null);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                ViewModel.HistoryUp();
                Prompt.SelectionStart = Prompt.Text.Length;
                break;
            case VirtualKey.Down:
                e.Handled = true;
                ViewModel.HistoryDown();
                Prompt.SelectionStart = Prompt.Text.Length;
                break;
        }
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Output.Length == 0) return;
        var package = new DataPackage();
        package.SetText(ViewModel.Output);
        Clipboard.SetContent(package);
    }
}
