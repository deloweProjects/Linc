using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

/// <summary>The Clipboard page (1.0.0.11, D-083).</summary>
public sealed partial class ClipboardPage : Page
{
    public ClipboardViewModel ViewModel { get; }

    public ClipboardPage()
    {
        ViewModel = App.Services.GetRequiredService<ClipboardViewModel>();
        InitializeComponent();
        // Diagnosis (UI test): the page lays out wider than the window; log who is wide, once.
        LayoutUpdated += LogWidthsOnce;
    }

    private bool _loggedWidths;

    private void LogWidthsOnce(object? sender, object e)
    {
        if (_loggedWidths || ActualWidth <= 0 || XamlRoot is null) return;
        _loggedWidths = true;
        var parts = new System.Collections.Generic.List<string>
        {
            $"window {XamlRoot.Size.Width:0}", $"page {ActualWidth:0} (desired {DesiredSize.Width:0})",
            $"scroller {Scroller.ActualWidth:0} (desired {Scroller.DesiredSize.Width:0}, viewport {Scroller.ViewportWidth:0}, extent {Scroller.ExtentWidth:0})",
            $"column {Column.ActualWidth:0} (desired {Column.DesiredSize.Width:0}, max {Column.MaxWidth:0})",
        };
        for (int i = 0; i < Column.Children.Count; i++)
            if (Column.Children[i] is FrameworkElement c)
                parts.Add($"#{i} {c.GetType().Name} {c.ActualWidth:0}/{c.DesiredSize.Width:0}");
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this);
        for (int depth = 0; parent is FrameworkElement f && depth < 6; depth++, parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(f))
            parts.Add($"up{depth} {f.GetType().Name} {f.ActualWidth:0}");
        App.Services.GetRequiredService<ILogService>().Log(LogLevel.Info, "Clipboard layout: " + string.Join("; ", parts));
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear the clipboard history?",
            Content = "Everything except pinned items is deleted from this PC. Your phone keeps its own history.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearHistory();
        }
    }
}
