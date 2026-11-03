using Linc.Desktop.Services.Tools;
using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Linc.Desktop.Views.Tools;

/// <summary>Tools › Macros. The view model holds everything; this wires the bits XAML can't
/// bind: the per-row buttons, the template menu, the command search and the PIN prompt.</summary>
public sealed partial class MacrosSection : UserControl
{
    public MacrosToolViewModel ViewModel { get; } = App.Services.GetRequiredService<MacrosToolViewModel>();

    public MacrosSection()
    {
        InitializeComponent();
        foreach (var template in ViewModel.Templates)
        {
            var item = new MenuFlyoutItem { Text = template.Name, Icon = new FontIcon { Glyph = template.Glyph } };
            item.Click += (_, _) => ViewModel.AddTemplateCommand.Execute(template);
            TemplatesMenu.Items.Add(item);
        }
        ViewModel.AskPin = AskPinAsync;
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroCard card) ViewModel.RunCommand.Execute(card);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStepRow row) ViewModel.MoveUpCommand.Execute(row);
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStepRow row) ViewModel.MoveDownCommand.Execute(row);
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStepRow row) ViewModel.RemoveStepCommand.Execute(row);
    }

    private void OnStepsDragged(ListViewBase sender, DragItemsCompletedEventArgs args) => ViewModel.StepsReordered();

    private void OnAddKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.AddStepCommand.Execute(null);
    }

    private void OnCommandSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = sender.Text.Trim().Length == 0 ? null : ViewModel.SearchCommands(sender.Text);
    }

    private void OnCommandChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not CommandEntry entry) return;
        ViewModel.PickCommand(entry);
        sender.Text = "";
        sender.ItemsSource = null;
    }

    /// <summary>A "Type the PIN" step: asked here each run, typed, never kept (TOOLS.md §9).</summary>
    private async Task<string?> AskPinAsync()
    {
        if (XamlRoot is null) return null;
        var box = new PasswordBox { PlaceholderText = "PIN" };
        var dialog = new ContentDialog
        {
            Title = "The phone's PIN",
            Content = box,
            PrimaryButtonText = "Type it",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
        }
        catch (Exception)
        {
            return null; // another dialog is already open
        }
    }
}
