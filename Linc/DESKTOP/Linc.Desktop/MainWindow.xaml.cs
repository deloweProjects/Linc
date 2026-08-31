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
        ShowWindowCommand = new RelayCommand(() =>
        {
            AppWindow.Show();
            Activate();
        });
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
            "Sync" => typeof(SyncPage),
            "Details" => typeof(DetailsPage),
            "Logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        ContentFrame.Navigate(pageType);
    }
}
