using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views.Settings;

/// <summary>Settings › Connection. The hotspot and the Connection Doctor moved here from the old single Settings page as they were.</summary>
public sealed partial class ConnectionSettings : UserControl
{
    public SettingsViewModel ViewModel { get; }

    private readonly PcHotspot _hotspot;
    private bool _hotspotBusy;

    public ConnectionSettings()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        _hotspot = App.Services.GetRequiredService<PcHotspot>();
        Action onHotspot = () => DispatcherQueue.TryEnqueue(() => ShowHotspot());
        Loaded += (_, _) => { _hotspot.Changed += onHotspot; ShowHotspot(); };
        Unloaded += (_, _) => _hotspot.Changed -= onHotspot;
    }

    // ---- PC hotspot (LincLink step 6, by hand) ---------------------------------------

    private void ShowHotspot(string? message = null)
    {
        var on = _hotspot.Current != PcHotspot.Mode.Off;
        HotspotButton.Content = on ? "Stop PC hotspot" : "Start PC hotspot";
        HotspotButton.IsEnabled = !_hotspotBusy;
        HotspotStatus.Text = message ?? (_hotspot.Current switch
        {
            PcHotspot.Mode.WifiDirect => "On (Wi-Fi Direct).",
            PcHotspot.Mode.MobileHotspot when _hotspot.WasAlreadyOn => "On: Windows' Mobile Hotspot is on (switched on outside Linc), so Linc uses it.",
            PcHotspot.Mode.MobileHotspot => "On (Windows' Mobile Hotspot, started by Linc).",
            _ => _hotspot.LastError ?? "Off",
        });
        HotspotDetails.Visibility = on && _hotspot.Network is not null ? Visibility.Visible : Visibility.Collapsed;
        if (on && _hotspot.Network is { } network)
        {
            var (ssid, password) = network;
            HotspotSsid.Text = $"Wi-Fi: {ssid}";
            HotspotPassword.Text = $"Password: {password}";
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(PcHotspot.JoinQr(ssid, password), QRCoder.QRCodeGenerator.ECCLevel.Q);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new System.IO.MemoryStream(png);
            image.SetSource(stream.AsRandomAccessStream());
            HotspotQr.Source = image;
        }
    }

    private async void OnHotspotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _hotspotBusy = true;
            ShowHotspot(_hotspot.Current == PcHotspot.Mode.Off ? "Starting…" : "Stopping…");
            string? result = null;
            if (_hotspot.Current == PcHotspot.Mode.Off)
            {
                result = await Task.Run(_hotspot.StartAsync);
            }
            else
            {
                await Task.Run(_hotspot.StopAsync);
            }
            _hotspotBusy = false;
            ShowHotspot(result);
        }
        catch (Exception ex)
        {
            _hotspotBusy = false;
            ShowHotspot($"Couldn't: {ex.Message}");
            AsyncVoidGuard.Report(ex, "ConnectionSettings.OnHotspotClick");
        }
    }

    // ---- Connection Doctor -------------------------------------------------------------

    /// <summary>
    /// Runs every check, shows each result with its plain reason, and a Fix button where Linc can
    /// fix it; "Check again" re-runs them. Built in code: one list, no page of its own.
    /// </summary>
    private async void OnConnectionDoctorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var doctor = App.Services.GetRequiredService<ConnectionDoctor>();
            var list = new StackPanel { Spacing = 14, MinWidth = 460 };
            var scroller = new ScrollViewer { Content = list, MaxHeight = 520 };
            var dialog = new ContentDialog
            {
                Title = "Connection Doctor",
                Content = scroller,
                PrimaryButtonText = "Check again",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            async Task RunAsync()
            {
                list.Children.Clear();
                list.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new ProgressRing { IsActive = true, Width = 18, Height = 18 }, new TextBlock { Text = "Checking…" } },
                });
                var findings = await Task.Run(() => doctor.RunAsync(CancellationToken.None));
                list.Children.Clear();
                foreach (var finding in findings)
                {
                    list.Children.Add(Row(finding));
                }
            }

            UIElement Row(DoctorFinding finding)
            {
                var (glyph, brush) = finding.Status switch
                {
                    DoctorStatus.Ok => ("", "SuccessBrush"),
                    DoctorStatus.Warning => ("", "WarningBrush"),
                    DoctorStatus.Problem => ("", "ErrorBrush"),
                    _ => ("", "OnSurfaceVariantBrush"),
                };
                var icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                if (Application.Current.Resources.TryGetValue(brush, out var b) && b is Microsoft.UI.Xaml.Media.Brush color)
                {
                    icon.Foreground = color;
                }
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock { Text = finding.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var detail = new TextBlock { Text = finding.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
                text.Children.Add(detail);
                if (finding is { FixLabel: { } label, Fix: { } fix })
                {
                    var button = new Button { Content = label, Margin = new Thickness(0, 6, 0, 0) };
                    button.Click += async (_, _) =>
                    {
                        try
                        {
                            button.IsEnabled = false;
                            detail.Text = await Task.Run(fix);
                        }
                        catch (Exception ex)
                        {
                            detail.Text = $"That didn't work: {ex.Message}";
                            button.IsEnabled = true;
                        }
                    };
                    text.Children.Add(button);
                }
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(icon);
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                return grid;
            }

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true; // stay open and check again
                var deferral = args.GetDeferral();
                try
                {
                    await RunAsync();
                }
                finally
                {
                    deferral.Complete();
                }
            };
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    await RunAsync();
                }
                catch (Exception ex)
                {
                    AsyncVoidGuard.Report(ex, "ConnectionSettings.ConnectionDoctor");
                }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "ConnectionSettings.OnConnectionDoctorClick");
        }
    }
}
