using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;

namespace Linc.Desktop.Views;

/// <summary>
/// The Linc Bubble (Settings › Appearance › Linc Bubble, off by default; owner 2026-11-01: "allow it
/// to show up even on the home screen, allow placing it and it auto-hides; it's like a bubble with the
/// Linc icon, with some Linc settings and shortcuts when clicked, and it expands as the dynamic
/// island"). A small always-on-top window over every app:
///
/// - **Bubble**: the Linc icon in a circle, with a dot for the link. Drag it anywhere; let go and it
///   snaps to the nearest side of the screen. Left alone it tucks half into that edge and fades.
/// - **Island**: while something is live (a call, a file, music, mirroring) the bubble becomes the
///   island pill (<see cref="DynamicIsland"/>), which opens into its card when clicked.
/// - **Panel**: a click on the bubble opens Linc's shortcuts beside it; clicking elsewhere closes it.
///
/// The window is see-through outside what it draws (a transparent system backdrop) and clipped to
/// that shape (a window region), so nothing square ever shows around the bubble.
/// </summary>
public sealed class LincBubbleWindow : Window
{
    private const int BubbleSize = 52;
    private const int PanelWidth = 340;
    private const int Gap = 8;
    private static readonly TimeSpan TuckAfter = TimeSpan.FromSeconds(4);

    private enum Shape { Bubble, Island, Panel }

    private sealed record Place(bool Right, double YFraction);

