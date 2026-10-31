using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.RemoveRequested += vm => _ = ConfirmRemoveAsync(vm);
        _cable = new VirtualCableSetup(App.Services.GetRequiredService<ILogService>(), PcVoiceAccess.CablePresent);
        Loaded += async (_, _) => await RefreshCableAsync();
        _hotspot = App.Services.GetRequiredService<PcHotspot>();
        Action onHotspot = () => DispatcherQueue.TryEnqueue(() => ShowHotspot());
        Loaded += (_, _) => { _hotspot.Changed += onHotspot; ShowHotspot(); };
        Unloaded += (_, _) => _hotspot.Changed -= onHotspot;
    }

    // ---- PC hotspot (LincLink step 6, by hand) ---------------------------------------

    private readonly PcHotspot _hotspot;
    private bool _hotspotBusy;

    private void ShowHotspot(string? message = null)
    {
        var on = _hotspot.Current != PcHotspot.Mode.Off;
        HotspotButton.Content = on ? "Stop PC hotspot" : "Start PC hotspot";
        HotspotButton.IsEnabled = !_hotspotBusy;
        HotspotStatus.Text = message ?? (_hotspot.Current switch
        {
            PcHotspot.Mode.WifiDirect => "On (Wi-Fi Direct).",
            PcHotspot.Mode.MobileHotspot when _hotspot.WasAlreadyOn => "On: Windows' Mobile Hotspot was already on, so Linc uses it (Stop leaves it on).",
            PcHotspot.Mode.MobileHotspot => "On (Windows' Mobile Hotspot, started by Linc).",
            _ => _hotspot.LastError ?? "Off",
        });
        HotspotDetails.Visibility = on && _hotspot.Network is not null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
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

    private async void OnHotspotClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
            AsyncVoidGuard.Report(ex, "SettingsPage.OnHotspotClick");
        }
    }

    /// <summary>The same question as the tab's X "Remove completely" (D-079).</summary>
    private async Task ConfirmRemoveAsync(KnownDeviceVm vm)
    {
        var dialog = new ContentDialog
        {
            Title = $"Remove {vm.Model}?",
            Content = $"Linc will unpair {vm.Model} and delete its saved copy on this PC. You'd pair it again to use it.\n\n" +
                "To just put it away, close its tab instead — it stays paired.",
            PrimaryButtonText = "Remove completely",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RemoveDevice(vm);
        }
    }

    // ---- phone as microphone (D-071) ----------------------------------------------

    private readonly VirtualCableSetup _cable;

    private async Task RefreshCableAsync(string? message = null)
    {
        bool present = await Task.Run(PcVoiceAccess.CablePresent); // COM: off the UI thread
        bool busy = _cable.IsBusy;
        CableStatus.Text = message ?? (busy ? "Setting up…" : present
            ? "Ready — the phone can be this PC's microphone."
            : "Not set up. Linc sets it up the first time you use your phone as the microphone, or here.");
        CableInstallButton.IsEnabled = !busy && !present;
        CableRemoveButton.IsEnabled = !busy && present;
    }

    private async void OnCableInstallClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Set up the phone as a microphone?",
                Content = $"Linc will download {VirtualCablePackage.DisplayName} {VirtualCablePackage.Version} from vb-audio.com, check it, and open VB-Audio's own installer: approve Windows' prompt, then click \"Install Driver\". VB-CABLE is donationware — see vb-audio.com for its terms.",
                PrimaryButtonText = "Set up",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            CableInstallButton.IsEnabled = false;
            CableStatus.Text = "Downloading VB-CABLE…";
            var progress = new Progress<int>(p => CableStatus.Text = p < 100
                ? $"Downloading VB-CABLE… {p} %"
                : "Approve Windows' prompt, then click Install Driver in VB-Audio's setup.");
            var result = await Task.Run(() => _cable.InstallAsync(progress));
            await RefreshCableAsync(result.Ok ? null : result.Message);
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SettingsPage.OnCableInstallClick");
        }
    }

    private async void OnCableRemoveClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            CableRemoveButton.IsEnabled = false;
            CableStatus.Text = "Approve Windows' prompt, then click Remove Driver in VB-Audio's setup.";
            var result = await Task.Run(() => _cable.UninstallAsync());
            await RefreshCableAsync(result.Message);
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SettingsPage.OnCableRemoveClick");
        }
    }

    /// <summary>
    /// "Clear history" confirmation gate (§2.4): the action must ask before deleting every stored
    /// notification. Matches the Files page delete dialog shape — the gate lives in the code-behind
    /// so the view model just runs the delete once the user confirms.
    /// </summary>
    private async void OnClearHistoryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Clear notification history?",
                Content = "Every stored notification, for all devices, will be deleted from this PC. This can't be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SettingsPage.OnClearHistoryClick");
        }
    }

    // ---- Connection Doctor -------------------------------------------------------------

    /// <summary>
    /// Runs every check, shows each result with its plain reason, and a Fix button where Linc can
    /// fix it; "Check again" re-runs them. Built in code: one list, no page of its own.
    /// </summary>
    private async void OnConnectionDoctorClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
                XamlRoot = Content.XamlRoot,
            };

            async Task RunAsync()
            {
                list.Children.Clear();
                list.Children.Add(new StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
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

            Microsoft.UI.Xaml.UIElement Row(DoctorFinding finding)
            {
                var (glyph, brush) = finding.Status switch
                {
                    DoctorStatus.Ok => ("", "SuccessBrush"),
                    DoctorStatus.Warning => ("", "WarningBrush"),
                    DoctorStatus.Problem => ("", "ErrorBrush"),
                    _ => ("", "OnSurfaceVariantBrush"),
                };
                var icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top, Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 0) };
                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(brush, out var b) && b is Microsoft.UI.Xaml.Media.Brush color)
                {
                    icon.Foreground = color;
                }
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock { Text = finding.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var detail = new TextBlock { Text = finding.Detail, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Opacity = 0.85 };
                text.Children.Add(detail);
                if (finding is { FixLabel: { } label, Fix: { } fix })
                {
                    var button = new Button { Content = label, Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0) };
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
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
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
                    AsyncVoidGuard.Report(ex, "SettingsPage.ConnectionDoctor");
                }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SettingsPage.OnConnectionDoctorClick");
        }
    }
}
