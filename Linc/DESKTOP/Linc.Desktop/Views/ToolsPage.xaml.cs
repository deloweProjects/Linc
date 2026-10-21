using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Linc.Desktop.Views;

/// <summary>Tools: ADB as plain controls (Vibe/claude-notes/linc-tools). Each section is its own
/// control under Views/Tools; this page holds the rail, the phone chip and the shared bits.</summary>
public sealed partial class ToolsPage : Page
{
    public ToolsViewModel ViewModel { get; }

    private readonly ToolsContext _ctx;

    public ToolsPage()
    {
        ViewModel = App.Services.GetRequiredService<ToolsViewModel>();
        _ctx = App.Services.GetRequiredService<ToolsContext>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ctx.Confirm = ConfirmAsync;
        ViewModel.Activate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// The confirm step (PLAN.md §2 rule 4): Cancel is the default. A destructive step's button
    /// is red and only wakes after 1.5 s, so a double click can't wipe anything.
    /// </summary>
    private async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = request.Title,
            Content = new TextBlock { Text = request.Body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = request.Primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (request.Destructive)
        {
            dialog.IsPrimaryButtonEnabled = false;
            var style = new Style(typeof(Button));
            var red = Application.Current.Resources.TryGetValue("ErrorBrush", out var brush) ? brush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Firebrick);
            style.Setters.Add(new Setter(Control.BackgroundProperty, red));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)));
            dialog.PrimaryButtonStyle = style;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.5);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            timer.Start();
        }
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false; // another dialog is already open
        }
    }

    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.LastCommand.TrimStart('⌘', ' ');
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