    private readonly IDeviceRegistry _registry = App.Services.GetRequiredService<IDeviceRegistry>();
    private readonly IConnectionSupervisor _supervisor = App.Services.GetRequiredService<IConnectionSupervisor>();
    private readonly ILiveActivityService _live = App.Services.GetRequiredService<ILiveActivityService>();
    private readonly PcHotspot _hotspot = App.Services.GetRequiredService<PcHotspot>();

    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _bubble;
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 2) };
    private readonly DynamicIsland _island = new() { IgnoreSetting = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _panel;
    private readonly TextBlock _panelTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _panelStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _hotspotButton;
    private readonly Button _connectButton;
    private readonly DispatcherTimer _tuckTimer = new() { Interval = TuckAfter };
    private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromMilliseconds(15) };

    private Shape _shape = Shape.Bubble;
    private Place _place;
    private bool _tucked;
    private bool _pointerOver;
    private bool _pressed;
    private bool _dragged;
    private PointInt32 _pressCursor;
    private PointInt32 _pressWindow;
    private (PointInt32 From, PointInt32 To, DateTimeOffset Start)? _slideTarget;

    public LincBubbleWindow()
    {
        Title = "Linc Bubble";
        _place = LoadPlace();
        _bubble = BuildBubble();
        _panel = BuildPanel(out _hotspotButton, out _connectButton);
        _root.Children.Add(_panel);
        _root.Children.Add(_island);
        _root.Children.Add(_bubble);
        Content = _root;

        SystemBackdrop = new TransparentBackdrop();
        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        _tuckTimer.Tick += (_, _) =>
        {
            _tuckTimer.Stop();
            if (_shape == Shape.Bubble && !_pointerOver && !_pressed) Tuck(true);
        };
        _slide.Tick += (_, _) => SlideStep();
        _island.VisibilityChanged += () => DispatcherQueue.TryEnqueue(Layout);
        _island.BoundsChanged += () => DispatcherQueue.TryEnqueue(ClipToContent);
        _live.Changed += Layout;
        _supervisor.StateChanged += OnStateChanged;
        _hotspot.Changed += OnStateChanged;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && _shape == Shape.Panel) SetShape(Shape.Bubble);
        };
        Closed += (_, _) =>
        {
            _live.Changed -= Layout;
            _supervisor.StateChanged -= OnStateChanged;
            _hotspot.Changed -= OnStateChanged;
            _tuckTimer.Stop();
            _slide.Stop();
        };
        _root.Loaded += (_, _) =>
        {
            RefreshStatus();
            Layout();
        };
    }

    /// <summary>Show without taking focus from what the owner is doing.</summary>
    public void ShowQuietly()
    {
        AppWindow.Show(false);
        Layout();
    }

    // ---- pieces ----

    private Border BuildBubble()
    {
        var grid = new Grid { Width = BubbleSize, Height = BubbleSize };
        grid.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/linc.png")),
            Width = 30, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(_dot);
        var border = new Border
        {
            Width = BubbleSize, Height = BubbleSize,
            CornerRadius = new CornerRadius(BubbleSize / 2.0),
            Background = Theme("SurfaceContainerHighBrush"),
            BorderBrush = Theme("OutlineVariantBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = grid,
        };
        ToolTipService.SetToolTip(border, "Linc — click for shortcuts, drag to move");
        border.PointerEntered += (_, _) => { _pointerOver = true; _tuckTimer.Stop(); if (_tucked) Tuck(false); };
        border.PointerExited += (_, _) => { _pointerOver = false; ArmTuck(); };
        border.PointerPressed += OnPressed;
        border.PointerMoved += OnMoved;
        border.PointerReleased += OnReleased;
        border.PointerCaptureLost += (_, _) => _pressed = false;
        return border;
    }

    private Border BuildPanel(out Button hotspot, out Button connect)
    {
        var stack = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/linc.png")), Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center });
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        _panelTitle.Foreground = Theme("OnSurfaceBrush");
        _panelStatus.Foreground = Theme("OnSurfaceVariantBrush");
        titles.Children.Add(_panelTitle);
        titles.Children.Add(_panelStatus);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var close = RoundIcon("", "Close", () => SetShape(Shape.Bubble), 32);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        stack.Children.Add(header);

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var home = App.Services.GetRequiredService<HomeViewModel>();
        var main = App.MainWindowInstance as MainWindow;
        hotspot = Tile("", "Hotspot", () => _ = ToggleHotspotAsync());
        (string Glyph, string Label, Action Run)[] tiles =
        [
            ("", "Open Linc", () => main?.ShowPage(null)),
            ("", "Mirror", () => home.StartMirrorCommand.Execute(null)),
            ("", "Ring", () => home.RingPhoneCommand.Execute(null)),
            ("", "Screenshot", () => home.PullScreenshotCommand.Execute(null)),
            ("", "Send clipboard", () => main?.SendClipboardToPhone()),
            ("", "Files", () => main?.ShowPage("Files")),
            ("", "Share", () => main?.ShowPage("Share")),
        ];
        int i = 0;
        foreach (var (glyph, label, run) in tiles)
        {
            var tile = Tile(glyph, label, () =>
            {
                try { run(); } catch (Exception) { /* the feature reports its own failures */ }
                SetShape(Shape.Bubble);
            });
            Grid.SetColumn(tile, i % 4);
            Grid.SetRow(tile, i / 4);
            grid.Children.Add(tile);
            i++;
        }
        Grid.SetColumn(hotspot, i % 4);
        Grid.SetRow(hotspot, i / 4);
        grid.Children.Add(hotspot);
        stack.Children.Add(grid);

        connect = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(18),
            Height = 36,
            Content = "Connect now",
            Background = Theme("PrimaryBrush"),
            Foreground = Theme("OnPrimaryBrush"),
            BorderThickness = new Thickness(0),
        };
        connect.Click += (_, _) =>
        {
            if (_supervisor.State == LinkState.Paused) _supervisor.ResumeAutomatic();
            else _supervisor.NudgeReconnect("Linc Bubble");
            RefreshStatus();
        };
        stack.Children.Add(connect);

        return new Border
        {
            Width = PanelWidth,
            Padding = new Thickness(16, 14, 16, 16),
            CornerRadius = new CornerRadius(26),
            Background = Theme("SurfaceContainerHighBrush"),
            BorderBrush = Theme("OutlineVariantBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = stack,
        };
    }

    private static Button Tile(string glyph, string label, Action run)
    {
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 64,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = Theme("SecondaryContainerBrush"),
            Foreground = Theme("OnSecondaryContainerBrush"),
            Content = content,
            Padding = new Thickness(2),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => run();
        return button;
    }

    private static Button RoundIcon(string glyph, string label, Action run, double size)
    {
        var button = new Button
        {
            Width = size, Height = size, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = Theme("OnSurfaceVariantBrush"),
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => run();
        return button;
    }

    private static Brush Theme(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Colors.Gray);

    // ---- state ----

    private void OnStateChanged() => DispatcherQueue.TryEnqueue(RefreshStatus);

    private void RefreshStatus()
    {
        var model = _registry.KnownDevices.FirstOrDefault(d => d.Serial == _registry.PairedSerial)?.Model ?? "Your phone";
        var state = _supervisor.State;
        _panelTitle.Text = model;
        _panelStatus.Text = state switch
        {
            LinkState.Connected => "Connected",
            LinkState.Connecting => "Connecting…",
            LinkState.Searching => "Looking for the phone…",
            LinkState.Paused => "Paused",
            LinkState.NoDevice => "No phone paired yet",
            _ => state.ToString(),
        };
        _dot.Fill = state == LinkState.Connected ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x34, 0xC7, 0x59)) : Theme("OutlineBrush");
        _dot.Stroke = Theme("SurfaceContainerHighBrush");
        _connectButton.Visibility = state is LinkState.Searching or LinkState.Paused ? Visibility.Visible : Visibility.Collapsed;
        if (_hotspotButton.Content is StackPanel { Children: [_, TextBlock label] })
        {
            label.Text = _hotspot.Current == PcHotspot.Mode.Off ? "Hotspot" : "Hotspot on";
        }
        _hotspotButton.Background = Theme(_hotspot.Current == PcHotspot.Mode.Off ? "SecondaryContainerBrush" : "PrimaryBrush");
        _hotspotButton.Foreground = Theme(_hotspot.Current == PcHotspot.Mode.Off ? "OnSecondaryContainerBrush" : "OnPrimaryBrush");
    }

    private async Task ToggleHotspotAsync()
    {
        if (_hotspot.Current == PcHotspot.Mode.Off) await Task.Run(_hotspot.StartAsync);
        else await Task.Run(_hotspot.StopAsync);
        DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void SetShape(Shape shape)
    {
        if (_shape == shape) return;
        _shape = shape;
        Layout();
    }

    /// <summary>Size and place the window for the current shape, and clip it to what it draws.</summary>
    private void Layout()
    {
        if (_shape != Shape.Panel) _shape = _island.HasActivity || _live.Activities.Count > 0 ? Shape.Island : Shape.Bubble;
        if (_shape != Shape.Bubble && _tucked) Tuck(false);
        // While something is live the bubble stays at its spot with the island beside it (a click on
        // the bubble still opens the shortcuts; a click on the island opens its card).
        _bubble.Visibility = _shape != Shape.Panel ? Visibility.Visible : Visibility.Collapsed;
        _bubble.HorizontalAlignment = _place.Right && _shape == Shape.Island ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _island.Visibility = _shape == Shape.Island ? Visibility.Visible : Visibility.Collapsed;
        _panel.Visibility = _shape == Shape.Panel ? Visibility.Visible : Visibility.Collapsed;

        var scale = Scale();
        var area = WorkArea();
        int width, height;
        switch (_shape)
        {
            case Shape.Panel:
                _panel.Measure(new Windows.Foundation.Size(PanelWidth, double.PositiveInfinity));
                width = PanelWidth;
                height = (int)Math.Ceiling(_panel.DesiredSize.Height);
                break;
            case Shape.Island:
                width = 340 + BubbleSize + 6; // room for the island's expanded card; the region trims it to the pill
                height = 240;
                _island.HorizontalAlignment = _place.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                _island.Margin = _place.Right ? new Thickness(0, 10, BubbleSize + 6, 0) : new Thickness(BubbleSize + 6, 10, 0, 0);
                break;
            default:
                width = BubbleSize;
                height = BubbleSize;
                break;
        }
        var px = new SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
        // The shape's anchor is the bubble's spot on its side of the screen.
        var y = area.Y + (int)Math.Round(_place.YFraction * Math.Max(0, area.Height - BubbleSize * scale));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - px.Height));
        var margin = (int)(Gap * scale);
        var x = _place.Right ? area.X + area.Width - px.Width - margin : area.X + margin;
        if (_tucked && _shape == Shape.Bubble)
        {
            x = _place.Right ? area.X + area.Width - (int)(BubbleSize * scale * 0.42) : area.X - (int)(BubbleSize * scale * 0.58);
        }
        _slide.Stop();
        _slideTarget = null;
        AppWindow.MoveAndResize(new RectInt32(x, y, px.Width, px.Height));
        _root.Opacity = _tucked ? 0.6 : 1;
        ClipToContent();
        if (_shape == Shape.Bubble) ArmTuck(); else _tuckTimer.Stop();
    }

    private void ArmTuck()
    {
        _tuckTimer.Stop();
        if (_shape == Shape.Bubble && !_tucked) _tuckTimer.Start();
    }

    /// <summary>Tuck half into the screen edge (or come back out), sliding.</summary>
    private void Tuck(bool tuck)
    {
        if (_tucked == tuck) return;
        _tucked = tuck;
        var scale = Scale();
        var area = WorkArea();
        var pos = AppWindow.Position;
        var margin = (int)(Gap * scale);
        var size = (int)(BubbleSize * scale);
        var x = tuck
            ? (_place.Right ? area.X + area.Width - (int)(size * 0.42) : area.X - (int)(size * 0.58))
            : (_place.Right ? area.X + area.Width - size - margin : area.X + margin);
        _root.Opacity = tuck ? 0.6 : 1;
        _slideTarget = (pos, new PointInt32(x, pos.Y), DateTimeOffset.UtcNow);
        _slide.Start();
    }

    private void SlideStep()
    {
        if (_slideTarget is not { } t)
        {
            _slide.Stop();
            return;
        }
        var p = Math.Min(1, (DateTimeOffset.UtcNow - t.Start).TotalMilliseconds / 220);
        var eased = 1 - Math.Pow(1 - p, 3);
        AppWindow.Move(new PointInt32((int)(t.From.X + (t.To.X - t.From.X) * eased), (int)(t.From.Y + (t.To.Y - t.From.Y) * eased)));
        if (p >= 1)
        {
            _slide.Stop();
            _slideTarget = null;
        }
    }

    // ---- dragging ----

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressed = true;
        _dragged = false;
        GetCursorPos(out var cursor);
        _pressCursor = new PointInt32(cursor.X, cursor.Y);
        _pressWindow = AppWindow.Position;
        _bubble.CapturePointer(e.Pointer);
        if (_tucked) Tuck(false);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        GetCursorPos(out var cursor);
        int dx = cursor.X - _pressCursor.X, dy = cursor.Y - _pressCursor.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 6) return;
        _dragged = true;
        _slide.Stop();
        _slideTarget = null;
        AppWindow.Move(new PointInt32(_pressWindow.X + dx, _pressWindow.Y + dy));
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _bubble.ReleasePointerCapture(e.Pointer);
        if (!_dragged)
        {
            SetShape(Shape.Panel);
            RefreshStatus();
            Activate(); // so clicking elsewhere closes the panel
            return;
        }
        // Snap to the nearer side, keep the height.
        var area = WorkArea();
        var pos = AppWindow.Position;
        var size = (int)(BubbleSize * Scale());
        var right = pos.X + size / 2 > area.X + area.Width / 2;
        var fraction = Math.Clamp((pos.Y - area.Y) / (double)Math.Max(1, area.Height - size), 0, 1);
        _place = new Place(right, fraction);
        SavePlace(_place);
        Layout();
    }

    // ---- window plumbing ----

    private double Scale()
    {
        try
        {
            return GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private RectInt32 WorkArea()
    {
        var display = DisplayArea.GetFromPoint(AppWindow.Position, DisplayAreaFallback.Primary);
        return display.WorkArea;
    }

    /// <summary>Clip the window to what it draws, so nothing square ever shows around it.</summary>
    private void ClipToContent()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = Scale();
            var size = AppWindow.Size;
            IntPtr region;
            switch (_shape)
            {
                case Shape.Bubble:
                    var d = (int)Math.Ceiling(BubbleSize * scale) + 1;
                    region = CreateEllipticRgn(0, 0, d, d);
                    break;
                case Shape.Island:
                    var bd = (int)Math.Ceiling(BubbleSize * scale) + 1;
                    var bx = _place.Right ? size.Width - bd : 0;
                    region = CreateEllipticRgn(bx, 0, bx + bd, bd);
                    if (_island.IslandBounds(_root) is { } b)
                    {
                        var pill = CreateRoundRectRgn((int)(b.X * scale) - 1, (int)(b.Y * scale) - 1,
                            (int)Math.Ceiling((b.X + b.Width) * scale) + 2, (int)Math.Ceiling((b.Y + b.Height) * scale) + 2,
                            (int)(Math.Min(52, b.Height) * scale), (int)(Math.Min(52, b.Height) * scale));
                        CombineRgn(region, region, pill, 2); // RGN_OR
                        DeleteObject(pill);
                    }
                    break;
                default:
                    region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, (int)(52 * scale), (int)(52 * scale));
                    break;
            }
            SetWindowRgn(hwnd, region, true); // the window owns the region from here
        }
        catch (Exception)
        {
            // Without a region the transparent backdrop alone still hides the corners.
        }
    }

    private string PlacePath => System.IO.Path.Combine(_registry.RootPath, "bubble.json");

    private Place LoadPlace()
    {
        try
        {
            if (File.Exists(PlacePath) && JsonSerializer.Deserialize<Place>(File.ReadAllText(PlacePath)) is { } place) return place;
        }
        catch (Exception)
        {
        }
        return new Place(true, 0.62);
    }

    private void SavePlace(Place place)
    {
        try
        {
            File.WriteAllText(PlacePath, JsonSerializer.Serialize(place));
        }
        catch (Exception)
        {
            // Not saved: it comes back on the right next time. Nothing worth a message.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    /// <summary>A fully see-through window background (the system compositor draws nothing).</summary>
    private sealed class TransparentBackdrop : SystemBackdrop
    {
        private Windows.UI.Composition.Compositor? _compositor;

        protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            try
            {
                _compositor ??= new Windows.UI.Composition.Compositor();
                connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            }
            catch (Exception)
            {
                // No system compositor on this thread: the window region still gives the shape.
            }
        }

        protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            disconnectedTarget.SystemBackdrop = null;
        }
    }
}

/// <summary>Opens and closes the Linc Bubble with its setting. Started once from App.</summary>
public static class LincBubbleHost
{
    private static LincBubbleWindow? _window;

    public static void Begin()
    {
        var registry = App.Services.GetRequiredService<IDeviceRegistry>();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Apply()
        {
            if (registry.LiveBubble && _window is null)
            {
                try
                {
                    _window = new LincBubbleWindow();
                    _window.Closed += (_, _) => _window = null;
                    _window.ShowQuietly();
                }
                catch (Exception ex)
                {
                    App.Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, $"Linc Bubble couldn't open ({ex.Message}).");
                    _window = null;
                }
            }
            else if (!registry.LiveBubble && _window is { } open)
            {
                _window = null;
                open.Close();
            }
        }
        registry.AppearanceChanged += () => dispatcher.TryEnqueue(Apply);
        Apply();
    }
}
