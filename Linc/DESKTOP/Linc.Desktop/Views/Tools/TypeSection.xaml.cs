using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Linc.Desktop.Views.Tools;

public sealed partial class TypeSection : UserControl
{
    public TypeToolViewModel ViewModel { get; } = App.Services.GetRequiredService<TypeToolViewModel>();

    public TypeSection()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TypeToolViewModel.LiveKeys)) return;
            // Put the keyboard where the keys are going: the live box, or back in the line box.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.LiveKeys) LivePad.Focus(FocusState.Programmatic);
                else LineBox.Focus(FocusState.Programmatic);
            });
        };
    }

    private void OnLineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.SendCommand.Execute(null);
    }

    // Keys that aren't characters (Backspace, Enter, Tab, arrows…): sent, and stopped here so
    // they don't move focus around the page.
    private void OnLivePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.LiveSpecialKey(e.Key.ToString())) e.Handled = true;
    }

    private void OnLiveCharacter(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // Control characters (Enter, Backspace, Tab, Esc) also arrive here; they went as keys.
        if (e.Character < ' ' || e.Character == '\u007f') return;
        e.Handled = true;
        ViewModel.LiveCharacter(e.Character);
    }

    private void OnLivePadTapped(object sender, TappedRoutedEventArgs e) => LivePad.Focus(FocusState.Pointer);
}
