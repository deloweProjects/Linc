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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;

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
            // ...nor the phone's screen resized by the mirror's Fill this monitor.
            Task.Run(() => App.Services.GetRequiredService<IMirrorService>().Dispose()).Wait(TimeSpan.FromSeconds(8));
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

        // The floating bar steps up out of the way of the Windows taskbar (see UpdateDockLift).
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) UpdateDockLift();
        };
        Activated += (_, _) => UpdateDockLift();
        Dock.SizeChanged += (_, _) => LayoutDock();

        // Add a phone sits over the pages on the app's backdrop: the page under it is hidden
        // rather than showing through (the owner: "Add a phone: plain white, no app backdrop").
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppShellViewModel.IsOnboarding)) ShowOnboarding();
        };
        ShowOnboarding();
    }

    private void ShowOnboarding() =>
        ContentFrame.Visibility = Shell.IsOnboarding ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Transparency (the owner: "the background is just dark, add transparency effects"): Mica —
    /// the desktop's wallpaper, blurred and tinted — behind the whole window, or Desktop Acrylic
    /// where Mica isn't available (Windows 10). The window's surface colour, which follows the
    /// phone's palette, is laid over it part-transparent, so the colours still come from the
    /// phone and the cards stay solid on top. Windows' own "Transparency effects" switch off turns
    /// both backdrops into a plain fill by themselves.
    /// <para>
    /// The phone's wallpaper on top of that (Settings › Appearance › Wallpaper as background), only
    /// lightly blurred (the owner: "the blur [is] too strong"): it is decoded at
    /// <see cref="SoftWallpaperWidth"/> px, so drawing it larger softens it a little rather than
    /// smearing it into a wash. A wide or maximised window shows the portrait wallpaper whole in
    /// the middle, its edges fading into the wallpaper's own colours (sampled from its left and
    /// right, plus two soft radial glows from its corners) across the sides; a narrower window
    /// fills with it. The theme's surface, part-transparent, tones all of it light or dark.
    /// </para>
    /// </summary>
    private void ApplyBackdrop()
    {
        try
        {
            bool mica = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
            if (mica) SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) SystemBackdrop = new DesktopAcrylicBackdrop();
            else return;
            if (Application.Current.Resources["SurfaceBrush"] is not SolidColorBrush surface || Content is not Grid root) return;

            var theme = App.Services.GetRequiredService<IThemeSyncService>();
            var registry = App.Services.GetRequiredService<IDeviceRegistry>();
            var cache = App.Services.GetRequiredService<IDeviceCacheService>();

            // Layers, back to front: Mica (the window) → the wallpaper (full-bleed, or centred with
            // its colours across the sides) → the theme's surface, part-transparent → the pages.
            var fill = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.85, IsHitTestVisible = false };

            var leftStop = new GradientStop { Offset = 0 };
            var midStop = new GradientStop { Offset = 0.5 };
            var rightStop = new GradientStop { Offset = 1 };
            var sideWash = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
            sideWash.GradientStops.Add(leftStop);
            sideWash.GradientStops.Add(midStop);
            sideWash.GradientStops.Add(rightStop);
            var (glowA, glowAStop) = Glow(new Windows.Foundation.Point(0.12, 0.22));
            var (glowB, glowBStop) = Glow(new Windows.Foundation.Point(0.88, 0.8));

            // The centred wallpaper fades to nothing at its sides through a mask, so no edge shows
            // (fading it into one average colour per side left a visible outline: the owner's "clear
            // edge of the portrait wallpaper").
            var portrait = new Border { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(portrait).Compositor;
            var portraitSurface = compositor.CreateSurfaceBrush();
            portraitSurface.Stretch = Microsoft.UI.Composition.CompositionStretch.UniformToFill;
            var edgeMask = compositor.CreateLinearGradientBrush();
            edgeMask.StartPoint = new System.Numerics.Vector2(0, 0.5f);
            edgeMask.EndPoint = new System.Numerics.Vector2(1, 0.5f);
            edgeMask.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.Transparent));
            edgeMask.ColorStops.Add(compositor.CreateColorGradientStop(0.3f, Microsoft.UI.Colors.White));
            edgeMask.ColorStops.Add(compositor.CreateColorGradientStop(0.7f, Microsoft.UI.Colors.White));
            edgeMask.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.Transparent));
            var maskedPortrait = compositor.CreateMaskBrush();
            maskedPortrait.Source = portraitSurface;
            maskedPortrait.Mask = edgeMask;
            var portraitVisual = compositor.CreateSpriteVisual();
            portraitVisual.Brush = maskedPortrait;
            portraitVisual.Opacity = 0.92f;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(portrait, portraitVisual);
            Windows.Storage.Streams.InMemoryRandomAccessStream? portraitStream = null;

            var sides = new Grid { Background = sideWash, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            sides.Children.Add(new Border { Background = glowA, Opacity = 0.75, IsHitTestVisible = false });
            sides.Children.Add(new Border { Background = glowB, Opacity = 0.75, IsHitTestVisible = false });
            sides.Children.Add(portrait);

            var tint = new SolidColorBrush(surface.Color);
            var veil = new Border { Background = tint, IsHitTestVisible = false };
            var backdrop = new Grid { IsHitTestVisible = false };
            backdrop.Children.Add(fill);
            backdrop.Children.Add(sides);
            backdrop.Children.Add(veil);
            Grid.SetRowSpan(backdrop, Math.Max(1, root.RowDefinitions.Count));
            root.Children.Insert(0, backdrop);
            root.Background = null;

            BitmapImage? soft = null;
            double aspect = 0; // width / height of the wallpaper; < 1 is portrait
            int generation = 0;

            // Which of the two arrangements this window size wants, and the portrait's width.
            void Arrange()
            {
                bool maximised = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                bool wide = soft is not null && aspect is > 0 and < 0.9 && (maximised || root.ActualWidth >= WideBackdropWidth);
                fill.Source = wide ? null : soft;
                fill.Visibility = soft is not null && !wide ? Visibility.Visible : Visibility.Collapsed;
                sides.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
                if (wide && root.ActualHeight > 0)
                {
                    // A little wider than the picture, so the faded sides overlap the colour wash.
                    portrait.Width = Math.Min(root.ActualWidth, root.ActualHeight * aspect * 1.35);
                    portraitVisual.Size = new System.Numerics.Vector2((float)portrait.Width, (float)root.ActualHeight);
                }
                // Light theme: a thinner white veil, or everything looks washed out.
                double veil = root.ActualTheme == ElementTheme.Light ? 0.72 : 1.0;
                bool plain = !registry.WallpaperBackdrop;
                // Wallpaper as background off: the plain look from before it (the owner: "use that
                // prev normal background"): the theme's own colour, solid, and solid cards.
                tint.Opacity = plain ? 1.0 : veil * (soft is null ? (mica ? 0.55 : 0.7) : wide ? 0.5 : 0.58);
                ToneSurfaces(root.ActualTheme == ElementTheme.Light, plain);
            }

            async Task LoadAsync()
            {
                int mine = ++generation;
                var serial = registry.PairedSerial;
                byte[]? bytes = null;
                if (registry.WallpaperBackdrop && serial is not null && theme.Wallpaper is not null)
                {
                    bytes = await Task.Run(() => cache.LoadWallpaper(serial));
                }
                if (bytes is not { Length: > 0 })
                {
                    if (mine != generation) return;
                    soft = null;
                    Arrange();
                    return;
                }
                var sample = await SampleWallpaperAsync(bytes);
                var image = new BitmapImage { DecodePixelWidth = SoftWallpaperWidth };
                using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                    stream.Seek(0);
                    await image.SetSourceAsync(stream);
                }
                if (mine != generation) return; // a newer wallpaper or setting won
                var portraitBytes = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await portraitBytes.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                portraitBytes.Seek(0);
                portraitStream?.Dispose();
                portraitStream = portraitBytes; // kept while the surface loads from it
                // Decoded small and drawn large: the same soft blur as the full-bleed fill.
                portraitSurface.Surface = LoadedImageSurface.StartLoadFromStream(portraitBytes,
                    new Windows.Foundation.Size(SoftWallpaperWidth * 2, SoftWallpaperWidth * 4));
                soft = image;
                aspect = sample?.Aspect ?? 0;
                if (sample is { } colours)
                {
                    leftStop.Color = colours.Left;
                    midStop.Color = colours.Middle;
                    rightStop.Color = colours.Right;
                    glowAStop.Color = colours.TopLeft;
                    glowBStop.Color = colours.BottomRight;
                }
                Arrange();
            }

            async void Reload()
            {
                try
                {
                    await LoadAsync();
                }
                catch (Exception ex)
                {
                    App.Services.GetRequiredService<ILogService>().Log(LogLevel.Info, $"The wallpaper background couldn't be drawn ({ex.GetType().Name}: {ex.Message})");
                }
            }

            Arrange();
            Reload();
            root.SizeChanged += (_, _) => Arrange();
            root.ActualThemeChanged += (_, _) => Arrange();
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidPresenterChange) Arrange();
            };
            surface.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => tint.Color = surface.Color);
            theme.WallpaperChanged += Reload;
            registry.AppearanceChanged += () => DispatcherQueue.TryEnqueue(Reload);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, $"Window transparency isn't available ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>How wide the backdrop's wallpaper is decoded: drawn larger than this, it is softly blurred.</summary>
    private const int SoftWallpaperWidth = 180;

    /// <summary>From this window width (or maximised) the wallpaper sits whole in the middle.</summary>
    private const double WideBackdropWidth = 1400;

    /// <summary>A soft round glow of one colour, fading out, for the backdrop's sides.</summary>
    private static (RadialGradientBrush Brush, GradientStop Inner) Glow(Windows.Foundation.Point centre)
    {
        var inner = new GradientStop { Offset = 0 };
        var brush = new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = 0.55,
            RadiusY = 0.7,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.GradientStops.Add(inner);
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Microsoft.UI.Colors.Transparent });
        return (brush, inner);
    }

    /// <summary>
    /// Cards a touch more see-through in light theme: at dark theme's opacity, near-white cards
    /// over a white veil read as a wall of white (the owner: "reduce whiteness of stuff a lil bit").
    /// </summary>
    private static void ToneSurfaces(bool light, bool solid)
    {
        var resources = Application.Current.Resources;
        if (resources["SurfaceContainerBrush"] is SolidColorBrush card) card.Opacity = solid ? 1 : light ? 0.66 : 0.78;
        if (resources["SurfaceContainerHighBrush"] is SolidColorBrush high) high.Opacity = solid ? 1 : light ? 0.74 : 0.86;
        if (resources["FrostedSurfaceBrush"] is AcrylicBrush frost) frost.TintOpacity = solid ? 0.9 : light ? 0.42 : 0.55;
        // Overlays (flyouts, menus, dialogs, the side rail): frosted glass in the theme's colour
        // (ThemeSyncService retints it), a little denser over the plain look's solid cards.
        double overlay = light ? (solid ? 0.72 : 0.6) : (solid ? 0.8 : 0.7);
        foreach (var key in ThemeSyncService.OverlayBrushKeys)
        {
            switch (resources[key])
            {
                case AcrylicBrush acrylic:
                    acrylic.TintOpacity = overlay;
                    break;
                case SolidColorBrush glaze:
                    glaze.Opacity = overlay;
                    break;
            }
        }
    }

    private sealed record WallpaperColours(
        Windows.UI.Color Left, Windows.UI.Color Middle, Windows.UI.Color Right,
        Windows.UI.Color TopLeft, Windows.UI.Color BottomRight, double Aspect);

    /// <summary>
    /// The wallpaper's ambient colours: the decoder shrinks it to 3×3 (averaging as it goes), so
    /// each cell is the mean colour of a ninth of the picture. Null when it can't be decoded.
    /// </summary>
    private static async Task<WallpaperColours?> SampleWallpaperAsync(byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = 3,
                ScaledHeight = 3,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();
            if (px.Length < 9 * 4) return null;
            Windows.UI.Color Cell(int i) => Windows.UI.Color.FromArgb(255, px[i * 4 + 2], px[i * 4 + 1], px[i * 4]);
            Windows.UI.Color Mean(int a, int b, int c)
            {
                Windows.UI.Color x = Cell(a), y = Cell(b), z = Cell(c);
                return Windows.UI.Color.FromArgb(255, (byte)((x.R + y.R + z.R) / 3), (byte)((x.G + y.G + z.G) / 3), (byte)((x.B + y.B + z.B) / 3));
            }
            double aspect = decoder.OrientedPixelHeight > 0 ? decoder.OrientedPixelWidth / (double)decoder.OrientedPixelHeight : 0;
            return new WallpaperColours(Mean(0, 3, 6), Mean(1, 4, 7), Mean(2, 5, 8), Cell(0), Cell(8), aspect);
        }
        catch (Exception)
        {
            return null; // cosmetic only
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
            Height = 48,
            MinWidth = 48,
            Padding = new Thickness(14, 0, 14, 0),
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
        button.Click += (_, _) =>
        {
            Shell.IsOnboarding = false; // the bar leaves Add a phone, like its Cancel
            Nav.SelectedItem = item;
        };
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
        // LeftMinimal while the bar is in use: a hidden pane in Left/Auto mode can still keep its
        // column, which pushes every centred page to the right.
        Nav.PaneDisplayMode = floating ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Auto;
        Nav.IsPaneToggleButtonVisible = !floating;
        Nav.IsPaneVisible = !floating;
        Dock.Visibility = floating ? Visibility.Visible : Visibility.Collapsed;
        LayoutDock();
        UpdateDockLift();
    }

    // ---- The floating bar's place: just above the window's bottom, clear of the taskbar ----

    /// <summary>Window bottom (or the taskbar's top) to the bar.</summary>
    private const double DockGap = 8;

    /// <summary>The bar's top to the end of the page above it.</summary>
    private const double DockClearance = 4;

    /// <summary>How far an auto-hide taskbar reaches up when it slides in (Windows 11: 48).</summary>
    private const double AutoHideTaskbarLift = 48;

    /// <summary>How far the bar is raised so the Windows taskbar doesn't cover it, in DIPs.</summary>
    private double _dockLift;

    /// <summary>
    /// The band kept free under the pages is the bar's own height plus a small gap (it used to be
    /// a fixed 92 px, and pages stopped well above the bar: the owner's "far too tall").
    /// </summary>
    private void LayoutDock()
    {
        bool floating = Dock.Visibility == Visibility.Visible;
        Dock.Margin = new Thickness(16, 0, 16, DockGap + _dockLift);
        double height = Dock.ActualHeight > 0 ? Dock.ActualHeight : 62;
        ContentFrame.Margin = floating
            ? new Thickness(0, 0, 0, height + DockGap + DockClearance + _dockLift)
            : new Thickness(0);
    }

    /// <summary>
    /// The Windows taskbar sits on top of every window. Where it overlaps this window's bottom —
    /// the window dragged down under it, or maximised with an auto-hide taskbar that slides in
    /// over it — the bar is lifted by that much, so the taskbar never covers it. The overlap is
    /// the window's client bottom against its monitor's work area (the monitor minus the
    /// taskbar); an auto-hide taskbar leaves the work area whole, so that case asks the shell
    /// (SHAppBarMessage) whether the taskbar auto-hides along the bottom.
    /// </summary>
    private void UpdateDockLift()
    {
        double lift = 0;
        bool watchTaskbar = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && GetClientRect(hwnd, out var client))
            {
                var corner = new POINT { X = 0, Y = client.Bottom };
                ClientToScreen(hwnd, ref corner);
                int bottom = Math.Min(corner.Y, info.rcMonitor.Bottom);
                double scale = Content?.XamlRoot?.RasterizationScale is { } s and > 0 ? s : 1;
                bool maximised = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                if (info.rcWork.Bottom < info.rcMonitor.Bottom && bottom > info.rcWork.Bottom)
                {
                    lift = (bottom - info.rcWork.Bottom) / scale; // a docked taskbar along the bottom
                }
                else if (info.rcWork.Bottom >= info.rcMonitor.Bottom
                         && (maximised || bottom >= info.rcMonitor.Bottom - 2)
                         && TaskbarAutoHidesAtBottom())
                {
                    // Only while it is actually showing: an auto-hide taskbar slides in and out, and
                    // a bar lifted all the time sat far too high (the owner: "supposed to be
                    // reactive, go high when the taskbar is open, then lower"). Watched while it
                    // can appear.
                    watchTaskbar = true;
                    if (FindWindow("Shell_TrayWnd", null) is var tray && tray != IntPtr.Zero
                        && GetWindowRect(tray, out var bar) && bar.Top < info.rcMonitor.Bottom - 6)
                    {
                        lift = Math.Min(Math.Max(0, bottom - bar.Top) / scale, AutoHideTaskbarLift + 24);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            lift = 0; // no shell to ask: leave the bar where it is
        }
        WatchTaskbar(watchTaskbar);
        if (Math.Abs(lift - _dockLift) < 0.5) return;
        _dockLift = lift;
        LayoutDock();
    }

    private DispatcherTimer? _taskbarWatch;

    /// <summary>Checks the auto-hide taskbar a few times a second while it could cover the bar.</summary>
    private void WatchTaskbar(bool on)
    {
        if (on)
        {
            if (_taskbarWatch is null)
            {
                _taskbarWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _taskbarWatch.Tick += (_, _) => UpdateDockLift();
            }
            if (!_taskbarWatch.IsEnabled) _taskbarWatch.Start();
        }
        else
        {
            _taskbarWatch?.Stop();
        }
    }

    private static bool TaskbarAutoHidesAtBottom()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        bool autoHide = ((uint)SHAppBarMessage(AbmGetState, ref data) & AbsAutoHide) != 0;
        if (!autoHide) return false;
        data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(AbmGetTaskbarPos, ref data) == UIntPtr.Zero || data.uEdge == AbeBottom;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint AbmGetState = 4, AbmGetTaskbarPos = 5, AbsAutoHide = 1, AbeBottom = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
            "Tools" => typeof(ToolsPage),
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
