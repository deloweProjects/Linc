using AdvancedSharpAdbClient.Models;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Linc.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop;

public sealed partial class MainWindow : Window
{
    public AppShellViewModel Shell { get; }
    public DeviceTabsViewModel Tabs { get; }
    public RelayCommand ShowWindowCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ShowShareCommand { get; }

    public MainWindow()
    {
        Shell = App.Services.GetRequiredService<AppShellViewModel>();
        Tabs = App.Services.GetRequiredService<DeviceTabsViewModel>();
        // "+" opens the guided wizard (M2b): it walks the user through developer options, detects
        // the phone, installs the companion and saves it as a tab. This replaces M2a's "navigate to
        // the Device page and start pairing", so the whole first-run story is one consistent flow.
        Tabs.AddDeviceRequested += () => Shell.IsOnboarding = true;
        // M12c-amend Part C: the non-wizard install-confirmation gate. MainWindow's Content is
        // the one XamlRoot that outlives whichever page is on screen, so the dialog can show
        // regardless of which page a background reconnect happens to interrupt.
        Shell.InstallConfirmationRequested += device => _ = OnInstallConfirmationRequestedAsync(device);
        // Quick Share offers can arrive on any page (or with the window hidden, where the toast
        // carries the Accept/Decline buttons instead). Same reasoning as the install gate above.
        App.Services.GetRequiredService<QuickShare.IQuickShareService>().IncomingOffer += offer =>
            DispatcherQueue.TryEnqueue(() => _ = OnQuickShareOfferAsync(offer));
        ShowWindowCommand = new RelayCommand(() =>
        {
            AppWindow.Show();
            Activate();
        });
        ShowShareCommand = new RelayCommand(ShowSharePage);
        ExitCommand = new RelayCommand(() =>
        {
            TrayIcon.Dispose();
            Application.Current.Exit();
        });

        InitializeComponent();
        try
        {
            // M14: the tray reads the mono variant (white L on transparency) — an opaque
            // black tile becomes a heavy black box on a light Windows 11 taskbar. The exe and
            // the window/taskbar keep the square AppIcon.ico.
            TrayIcon.Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIconMono.ico"));
            TrayIcon.ForceCreate();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Error, $"MainWindow: tray icon setup failed; continuing without a tray icon. ({ex.GetType().Name}: {ex.Message})");
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Modern title bar: the chrome joins the app's design (M16 polish).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Live-retint the app to match the phone's Material You palette once connected.
        App.Services.GetRequiredService<IThemeSyncService>().Start((FrameworkElement)Content);


        // Closing the window hides Linc to the tray; it keeps watching for the phone.
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        e.Cancel = true;
        sender.Hide();
    }

    /// <summary>
    /// M12c-amend C2.1/C2.2: the non-wizard companion-install confirmation. Matches
    /// SettingsPage.OnClearHistoryClick's shape — the gate lives here in code-behind, the
    /// service just runs the action once confirmed. Never awaited by the caller (C2.5): the
    /// connect that triggered this has already moved on without the companion installed.
    /// </summary>
    private async Task OnInstallConfirmationRequestedAsync(DeviceData device)
    {
        var dialog = new ContentDialog
        {
            Title = "Install the Linc app on your phone?",
            Content = $"The Linc app isn't on {device.Model ?? device.Serial ?? "this phone"} yet. " +
                "Linc can install it now — your phone may show its own install prompt. " +
                "If you skip this, the phone just won't get Linc's features until you install it another way.",
            PrimaryButtonText = "Install",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var setup = App.Services.GetRequiredService<IPhoneSetupService>();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await setup.ConfirmInstallAsync(device, CancellationToken.None);
        }
        else
        {
            setup.DeclineInstall(device);
        }
    }

    /// <summary>
    /// The in-window Accept/Decline for a Quick Share offer, showing the PIN so the person can
    /// check it matches the phone. Answering on the toast closes this; answering here removes
    /// the toast (the service does that once the decision lands).
    /// </summary>
    private async Task OnQuickShareOfferAsync(QuickShare.QsIncomingOffer offer)
    {
        if (!AppWindow.IsVisible || Content?.XamlRoot is null)
        {
            return; // the toast is the prompt while Linc sits in the tray
        }
        var items = string.Join(Environment.NewLine, offer.Items.Take(6)) +
            (offer.Items.Count > 6 ? $"{Environment.NewLine}…and {offer.Items.Count - 6} more" : "");
        var dialog = new ContentDialog
        {
            Title = $"{offer.SenderName} wants to share with you",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = items, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = $"PIN {offer.Pin}",
                        FontSize = 28,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = offer.FromKnownPhone
                            ? "This looks like one of your phones. Check the PIN matches the one on its screen."
                            : "Only accept if the PIN matches the one on the sender's screen.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                },
            },
            PrimaryButtonText = "Accept",
            CloseButtonText = "Decline",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        _ = offer.Decided.ContinueWith(_ => DispatcherQueue.TryEnqueue(dialog.Hide), TaskScheduler.Default);
        try
        {
            var result = await dialog.ShowAsync();
            if (offer.Decided.IsCompleted)
            {
                return; // answered on the toast; the dialog was only closed to match
            }
            if (result == ContentDialogResult.Primary)
            {
                offer.Accept();
            }
            else
            {
                offer.Decline();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open (WinUI allows one at a time); the toast still asks.
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Info,
                $"Quick Share prompt shown as a notification only: {ex.Message}");
        }
    }

    /// <summary>Bring the window up on the Share page (tray item, Explorer hand-off).</summary>
    public void ShowSharePage() => DispatcherQueue.TryEnqueue(() =>
    {
        AppWindow.Show();
        Activate();
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == "Share");
        if (item is not null)
        {
            Nav.SelectedItem = item; // OnNavSelectionChanged does the navigation
        }
    });

    // The lone "pair a phone" button that stands in for the tab strip when no phone is paired
    // (M2b zero-device entry). Same destination as the strip's "+" and Home's CTA.
    private void OnPairPhone(object sender, RoutedEventArgs e) => Shell.IsOnboarding = true;

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(HomePage));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        var tag = (e.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        var pageType = tag switch
        {
            "Home" => typeof(HomePage),
            "Device" => typeof(DevicePage),
            "Files" => typeof(FilesPage),
            "Share" => typeof(QuickSharePage),
            "Sync" => typeof(SyncPage),
            "Details" => typeof(DetailsPage),
            "Logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        ContentFrame.Navigate(pageType);
    }
}
