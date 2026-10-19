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
    public RelayCommand SendFilesCommand { get; }
    public RelayCommand SendClipboardCommand { get; }
    public RelayCommand PasteAndSendCommand { get; }
    public RelayCommand TogglePauseCommand { get; }

    private System.Drawing.Icon? _trayConnected;
    private System.Drawing.Icon? _trayDisconnected;

    public MainWindow()
    {
        Shell = App.Services.GetRequiredService<AppShellViewModel>();
        Tabs = App.Services.GetRequiredService<DeviceTabsViewModel>();
        // "+" opens the guided wizard (M2b): it walks the user through developer options, detects
        // the phone, installs the companion and saves it as a tab. This replaces M2a's "navigate to
        // the Device page and start pairing", so the whole first-run story is one consistent flow.
        Tabs.AddDeviceRequested += () => Shell.IsOnboarding = true;
        Tabs.CloseRequested += tab => _ = ConfirmCloseTabAsync(tab);
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
        SendFilesCommand = new RelayCommand(() => _ = PickAndSendAsync());
        SendClipboardCommand = new RelayCommand(() => _ = SendClipboardAsync(pasteFiles: false));
        PasteAndSendCommand = new RelayCommand(() => _ = SendClipboardAsync(pasteFiles: true));
        TogglePauseCommand = new RelayCommand(() => Shell.TogglePause());
        ExitCommand = new RelayCommand(() =>
        {
            // Never leave the phone's virtual cable as the PC's default microphone (D-071).
            var log = App.Services.GetRequiredService<ILogService>();
            Task.Run(() => PcVoiceAccess.RestoreAllMics(log)).Wait(TimeSpan.FromSeconds(3));
            // ...nor the display scaled up by Tablet mode (D-074).
            Task.Run(() => PcTabletMode.RestorePending(log)).Wait(TimeSpan.FromSeconds(3));
            TrayIcon.Dispose();
            Application.Current.Exit();
        });

        InitializeComponent();
        try
        {
            // M14: the tray reads the mono variant (white L on transparency) — an opaque
            // black tile becomes a heavy black box on a light Windows 11 taskbar. The exe and
            // the window/taskbar keep the square AppIcon.ico.
            _trayConnected = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIconMono.ico"));
            _trayDisconnected = TrayBadge.Disconnected(_trayConnected);
            SetTrayIcon(Shell.IsConnected);
            Shell.TrayStateChanged += connected => DispatcherQueue.TryEnqueue(() => SetTrayIcon(connected));
            TrayIcon.ForceCreate();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Error, $"MainWindow: tray icon setup failed; continuing without a tray icon. ({ex.GetType().Name}: {ex.Message})");
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Modern title bar: the chrome joins the app's design (M16 polish).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // Live-retint the app to match the phone's Material You palette once connected.
        App.Services.GetRequiredService<IThemeSyncService>().Start((FrameworkElement)Content);
        ApplyBackdrop();
        App.Services.GetRequiredService<IDeviceRegistry>().AppearanceChanged += () => DispatcherQueue.TryEnqueue(ApplyNavStyle);

        // Closing the window hides Linc to the tray; it keeps watching for the phone.
        AppWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>
    /// Transparency (the owner: "the background is just dark, add transparency effects"): Mica —
    /// the desktop's wallpaper, blurred and tinted — behind the whole window, or Desktop Acrylic
    /// where Mica isn't available (Windows 10). The window's surface colour, which follows the
    /// phone's palette, is laid over it part-transparent, so the colours still come from the
    /// phone and the cards stay solid on top. Windows' own "Transparency effects" switch off turns
    /// both backdrops into a plain fill by themselves.
    /// </summary>
    private void ApplyBackdrop()
    {
        try
        {
            bool mica = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
            if (mica) SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            else return;
            if (Application.Current.Resources["SurfaceBrush"] is not Microsoft.UI.Xaml.Media.SolidColorBrush surface || Content is not Grid root) return;

            // Layers, back to front: Mica (the window) → the phone's wallpaper, blurred (an
            // ambient glow of the phone, the owner's ask) → the theme's surface, part-transparent
            // → the pages, whose cards are themselves a little see-through.
            var theme = App.Services.GetRequiredService<IThemeSyncService>();
            var registry = App.Services.GetRequiredService<IDeviceRegistry>();
            var ambient = new Image
            {
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Opacity = 0.9,
                IsHitTestVisible = false,
            };
            var tint = new Microsoft.UI.Xaml.Media.SolidColorBrush(surface.Color);
            // Settings › Appearance › Wallpaper as background: off leaves Mica and the tint.
            void Show()
            {
                var wallpaper = registry.WallpaperBackdrop ? theme.AmbientWallpaper : null;
                ambient.Source = wallpaper;
                tint.Opacity = wallpaper is null ? (mica ? 0.55 : 0.7) : 0.62;
            }
            Show();
            var veil = new Border { Background = tint, IsHitTestVisible = false };
            var backdrop = new Grid { IsHitTestVisible = false };
            backdrop.Children.Add(ambient);
            backdrop.Children.Add(veil);
            Grid.SetRowSpan(backdrop, Math.Max(1, root.RowDefinitions.Count));
            root.Children.Insert(0, backdrop);
            root.Background = null;
            surface.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Media.SolidColorBrush.ColorProperty, (_, _) => tint.Color = surface.Color);
            theme.WallpaperChanged += Show;
            registry.AppearanceChanged += () => DispatcherQueue.TryEnqueue(Show);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, $"Window transparency isn't available ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Show the connected or disconnected tray icon. H.NotifyIcon DISPOSES the previous Icon each
    /// time Icon changes (TaskbarIcon.OnIconChanged), so it gets a fresh copy every time: handing it
    /// our cached one meant the first connect destroyed the disconnected icon, and the next drop
    /// set a disposed icon — an exception inside a DispatcherQueue callback, which WinUI turns into
    /// a fail-fast (0xC000027B) that closed Linc on every disconnect (1.0.0.11, D-090). A tray
    /// problem must never close the app either way, hence the catch.
    /// </summary>
    private void SetTrayIcon(bool connected)
    {
        try
        {
            if ((connected ? _trayConnected : _trayDisconnected) is { } icon)
                TrayIcon.Icon = (System.Drawing.Icon)icon.Clone();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, $"Couldn't update the tray icon ({ex.GetType().Name}: {ex.Message})");
        }
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
    /// <summary>
    /// Explorer's "Send to phone (Linc)" and the tray's Send to phone: over the link when the phone
    /// is connected, quietly; otherwise the Share page opens with them ready for Quick Share.
    /// </summary>
    public void SendToPhone(IReadOnlyList<string> paths)
    {
        var share = App.Services.GetRequiredService<ShareViewModel>();
        if (share.SendFiles(paths))
        {
            Notify("Sending to your phone", paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : $"{paths.Count} files");
        }
        else
        {
            ShowSharePage();
        }
    }

    private async Task PickAndSendAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            SendToPhone(files.Select(f => f.Path).ToList());
        }
    }

    /// <summary>
    /// Tray → Send to phone. "Clipboard text": what's on this PC's clipboard goes on the phone's.
    /// "Paste &amp; send": whatever is on the clipboard goes — copied files are sent, a link opens on
    /// the phone, other text lands on its clipboard.
    /// </summary>
    private async Task SendClipboardAsync(bool pasteFiles)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (pasteFiles && content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                SendToPhone(items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList());
                return;
            }
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                Notify("Nothing to send", "The clipboard has no text or files.");
                return;
            }
            var text = (await content.GetTextAsync()).Trim();
            var connection = App.Services.GetRequiredService<IConnectionManager>();
            if (pasteFiles && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                await connection.ContinueUrlAsync(uri.AbsoluteUri, CancellationToken.None);
                Notify("Opening on your phone", uri.Host);
                return;
            }
            var sent = await App.Services.GetRequiredService<IClipboardSyncService>().SendToPhoneAsync(text);
            Notify(sent ? "On your phone's clipboard" : "Your phone isn't connected", sent ? (text.Length > 60 ? text[..60] + "…" : text) : "Linc will send things again once it reconnects.");
        }
        catch (Exception ex) when (ex is LincException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Notify("Couldn't send", ex is LincException ? ex.Message : "Another app has the clipboard open — try again.");
        }
    }

    private static void Notify(string title, string body)
    {
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(
                new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder().AddText(title).AddText(body).BuildNotification());
        }
        catch (Exception)
        {
            // Notifications unavailable; nothing else to tell.
        }
    }

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

    /// <summary>
    /// The tab X (D-079): say what closing does before doing it. Disconnect and close keeps the
    /// pairing; Remove forgets the phone; Cancel does nothing.
    /// </summary>
    private async Task ConfirmCloseTabAsync(DeviceTabViewModel tab)
    {
        bool connected = Tabs.IsConnected(tab);
        var dialog = new ContentDialog
        {
            Title = $"Close {tab.Model}?",
            Content = (connected ? $"Linc will disconnect from {tab.Model}. " : "") +
                $"It stays paired: bring it back any time with + → \"Phones you've used before\" — no pairing needed.\n\n" +
                "Remove it instead if you don't want it on this PC any more (you'd pair it again to use it).",
            PrimaryButtonText = connected ? "Disconnect and close" : "Close",
            SecondaryButtonText = "Remove completely",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                Tabs.DisconnectAndClose(tab);
                break;
            case ContentDialogResult.Secondary:
                Tabs.Remove(tab);
                break;
        }
    }

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        BuildDock();
        ApplyNavStyle();
        ContentFrame.Navigate(typeof(HomePage));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private readonly List<(Button Button, object Item, TextBlock Label)> _dock = [];

    /// <summary>
    /// The floating bar's buttons, one per page in Nav (plus Settings after a divider). Clicking
    /// one selects that page in Nav, so navigation stays in one place: OnNavSelectionChanged.
    /// </summary>
    private void BuildDock()
    {
        var pages = Nav.MenuItems.OfType<NavigationViewItem>()
            .Select(i => (Item: (object)i, Name: i.Content as string ?? "", Glyph: (i.Icon as FontIcon)?.Glyph ?? "\uE80F"))
            .ToList();
        foreach (var (item, name, glyph) in pages)
        {
            AddDockButton(item, name, glyph);
        }
        DockItems.Children.Add(new Border
        {
            Width = 1,
            Height = 28,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["OutlineVariantBrush"],
        });
        AddDockButton(Nav.SettingsItem, "Settings", "\uE713");
    }

    private void AddDockButton(object item, string name, string glyph)
    {
        var label = new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var button = new Button
        {
            Height = 52,
            MinWidth = 52,
            Padding = new Thickness(15, 0, 15, 0),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { new FontIcon { Glyph = glyph, FontSize = 20 }, label },
            },
        };
        ToolTipService.SetToolTip(button, name);
        ToolTipService.SetPlacement(button, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Nav.SelectedItem = item;
        DockItems.Children.Add(button);
        _dock.Add((button, item, label));
    }

    /// <summary>The open page's button is filled and shows its name; the rest are icons.</summary>
    private void ShowDockSelection(object? selected)
    {
        var res = Application.Current.Resources;
        foreach (var (button, item, label) in _dock)
        {
            bool on = ReferenceEquals(item, selected);
            button.Background = on ? (Microsoft.UI.Xaml.Media.Brush)res["PrimaryContainerBrush"] : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = (Microsoft.UI.Xaml.Media.Brush)res[on ? "OnPrimaryContainerBrush" : "OnSurfaceBrush"];
            label.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Settings › Appearance › Navigation: the floating bar (default) or the side rail.</summary>
    private void ApplyNavStyle()
    {
        bool floating = App.Services.GetRequiredService<IDeviceRegistry>().FloatingNav;
        Nav.IsPaneVisible = !floating;
        Dock.Visibility = floating ? Visibility.Visible : Visibility.Collapsed;
        // Room under the pages so the bar never covers the end of one.
        ContentFrame.Margin = floating ? new Thickness(0, 0, 0, 92) : new Thickness(0);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        ShowDockSelection(sender.SelectedItem);
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
            "Clipboard" => typeof(ClipboardPage),
            "Share" => typeof(QuickSharePage),
            "Sync" => typeof(SyncPage),
            "Details" => typeof(DetailsPage),
            "Logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        ContentFrame.Navigate(pageType);
    }
}
