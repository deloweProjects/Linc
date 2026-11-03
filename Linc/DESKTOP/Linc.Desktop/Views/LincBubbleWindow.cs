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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace Linc.Desktop.Views;

/// <summary>
/// The Linc Bubble (Settings › Appearance › Linc Bubble, off by default; owner 2026-11-01: "a bubble
/// with the Linc icon … it expands as the dynamic island … auto-hides like the dynamic island does").
/// One shape over every app, growing out of the bubble and back into it:
///
/// - <b>Bubble</b>: the Linc icon in a circle; a ring for the link, a dot while something is live.
///   Left alone it tucks half into the screen edge. Drag it anywhere; it snaps to the nearer side.
/// - <b>Pill</b> (the island): something new happens (a song, a file, a call) and the bubble
///   stretches into a pill saying what, for a few seconds, then shrinks back by itself. Hovering the
///   bubble peeks the pill out again.
/// - <b>Card</b>: a click on the pill opens the activity's card (art, progress, its buttons); a
///   click on the bubble with nothing live opens Linc's shortcuts. Both close when the pointer leaves.
///
/// The window is a fixed transparent canvas that never takes focus; a window region follows the
/// shape every frame, so nothing square ever shows and clicks outside the shape go to the app below.
/// </summary>
public sealed class LincBubbleWindow : Window
{
    // Settings › Appearance › Bubble size (owner, 2026-11-02: "make it smaller ... allow to reduce its size").
    private double BubbleSize;
    // One rim for every shape: the link's colour around the bubble, a quiet outline around cards.
    private const double Rim = 2;
    private double FaceSize => BubbleSize - Rim * 2;
    // Idle and tucked: a slim tab flush with the screen edge instead of half a bubble sticking out
    // (owner, 2026-11-02: "I don't want to see that black protrude").
    private const double TabWidth = 6;
    private double TabHeight => Math.Round(BubbleSize * 0.9);
    private const double CardWidth = 340;
    private const double PillMaxWidth = 300;
    private const double Margin = 10;
    // Room past the card for its spring overshoot: a card wider than the window for a moment showed its cut edge as a white line.
    private const double CanvasWidth = CardWidth + Margin * 2 + 48;
    private const double CanvasHeight = 420;

    private static readonly TimeSpan PillShows = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan TuckAfter = TimeSpan.FromSeconds(4);

    private enum Shape { Bubble, Pill, Card, Panel }

    private sealed record Place(bool Right, double YFraction);

    private readonly IDeviceRegistry _registry = App.Services.GetRequiredService<IDeviceRegistry>();
    private readonly ILiveActivityService _live = App.Services.GetRequiredService<ILiveActivityService>();
    private readonly IConnectionSupervisor? _supervisor;
    private readonly bool _preview;

    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _shell = new();
    // The bubble is the Linc mark itself (owner, 2026-11-02, a screenshot: "look at this ugly
    // thing"): one black circle, the link's colour as its rim, the live dot cut into the rim. It
    // was a grey disc with a separate ring inside and the icon inside that, slightly off-centre.
    private readonly Grid _bubbleFace = new();
    private readonly ProgressRing _shareRing = new() { MinWidth = 0, MinHeight = 0, IsIndeterminate = false, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly Ellipse _liveDot = new() { StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _edgeShift = new();
    private readonly Ellipse _mark = new()
    {
        Fill = new ImageBrush { ImageSource = BubbleIcon(160), Stretch = Stretch.UniformToFill },
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel _liveRows = new() { Spacing = 6 };
    private readonly Grid _tileGrid = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private Button? _editButton;
    private bool _editing;
    private static readonly SolidColorBrush BubbleInk = new(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0C));
    private Brush _rimBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x48, 0x48, 0x4C));
    private readonly Grid _pillFace = new() { ColumnSpacing = 10, Opacity = 0 };
    private readonly Border _pillLead = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(16) };
    private readonly FontIcon _pillGlyph = new() { FontSize = 14, Foreground = new SolidColorBrush(Colors.White) };
    private readonly TextBlock _pillTitle = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillTrail = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _cardFace = new() { Spacing = 12, Opacity = 0, Padding = new Thickness(16), Visibility = Visibility.Collapsed };
    private readonly StackPanel _panelFace = new() { Spacing = 12, Opacity = 0, Padding = new Thickness(16, 14, 16, 16), Visibility = Visibility.Collapsed };
    private readonly TextBlock _panelTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _panelStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _connectButton = new();
    private readonly TranslateTransform _faceShift = new();
    private readonly ScaleTransform _cardZoom = new();
    private readonly ScaleTransform _panelZoom = new();
    private readonly ScaleTransform _press = new();
    private readonly DispatcherTimer _pillTimer = new() { Interval = PillShows };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveGrace };
    private readonly DispatcherTimer _tuckTimer = new() { Interval = TuckAfter };

    private Button? _hotspotTile;
    private ProgressBar? _cardProgress;
    private Shape _shape = Shape.Bubble;
    private Place _place;
    private bool _growUp;
    private bool _tucked;
    private bool _pointerOver;
    private bool _pressed;
    private bool _dragged;
    // Something new arrived while a card or the shortcuts were open: peek it once they close.
    private bool _peekPending;
    private POINT _pressCursor;
    private PointInt32 _pressWindow;
    private (PointInt32 From, PointInt32 To, DateTimeOffset Start)? _slideTarget;
    private Storyboard? _morph;
    private LiveActivity? _shown;
    private string? _shownKey;
    private (double W, double H, double X, double Y)? _lastRegion;
    private string? _cardKey;

    public LincBubbleWindow(bool preview = false)
    {
        _preview = preview;
        _supervisor = preview ? null : App.Services.GetService<IConnectionSupervisor>();
        Title = "Linc Bubble";
        _place = preview ? new Place(true, 0.35) : LoadPlace();
        BubbleSize = SizeFromSettings();
        BuildShell();
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
        Frameless();

        _pillTimer.Tick += (_, _) =>
        {
            _pillTimer.Stop();
            if (_shape == Shape.Pill && !_pointerOver) SetShape(Shape.Bubble);
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (_pointerOver || _shown?.AutoExpand == true) return;
            if (_shape is Shape.Pill or Shape.Card or Shape.Panel) SetShape(Shape.Bubble);
        };
        _tuckTimer.Tick += (_, _) =>
        {
            _tuckTimer.Stop();
            if (_shape == Shape.Bubble && !_pointerOver && !_pressed) Tuck(true);
        };
        _live.Changed += OnLiveChanged;
        _registry.AppearanceChanged += OnLookChanged;
        if (_supervisor is not null) _supervisor.StateChanged += OnLinkChanged;
        CompositionTarget.Rendering += FollowShape;
        Closed += (_, _) =>
        {
            _live.Changed -= OnLiveChanged;
            _registry.AppearanceChanged -= OnLookChanged;
            if (_supervisor is not null) _supervisor.StateChanged -= OnLinkChanged;
            CompositionTarget.Rendering -= FollowShape;
            _pillTimer.Stop();
            _leaveTimer.Stop();
            _tuckTimer.Stop();
        };
        _root.Loaded += (_, _) =>
        {
            Place_();
            RefreshLink();
            OnLiveChanged();
            ArmTuck();
        };
    }

    /// <summary>Show without taking focus from what the owner is doing.</summary>
    public void ShowQuietly()
    {
        AppWindow.Show(false);
        StayOnTop();
    }

    /// <summary>Above every app, without taking focus (the presenter's own always-on-top didn't stick on a never-activated window).</summary>
    private void StayOnTop()
    {
        try
        {
            SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this), new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
        }
        catch (Exception)
        {
            // Stays where it is in the stack; still works when nothing covers it.
        }
    }

    // ---- building ----

    private void BuildShell()
    {
        _shell.Width = BubbleSize;
        _shell.Height = BubbleSize;
        _shell.CornerRadius = new CornerRadius(BubbleSize / 2);
        _shell.Background = BubbleInk;
        _shell.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(180) };
        _shell.BorderBrush = _rimBrush;
        _shell.BorderThickness = new Thickness(Rim);
        _shell.Margin = new Thickness(Margin);
        _shell.RenderTransform = _edgeShift; // the tab slides flush with the screen edge
        SizeFace();

        // Bubble face: icon, link ring, a dot while something is live, a ring for a moving file.
        _shareRing.Foreground = Theme("PrimaryBrush");
        _liveDot.Fill = Theme("PrimaryBrush");
        _liveDot.Stroke = BubbleInk;
        // The Linc mark, round and crisp (owner, 2026-11-02: "fix the icon"), and smaller inside the
        // black disc (2026-11-03: "the icon is big, make it small, not the whole bubble").
        _bubbleFace.Children.Add(_mark);
        _bubbleFace.Children.Add(_shareRing); // a moving file runs round the rim, over the mark's edge
        _bubbleFace.Children.Add(_liveDot);

        // Pill face: lead (art or glyph), title, trailing note.
        _pillFace.Padding = new Thickness(10, 0, 16, 0);
        _pillFace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pillFace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pillFace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pillLead.VerticalAlignment = VerticalAlignment.Center;
        _pillLead.Child = _pillGlyph;
        _pillTitle.Foreground = Theme("OnSurfaceBrush");
        _pillTrail.Foreground = Theme("OnSurfaceVariantBrush");
        Grid.SetColumn(_pillTitle, 1);
        Grid.SetColumn(_pillTrail, 2);
        _pillFace.Children.Add(_pillLead);
        _pillFace.Children.Add(_pillTitle);
        _pillFace.Children.Add(_pillTrail);
        _pillFace.RenderTransform = _faceShift;

        var faces = new Grid();
        faces.Children.Add(_pillFace);
        faces.Children.Add(_cardFace);
        faces.Children.Add(_panelFace);
        faces.Children.Add(_bubbleFace);
        _cardFace.RenderTransform = _cardZoom;
        _panelFace.RenderTransform = _panelZoom;
        // The press squeeze moves the mark, not the shell: the window's clip follows the shell, and a
        // scaled shell left it clipped off-centre (a dark crescent) if a press was interrupted.
        _bubbleFace.RenderTransform = _press;
        _bubbleFace.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _shell.Child = faces;
        _root.Children.Add(_shell);

        _shell.PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            _leaveTimer.Stop();
            _tuckTimer.Stop();
            if (_tucked) Tuck(false);
            // Hovering the bubble while something is live peeks the island out.
            if (_shape == Shape.Bubble && _shown is not null && !_pressed) SetShape(Shape.Pill);
        };
        _shell.PointerExited += (_, _) =>
        {
            _pointerOver = false;
            _leaveTimer.Interval = LeaveGrace;
            if (_shape != Shape.Bubble) _leaveTimer.Start();
            ArmTuck();
        };
        _shell.PointerPressed += OnPressed;
        _shell.PointerMoved += OnMoved;
        _shell.PointerReleased += OnReleased;
        _shell.PointerCaptureLost += (_, _) =>
        {
            _pressed = false;
            Squeeze(1); // never left squeezed
        };
        BuildPanel();
    }

    private void BuildPanel()
    {
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Ellipse { Width = 30, Height = 30, Fill = new ImageBrush { ImageSource = BubbleIcon(120), Stretch = Stretch.UniformToFill }, VerticalAlignment = VerticalAlignment.Center });
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_panelTitle);
        titles.Children.Add(_panelStatus);
        _panelTitle.Foreground = Theme("OnSurfaceBrush");
        _panelStatus.Foreground = Theme("OnSurfaceVariantBrush");
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        // Close (owner, 2026-11-02: "add close button to the bubble so I can easily close it"):
        // turns the bubble's setting off, so it stays away until Settings › Appearance turns it on.
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var close = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme("SecondaryContainerBrush"),
            Foreground = Theme("OnSecondaryContainerBrush"),
            Content = new FontIcon { Glyph = "", FontSize = 12 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close the Linc Bubble");
        ToolTipService.SetToolTip(close, "Close the bubble (Settings › Appearance brings it back)");
        close.Click += (_, _) =>
        {
            if (_preview) Close();
            else _registry.SaveLiveBubble(false); // LincBubbleHost closes the window
        };
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        // Edit the shortcuts (owner, 2026-11-03: "allow adding and removing quick actions").
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _editButton = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme("SecondaryContainerBrush"),
            Foreground = Theme("OnSecondaryContainerBrush"),
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editButton, "Choose the bubble's shortcuts");
        ToolTipService.SetToolTip(_editButton, "Choose the shortcuts");
        _editButton.Click += (_, _) =>
        {
            _editing = !_editing;
            _editButton.Content = new FontIcon { Glyph = _editing ? "\uE73E" : "\uE70F", FontSize = 12 };
            BuildTiles();
            Resize(animate: true);
        };
        Grid.SetColumn(_editButton, 2);
        header.Children.Add(_editButton);
        _panelFace.Children.Add(header);
        // What's happening now (a mirror, a song, a file): a click opens its card, so a live
        // activity never stands between you and the shortcuts (owner, 2026-11-03: "I'm mirroring,
        // it shows the mirror expand and I can't get to the quick actions").
        _panelFace.Children.Add(_liveRows);

        for (int c = 0; c < 4; c++) _tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        BuildTiles();
        _panelFace.Children.Add(_tileGrid);

        _connectButton.Content = "Connect now";
        _connectButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _connectButton.CornerRadius = new CornerRadius(18);
        _connectButton.Height = 36;
        _connectButton.BorderThickness = new Thickness(0);
        _connectButton.Background = Theme("PrimaryBrush");
        _connectButton.Foreground = Theme("OnPrimaryBrush");
        _connectButton.Click += (_, _) =>
        {
            if (_supervisor is null) return;
            if (_supervisor.State == LinkState.Paused) _supervisor.ResumeAutomatic();
            else _supervisor.NudgeReconnect("Linc Bubble");
            SetShape(Shape.Bubble);
        };
        _panelFace.Children.Add(_connectButton);
    }

    private double SizeFromSettings() =>
        int.TryParse(DeviceRegistry.NormalBubbleSize(_registry.BubbleSize), out var px) ? px : 36;

    /// <summary>Everything sized from the bubble's diameter (Settings › Appearance › Bubble size).</summary>
    private void SizeFace()
    {
        _bubbleFace.Width = _bubbleFace.Height = FaceSize;
        _shareRing.Width = _shareRing.Height = FaceSize;
        _mark.Width = _mark.Height = Math.Round(FaceSize * 0.74);
        var dot = Math.Max(8, Math.Round(BubbleSize * 0.24));
        _liveDot.Width = _liveDot.Height = dot;
        _liveDot.Margin = new Thickness(0, BubbleSize * 0.08, BubbleSize * 0.08, 0);
    }

    /// <summary>The size slider, the idle look or the shortcuts changed: follow at once, no reopening.</summary>
    private void OnLookChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        var size = SizeFromSettings();
        if (Math.Abs(size - BubbleSize) > 0.5)
        {
            BubbleSize = size;
            _shell.CornerRadius = new CornerRadius(BubbleSize / 2);
            SizeFace();
            Place_();
            Resize(animate: false);
        }
        if (_tucked && _registry.BubbleIdle == "visible") Tuck(false);
        Dress();
        BuildTiles();
    });

    /// <summary>The round Linc mark, decoded at the size it's drawn (times the screen scale) so it stays sharp.</summary>
    private static BitmapImage BubbleIcon(int pixels) =>
        new(new Uri("ms-appx:///Assets/linc-bubble.png")) { DecodePixelWidth = pixels };

    private sealed record Shortcut(string Glyph, string Label, Action Run, bool OpensLinc);

    /// <summary>Every shortcut the bubble offers; the owner picks which show, in this order.</summary>
    private IReadOnlyList<Shortcut> AllShortcuts()
    {
        var main = App.MainWindowInstance as MainWindow;
        return
        [
            new("\uE80F", "Open Linc", () => main?.ShowPage(null), true),
            new("\uE7F4", "Mirror", () => Home()?.StartMirrorCommand.Execute(null), false),
            new("\uEA8F", "Ring", () => Home()?.RingPhoneCommand.Execute(null), false),
            new("\uE722", "Screenshot", () => Home()?.PullScreenshotCommand.Execute(null), false),
            new("\uE77F", "Clipboard", () => main?.SendClipboardToPhone(), false),
            new("\uE8B7", "Files", () => main?.ShowPage("Files"), true),
            new("\uE72D", "Share", () => main?.ShowPage("Share"), true),
            new("\uE704", "Hotspot", () => _ = ToggleHotspotAsync(), false),
            new("\uE8EA", "Device", () => main?.ShowPage("Device"), true),
            new("\uE90F", "Tools", () => main?.ShowPage("Tools"), true),
        ];
    }

    private static readonly string[] DefaultShortcuts = ["Open Linc", "Mirror", "Ring", "Screenshot", "Clipboard", "Files", "Share", "Hotspot"];

    private List<string> ChosenShortcuts() =>
        string.IsNullOrWhiteSpace(_registry.BubbleTiles) || _preview
            ? [.. DefaultShortcuts]
            : [.. _registry.BubbleTiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The shortcut tiles: the chosen ones; in edit mode every one, the hidden ones faded with a +
    /// and the shown ones with a −, a tap adding or removing it. A shortcut runs and the panel stays
    /// open (owner, 2026-11-03: "when I click something it immediately closes"), except the ones
    /// that bring up Linc itself.
    /// </summary>
    private void BuildTiles()
    {
        _tileGrid.Children.Clear();
        _tileGrid.RowDefinitions.Clear();
        _hotspotTile = null;
        var chosen = ChosenShortcuts();
        var all = AllShortcuts();
        var shown = _editing
            ? [.. chosen.Select(name => all.FirstOrDefault(s => s.Label == name)).OfType<Shortcut>(), .. all.Where(s => !chosen.Contains(s.Label))]
            : chosen.Select(name => all.FirstOrDefault(s => s.Label == name)).OfType<Shortcut>().ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var shortcut = shown[i];
            var on = chosen.Contains(shortcut.Label);
            var tile = Tile(shortcut.Glyph, _editing ? (on ? "− " : "+ ") + shortcut.Label : shortcut.Label, () =>
            {
                if (_editing)
                {
                    var next = ChosenShortcuts();
                    if (!next.Remove(shortcut.Label)) next.Add(shortcut.Label);
                    _registry.SaveBubbleTiles(string.Join(",", next)); // OnLookChanged rebuilds
                    return;
                }
                try { shortcut.Run(); } catch (Exception) { /* the feature reports its own failures */ }
                if (shortcut.OpensLinc) SetShape(Shape.Bubble);
                else RefreshLink(); // e.g. the hotspot tile's colour
            });
            if (_editing && !on) tile.Opacity = 0.45;
            if (shortcut.Label == "Hotspot") _hotspotTile = tile;
            if (i % 4 == 0) _tileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(tile, i % 4);
            Grid.SetRow(tile, i / 4);
            _tileGrid.Children.Add(tile);
        }
        RefreshLink();
        if (_shape == Shape.Panel) Resize(animate: false);
    }

    /// <summary>The panel's live rows: up to three things happening now, each opening its card.</summary>
    private void FillLiveRows()
    {
        _liveRows.Children.Clear();
        foreach (var a in _live.Activities.Take(3))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Background = a.Art is { } art ? new ImageBrush { ImageSource = art, Stretch = Stretch.UniformToFill } : LeadBrush(a.Kind),
                Child = a.Art is null ? new FontIcon { Glyph = a.Glyph, FontSize = 13, Foreground = LeadInk(a.Kind) } : null,
            });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = a.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(a.Subtitle))
            {
                words.Children.Add(new TextBlock { Text = a.Subtitle, FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            }
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 10, 6),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(0),
                Background = Theme("SurfaceContainerHighestBrush"),
                Content = row,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, a.Title);
            var activity = a;
            button.Click += (_, _) =>
            {
                FillCard(activity);
                SetShape(Shape.Card);
            };
            _liveRows.Children.Add(button);
        }
        _liveRows.Visibility = _liveRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private HomeViewModel? Home() => _preview ? null : App.Services.GetService<HomeViewModel>();

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

    /// <summary>
    /// The app's own surface, the live brush (ThemeSyncService retints it in place when the phone's
    /// palette arrives), tinted and a little see-through like the phone card.
    /// </summary>
    private static Brush SurfaceBrush() => Theme("SurfaceContainerHighBrush");

    private static Brush Theme(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Colors.Gray);

    // ---- what to show ----

    private void OnLinkChanged() => DispatcherQueue.TryEnqueue(RefreshLink);

    private void RefreshLink()
    {
        var state = _supervisor?.State ?? LinkState.Connected;
        var model = _registry.KnownDevices.FirstOrDefault(d => d.Serial == _registry.PairedSerial)?.Model ?? "Your phone";
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
        _rimBrush = state == LinkState.Connected ? new SolidColorBrush(Color.FromArgb(0xFF, 0x34, 0xC7, 0x59)) : new SolidColorBrush(Color.FromArgb(0xFF, 0x48, 0x48, 0x4C));
        Dress();
        _connectButton.Visibility = state is LinkState.Searching or LinkState.Paused ? Visibility.Visible : Visibility.Collapsed;
        var hotspotOn = !_preview && App.Services.GetService<PcHotspot>()?.Current is { } mode && mode != PcHotspot.Mode.Off;
        if (_hotspotTile is not null)
        {
            _hotspotTile.Background = Theme(hotspotOn ? "PrimaryBrush" : "SecondaryContainerBrush");
            _hotspotTile.Foreground = Theme(hotspotOn ? "OnPrimaryBrush" : "OnSecondaryContainerBrush");
        }
    }

    private async Task ToggleHotspotAsync()
    {
        if (_preview || App.Services.GetService<PcHotspot>() is not { } hotspot) return;
        if (hotspot.Current == PcHotspot.Mode.Off) await Task.Run(hotspot.StartAsync);
        else await Task.Run(hotspot.StopAsync);
        DispatcherQueue.TryEnqueue(RefreshLink);
    }

    /// <summary>
    /// The live list changed. Something new, or something worth a look (a new song, a file
    /// finishing), pops the pill out for a few seconds; progress ticks only update in place.
    /// </summary>
    private void OnLiveChanged()
    {
        try
        {
            ShowLive();
        }
        catch (Exception ex)
        {
            // Never let the island take the app down; say what went wrong.
            App.Services.GetService<ILogService>()?.Log(LogLevel.Warn, $"Linc Bubble: {ex.GetType().Name}: {ex.Message}");
            if (_preview) File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linc-bubble-preview.txt"), ex + Environment.NewLine);
        }
    }

    private void ShowLive()
    {
        if (_shape == Shape.Panel)
        {
            FillLiveRows();
            Resize(animate: false);
        }
        var top = _live.Activities.FirstOrDefault();
        var key = top is null ? null : $"{top.Id}|{top.Title}|{top.Subtitle?.Split('·')[0]}";
        var attention = key != _shownKey;
        _shown = top;
        _shownKey = key;
        if (top is null)
        {
            _liveDot.Visibility = Visibility.Collapsed;
            _shareRing.Visibility = Visibility.Collapsed;
            if (_shape is Shape.Pill or Shape.Card) SetShape(Shape.Bubble);
            return;
        }
        FillPill(top);
        FillCard(top);
        var moving = top.Kind == LiveActivityKind.Share && top.Progress is < 100;
        _shareRing.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        _shareRing.Value = top.Progress ?? 0;
        _liveDot.Visibility = moving ? Visibility.Collapsed : Visibility.Visible;
        _liveDot.Fill = new SolidColorBrush(Accent(top.Kind));
        if (top.AutoExpand)
        {
            SetShape(Shape.Card); // a ringing call opens and stays
            return;
        }
        // Quiet while Linc itself is in front (its own island shows the same), and a burst of
        // notifications pops the pill once, then updates in place (owner, 2026-11-03: "when the
        // Linc app is open the bubble should be silent", "have a notification cool-down").
        if (attention && (LincInFront() || (top.Kind == LiveActivityKind.Notification && DateTimeOffset.UtcNow - _lastNotificationPop < NotificationQuiet)))
        {
            attention = false;
        }
        else if (attention && top.Kind == LiveActivityKind.Notification)
        {
            _lastNotificationPop = DateTimeOffset.UtcNow;
        }
        if (attention && _shape is Shape.Card or Shape.Panel && !_pointerOver)
        {
            _peekPending = true; // shown as a pill once the open card closes
        }
        if (attention && _shape is Shape.Bubble or Shape.Pill)
        {
            SetShape(Shape.Pill);
            _pillTimer.Stop();
            _pillTimer.Start();
        }
        else if (_shape is Shape.Pill or Shape.Card)
        {
            Resize(animate: false); // same activity, new numbers: size follows, no bounce
        }
    }

    private static readonly TimeSpan NotificationQuiet = TimeSpan.FromSeconds(8);
    private DateTimeOffset _lastNotificationPop;

    /// <summary>Linc's own window is the one in front.</summary>
    private bool LincInFront()
    {
        if (_preview || App.MainWindowInstance is not { } main) return false;
        try
        {
            return GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(main);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void FillPill(LiveActivity a)
    {
        _pillLead.Background = a.Art is { } art ? new ImageBrush { ImageSource = art, Stretch = Stretch.UniformToFill } : LeadBrush(a.Kind);
        _pillGlyph.Foreground = LeadInk(a.Kind);
        _pillGlyph.Glyph = a.Glyph;
        _pillGlyph.Visibility = a.Art is null ? Visibility.Visible : Visibility.Collapsed;
        _pillTitle.Text = a.Title;
        _pillTrail.Text = a.Trailing ?? "";
        _pillTrail.Visibility = string.IsNullOrEmpty(a.Trailing) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FillCard(LiveActivity a)
    {
        var key = a.Id + "|" + string.Join("|", (a.Actions ?? []).Select(x => x.Label)) + "|" + a.Title + "|" + a.Subtitle?.Split('·')[0] + "|" + (a.Art is null);
        if (key == _cardKey && _cardFace.Children.Count > 0)
        {
            if (_cardProgress is not null) _cardProgress.Value = a.Progress ?? 0; // progress ticks: just the bar
            return;
        }
        _cardKey = key;
        _cardFace.Children.Clear();
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(14),
            Background = a.Art is { } art ? new ImageBrush { ImageSource = art, Stretch = Stretch.UniformToFill } : LeadBrush(a.Kind),
            Child = a.Art is null ? new FontIcon { Glyph = a.Glyph, FontSize = 20, Foreground = LeadInk(a.Kind) } : null,
        };
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = a.Title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(a.Subtitle))
        {
            words.Children.Add(new TextBlock { Text = a.Subtitle, FontSize = 12, Foreground = Theme("OnSurfaceVariantBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        Grid.SetColumn(words, 1);
        head.Children.Add(tile);
        head.Children.Add(words);
        _cardFace.Children.Add(head);
        _cardProgress = null;
        if (a.Progress is { } progress)
        {
            _cardProgress = new ProgressBar { Minimum = 0, Maximum = 100, Value = progress, Foreground = Theme("PrimaryBrush"), Background = Theme("OutlineVariantBrush") };
            _cardFace.Children.Add(_cardProgress);
        }
        var actions = a.Actions ?? [];
        if (actions.Count > 0)
        {
            var row = new Grid { ColumnSpacing = 8 };
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 36,
                    CornerRadius = new CornerRadius(18),
                    BorderThickness = new Thickness(0),
                    Background = action.Danger ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A)) : Theme("SecondaryContainerBrush"),
                    Foreground = action.Danger ? new SolidColorBrush(Colors.White) : Theme("OnSecondaryContainerBrush"),
                    Content = action.Glyph is { } glyph
                        ? new FontIcon { Glyph = glyph, FontSize = 14 }
                        : new TextBlock { Text = action.Label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, action.Label);
                if (action.Glyph is not null) ToolTipService.SetToolTip(button, action.Label);
                button.Click += (_, _) =>
                {
                    try { action.Invoke(); } catch (Exception) { /* the feature reports its own failures */ }
                };
                Grid.SetColumn(button, i);
                row.Children.Add(button);
            }
            _cardFace.Children.Add(row);
        }
    }

    /// <summary>A lead tile: calls, links, battery and mirroring keep their signal colour; the rest wear the app's palette.</summary>
    private static Brush LeadBrush(LiveActivityKind kind) =>
        kind is LiveActivityKind.Media or LiveActivityKind.Share ? Theme("PrimaryContainerBrush") : new SolidColorBrush(Accent(kind));

    private static Brush LeadInk(LiveActivityKind kind) =>
        kind is LiveActivityKind.Media or LiveActivityKind.Share ? Theme("OnPrimaryContainerBrush") : new SolidColorBrush(Colors.White);

    private static Color Accent(LiveActivityKind kind) => kind switch
    {
        LiveActivityKind.Call => Color.FromArgb(0xFF, 0x34, 0xC7, 0x59),
        LiveActivityKind.Link => Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A),
        LiveActivityKind.Battery => Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A),
        LiveActivityKind.Mirror => Color.FromArgb(0xFF, 0x5B, 0x5B, 0xD6),
        LiveActivityKind.Notification => Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        LiveActivityKind.Status => Color.FromArgb(0xFF, 0x34, 0xC7, 0x59),
        LiveActivityKind.Clipboard => Color.FromArgb(0xFF, 0x30, 0xB0, 0xC7),
        _ => Application.Current.Resources.TryGetValue("PrimaryBrush", out var v) && v is SolidColorBrush { Color: var c } && c.A > 0
            ? c : Color.FromArgb(0xFF, 0x8F, 0x6A, 0xD8),
    };

    // ---- shape and motion ----

    private void SetShape(Shape shape)
    {
        if (_shape == shape)
        {
            return;
        }
        _shape = shape;
        Dress();
        if (shape != Shape.Bubble && _tucked) Tuck(false);
        if (shape != Shape.Bubble) StayOnTop(); // opening: be above whatever came up since
        if (shape != Shape.Pill) _pillTimer.Stop();
        if (shape == Shape.Panel)
        {
            RefreshLink();
            FillLiveRows();
        }
        Resize(animate: true);
        if (shape == Shape.Bubble) ArmTuck(); else _tuckTimer.Stop();
        if (shape == Shape.Bubble && _peekPending)
        {
            _peekPending = false;
            PeekSoon();
        }
        // Opened and never touched (a click and away, or opened by itself): it still closes.
        if (shape is Shape.Card or Shape.Panel && !_pointerOver && _shown?.AutoExpand != true)
        {
            _leaveTimer.Interval = TimeSpan.FromSeconds(8);
            _leaveTimer.Start();
        }
    }

    /// <summary>The bubble wears the black mark and the link's rim; pills and cards the app's surface and a quiet outline.</summary>
    private void Dress()
    {
        var bubble = _shape == Shape.Bubble;
        if (bubble && _tucked)
        {
            // The slim tab: a soft bar in the link's colour, or nothing at all with "Hidden" (still
            // there for the pointer: reaching the edge brings the bubble back).
            var hidden = _registry.BubbleIdle == "hidden" && !_preview;
            _shell.Background = hidden ? new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)) : TabBrush();
            _shell.BorderBrush = new SolidColorBrush(Colors.Transparent);
            _shell.BorderThickness = new Thickness(0);
            return;
        }
        _shell.BorderThickness = new Thickness(Rim);
        _shell.Background = bubble ? BubbleInk : SurfaceBrush();
        if (!bubble || _shell.ActualWidth <= BubbleSize + 1)
        {
            _shell.BorderBrush = bubble ? _rimBrush : Theme("OutlineVariantBrush");
            return;
        }
        // Shrinking back: the link's rim goes on once it's a circle again, not around a pill.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_shape == Shape.Bubble) _shell.BorderBrush = _rimBrush;
        };
        timer.Start();
    }

    private Brush TabBrush() =>
        _supervisor?.State is LinkState.Connected or null
            ? new SolidColorBrush(Color.FromArgb(0xD0, 0x34, 0xC7, 0x59))
            : new SolidColorBrush(Color.FromArgb(0xB0, 0x8E, 0x8E, 0x93));

    /// <summary>Tucked, the tab sits flush with the screen edge, centred on where the bubble was.</summary>
    private void ShiftToEdge(bool animate)
    {
        var tab = _shape == Shape.Bubble && _tucked;
        double x = tab ? (_place.Right ? Margin : -Margin) : 0;
        double y = tab ? (_growUp ? -1 : 1) * (BubbleSize - TabHeight) / 2 : 0;
        if (!animate)
        {
            _edgeShift.X = x;
            _edgeShift.Y = y;
            return;
        }
        var board = new Storyboard();
        foreach (var (axis, to) in new[] { ("X", x), ("Y", y) })
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(tab ? 340 : 420)),
                EasingFunction = tab ? new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 } : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 },
            };
            Storyboard.SetTarget(a, _edgeShift);
            Storyboard.SetTargetProperty(a, axis);
            board.Children.Add(a);
        }
        board.Begin();
    }

    /// <summary>After the shrink settles, pop the pill out for what arrived while the card was open.</summary>
    private void PeekSoon()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(380);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_shape != Shape.Bubble || _shown is null || _pressed) return;
            SetShape(Shape.Pill);
            _pillTimer.Stop();
            _pillTimer.Start();
        };
        timer.Start();
    }

    /// <summary>The preview's stand-in for a click: open the card (something live) or the shortcuts.</summary>
    internal void PreviewClick() => SetShape(Shape.Panel);

    /// <summary>The shell's size for the shape, then a morph to it, faces crossfading on the way.</summary>
    private void Resize(bool animate)
    {
        double width, height;
        switch (_shape)
        {
            case Shape.Pill:
                _pillFace.Width = double.NaN;
                _pillFace.Measure(new Windows.Foundation.Size(double.PositiveInfinity, Math.Max(BubbleSize, 44)));
                width = Math.Clamp(Math.Ceiling(_pillFace.DesiredSize.Width) + 4, 150, PillMaxWidth);
                height = Math.Max(BubbleSize, 44);
                _pillFace.Width = width;
                break;
            case Shape.Card:
                _cardFace.Visibility = Visibility.Visible;
                _cardFace.Width = CardWidth;
                _cardFace.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
                width = CardWidth;
                height = Math.Ceiling(_cardFace.DesiredSize.Height) + 2;
                break;
            case Shape.Panel:
                _panelFace.Visibility = Visibility.Visible;
                _panelFace.Width = CardWidth;
                _panelFace.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
                width = CardWidth;
                height = Math.Ceiling(_panelFace.DesiredSize.Height) + 2;
                break;
            default:
                width = _tucked ? TabWidth : BubbleSize;
                height = _tucked ? TabHeight : BubbleSize;
                break;
        }
        Fade(_bubbleFace, _shape == Shape.Bubble && !_tucked ? 1 : 0, animate);
        ShiftToEdge(animate);
        Fade(_pillFace, _shape == Shape.Pill ? 1 : 0, animate);
        Fade(_cardFace, _shape == Shape.Card ? 1 : 0, animate, () => { if (_shape != Shape.Card) _cardFace.Visibility = Visibility.Collapsed; }, _cardZoom);
        Fade(_panelFace, _shape == Shape.Panel ? 1 : 0, animate, () => { if (_shape != Shape.Panel) _panelFace.Visibility = Visibility.Collapsed; }, _panelZoom);
        _bubbleFace.IsHitTestVisible = _shape == Shape.Bubble;
        _pillFace.IsHitTestVisible = _shape == Shape.Pill;
        _cardFace.IsHitTestVisible = _shape == Shape.Card;
        _panelFace.IsHitTestVisible = _shape == Shape.Panel;
        if (_shape == Shape.Pill && animate) SlideFace();
        Morph(width, height, animate);
    }

    /// <summary>Width and height to the target: growing overshoots a touch and settles, shrinking glides in.</summary>
    private void Morph(double width, double height, bool animate)
    {
        _morph?.Stop();
        var fromW = _shell.ActualWidth > 0 ? _shell.ActualWidth : _shell.Width;
        var fromH = _shell.ActualHeight > 0 ? _shell.ActualHeight : _shell.Height;
        if (!animate || (Math.Abs(fromW - width) < 0.5 && Math.Abs(fromH - height) < 0.5))
        {
            _shell.Width = width;
            _shell.Height = height;
            return;
        }
        _shell.Width = fromW;
        _shell.Height = fromH;
        var growing = width * height > fromW * fromH;
        EasingFunctionBase ease = growing
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
            : new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var duration = new Duration(TimeSpan.FromMilliseconds(growing ? 480 : 340));
        var w = new DoubleAnimation { From = fromW, To = width, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        var h = new DoubleAnimation { From = fromH, To = height, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(w, _shell);
        Storyboard.SetTargetProperty(w, "Width");
        Storyboard.SetTarget(h, _shell);
        Storyboard.SetTargetProperty(h, "Height");
        var board = new Storyboard();
        board.Children.Add(w);
        board.Children.Add(h);
        board.Completed += (_, _) =>
        {
            _shell.Width = width;
            _shell.Height = height;
        };
        _morph = board;
        board.Begin();
    }

    /// <summary>
    /// A face fades in or out. The two overlap (the new one starts before the old one is gone), so
    /// the shell is never an empty grey box mid-morph (owner, 2026-11-02: "fix the animations");
    /// with <paramref name="zoom"/> the face also grows from 94 % as it appears.
    /// </summary>
    private static void Fade(UIElement element, double to, bool animate, Action? done = null, ScaleTransform? zoom = null)
    {
        if (!animate)
        {
            element.Opacity = to;
            if (zoom is not null) zoom.ScaleX = zoom.ScaleY = 1;
            done?.Invoke();
            return;
        }
        if (Math.Abs(element.Opacity - to) < 0.01)
        {
            done?.Invoke();
            return; // already there: no flicker from a needless restart
        }
        var fade = new DoubleAnimation
        {
            To = to,
            // In soon after the shape starts to move; out quickly so nothing spills past the edge.
            BeginTime = to > 0 ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero,
            Duration = new Duration(TimeSpan.FromMilliseconds(to > 0 ? 220 : 110)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fade);
        if (zoom is not null && to > 0)
        {
            foreach (var axis in new[] { "ScaleX", "ScaleY" })
            {
                var grow = new DoubleAnimation
                {
                    From = 0.94, To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
                };
                Storyboard.SetTarget(grow, zoom);
                Storyboard.SetTargetProperty(grow, axis);
                board.Children.Add(grow);
            }
        }
        board.Completed += (_, _) =>
        {
            element.Opacity = to;
            done?.Invoke();
        };
        board.Begin();
    }

    /// <summary>The pill's words slide out from the bubble's side as it opens.</summary>
    private void SlideFace()
    {
        var slide = new DoubleAnimation
        {
            From = _place.Right ? 18 : -18, To = 0,
            BeginTime = TimeSpan.FromMilliseconds(90),
            Duration = new Duration(TimeSpan.FromMilliseconds(380)),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, _faceShift);
        Storyboard.SetTargetProperty(slide, "X");
        var board = new Storyboard();
        board.Children.Add(slide);
        board.Begin();
    }

    private bool _mouseWasDown;

    /// <summary>
    /// Open and a click lands anywhere else on the screen: fold back into the bubble (owner,
    /// 2026-11-03: "when I hover away from the bubble and click elsewhere, hide it"). The bubble
    /// never takes focus, so it can't hear the click itself; it looks at the mouse button each frame.
    /// A ringing call's card stays.
    /// </summary>
    private void CloseOnClickElsewhere()
    {
        var down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0; // VK_LBUTTON, VK_RBUTTON
        var pressedNow = down && !_mouseWasDown;
        _mouseWasDown = down;
        if (!pressedNow || _shape == Shape.Bubble || _pointerOver || _shown?.AutoExpand == true || _preview) return;
        SetShape(Shape.Bubble);
    }

    /// <summary>Every frame: clip the window to the shell (only when it changed), so the canvas never shows or takes clicks.</summary>
    private void FollowShape(object? sender, object e)
    {
        if (_slideTarget is not null) SlideStep(); // every frame, not a 15 ms timer: no judder
        CloseOnClickElsewhere();
        if (_shell.ActualWidth <= 0) return;
        var origin = _shell.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var now = (_shell.ActualWidth, _shell.ActualHeight, origin.X, origin.Y);
        if (_lastRegion == now) return;
        _lastRegion = now;
        try
        {
            var scale = Scale();
            // A few pixels of slack round the shell: the clip is whole pixels and aliased, the shell's
            // edge is smooth and at fractional positions (125 % scaling), and a clip cut flush with it
            // shaved the rim unevenly into a crescent (owner, 2026-11-02: "still not aligned").
            const int pad = 3;
            int left = (int)Math.Floor(origin.X * scale) - pad, top = (int)Math.Floor(origin.Y * scale) - pad;
            int right = (int)Math.Ceiling((origin.X + _shell.ActualWidth) * scale) + 1 + pad;
            int bottom = (int)Math.Ceiling((origin.Y + _shell.ActualHeight) * scale) + 1 + pad;
            var radius = (int)Math.Round(Math.Min(BubbleSize, Math.Min(_shell.ActualWidth, _shell.ActualHeight)) * scale) + pad * 2;
            var region = CreateRoundRectRgn(left, top, right, bottom, radius, radius);
            SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), region, true); // the window owns it now
        }
        catch (Exception)
        {
            // No region: the transparent backdrop alone still hides the canvas.
        }
    }

    // ---- placement ----

    /// <summary>Put the canvas where the bubble lives: its side of the screen, opening down (or up near the bottom).</summary>
    private void Place_()
    {
        var scale = Scale();
        var area = WorkArea();
        var size = new SizeInt32((int)Math.Ceiling(CanvasWidth * scale), (int)Math.Ceiling(CanvasHeight * scale));
        var bubbleY = area.Y + (int)Math.Round(_place.YFraction * Math.Max(0, area.Height - BubbleSize * scale));
        _growUp = bubbleY + size.Height > area.Y + area.Height;
        var y = _growUp ? bubbleY + (int)((BubbleSize + Margin) * scale) - size.Height : bubbleY - (int)(Margin * scale);
        // 3 px past the screen edge: the content ends 3 px inside the window with a light 1 px edge
        // there, so the tucked tab sat off the edge beside a thin line. This puts both off-screen.
        const int overhang = 3;
        var x = _place.Right ? area.X + area.Width - size.Width + overhang : area.X - overhang;
        _shell.HorizontalAlignment = _place.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _shell.VerticalAlignment = _growUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        _pillFace.FlowDirection = _place.Right ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _pillTitle.FlowDirection = FlowDirection.LeftToRight;
        _pillTrail.FlowDirection = FlowDirection.LeftToRight;
        _bubbleFace.HorizontalAlignment = _place.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _bubbleFace.VerticalAlignment = _growUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        var corner = new Windows.Foundation.Point(_place.Right ? 1 : 0, _growUp ? 1 : 0);
        _cardFace.RenderTransformOrigin = corner;
        _panelFace.RenderTransformOrigin = corner;
        _slideTarget = null;
        AppWindow.MoveAndResize(new RectInt32(x, y, size.Width, size.Height));
        StayOnTop();
        _root.Opacity = 1;
        _lastRegion = null;
    }

    private void ArmTuck()
    {
        _tuckTimer.Stop();
        if (_shape == Shape.Bubble && !_tucked && !_pointerOver) _tuckTimer.Start();
    }

    /// <summary>Tuck half into the screen edge (or come back out), sliding.</summary>
    private void Tuck(bool tuck)
    {
        if (_tucked == tuck) return;
        if (tuck && _registry.BubbleIdle == "visible" && !_preview) return; // "Always a bubble"
        _tucked = tuck;
        Dress();
        if (_shape == Shape.Bubble) Resize(animate: true);
    }


    private void SlideStep()
    {
        if (_slideTarget is not { } t)
        {
            return;
        }
        var p = Math.Min(1, (DateTimeOffset.UtcNow - t.Start).TotalMilliseconds / 280);
        var eased = 1 - Math.Pow(1 - p, 3);
        AppWindow.Move(new PointInt32((int)(t.From.X + (t.To.X - t.From.X) * eased), (int)(t.From.Y + (t.To.Y - t.From.Y) * eased)));
        if (p >= 1)
        {
            _slideTarget = null;
        }
    }

    // ---- clicks and dragging ----

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Buttons inside the card and the panel take their own clicks.
        if (_shape is Shape.Card or Shape.Panel) return;
        _pressed = true;
        _dragged = false;
        GetCursorPos(out _pressCursor);
        _pressWindow = AppWindow.Position;
        _shell.CapturePointer(e.Pointer);
        Squeeze(0.9);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        GetCursorPos(out var cursor);
        int dx = cursor.X - _pressCursor.X, dy = cursor.Y - _pressCursor.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 6) return;
        if (!_dragged)
        {
            _dragged = true;
            Squeeze(1);
            _pillTimer.Stop();
            if (_shape != Shape.Bubble) SetShape(Shape.Bubble);
        }
        _slideTarget = null;
        AppWindow.Move(new PointInt32(_pressWindow.X + dx, _pressWindow.Y + dy));
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _shell.ReleasePointerCapture(e.Pointer);
        Squeeze(1);
        if (!_dragged)
        {
            // A click: the card when something is live, the shortcuts when not.
            SetShape(_shape == Shape.Pill && _shown is not null ? Shape.Card : Shape.Panel);
            return;
        }
        // Snap to the nearer side, keep the height.
        var scale = Scale();
        var area = WorkArea();
        GetCursorPos(out var cursor);
        var right = cursor.X > area.X + area.Width / 2;
        var bubbleTop = cursor.Y - BubbleSize * scale / 2;
        var fraction = Math.Clamp((bubbleTop - area.Y) / Math.Max(1, area.Height - BubbleSize * scale), 0, 1);
        _place = new Place(right, fraction);
        if (!_preview) SavePlace(_place);
        _tucked = false;
        Dress();
        Place_();
        Resize(animate: false);
        ArmTuck();
    }

    /// <summary>The shell gives under the finger and springs back.</summary>
    private void Squeeze(double to)
    {
        var board = new Storyboard();
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(to < 1 ? 110 : 420)),
                EasingFunction = to < 1 ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 },
            };
            Storyboard.SetTarget(a, _press);
            Storyboard.SetTargetProperty(a, axis);
            board.Children.Add(a);
        }
        board.Begin();
    }

    // ---- window plumbing ----

    /// <summary>No frame, no border, no shadow, never activated (clicks don't steal focus), not in Alt+Tab.</summary>
    private void Frameless()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int)); // DWMWA_BORDER_COLOR
            int square = 1; // DWMWCP_DONOTROUND: the region gives the shape
            DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            var ex = GetWindowLongPtr(hwnd, -20); // GWL_EXSTYLE
            SetWindowLongPtr(hwnd, -20, ex | 0x08000000 | 0x00000080); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            // A plain popup, no frame at all: the leftover resize frame kept the tab a few pixels off
            // the screen edge with a 1 px light line beside it.
            var style = GetWindowLongPtr(hwnd, -16); // GWL_STYLE
            style &= ~(nint)(0x00C00000 | 0x00040000 | 0x00800000); // WS_CAPTION | WS_THICKFRAME | WS_BORDER
            SetWindowLongPtr(hwnd, -16, style | unchecked((nint)0x80000000)); // WS_POPUP
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED
            // Real per-pixel transparency: without it the "transparent" backdrop draws black, and
            // only the (aliased, whole-pixel) clip hid it, which left a black edge round the bubble
            // and the tab (owner, 2026-11-02: "I don't want to see that black protrude").
            var blur = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2, fEnable = true, hRgnBlur = CreateRectRgn(0, 0, -1, -1) }; // DWM_BB_ENABLE | DWM_BB_BLURREGION, an empty region
            DwmEnableBlurBehindWindow(hwnd, ref blur);
        }
        catch (Exception)
        {
            // Older Windows: a thin border may show; the bubble still works.
        }
    }

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

    private RectInt32 WorkArea() => DisplayArea.GetFromPoint(AppWindow.Position, DisplayAreaFallback.Primary).WorkArea;

    private string PlacePath => System.IO.Path.Combine(_registry.RootPath, "bubble.json");

    private Place LoadPlace()
    {
        try
        {
            if (File.Exists(PlacePath) && JsonSerializer.Deserialize<Place>(File.ReadAllText(PlacePath)) is { } place) return place;
        }
        catch (Exception)
        {
            // Unreadable: it starts on the right.
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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND { public uint dwFlags; [MarshalAs(UnmanagedType.Bool)] public bool fEnable; public IntPtr hRgnBlur; [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized; }

    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
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
                connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
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

    /// <summary>
    /// `--bubble-preview` (with `--data-root` somewhere empty): only the bubble, with pretend live
    /// activity on a loop, nothing else started, so its looks and motion can be checked and
    /// screenshotted beside a running Linc. For development only.
    /// </summary>
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewTimer;

    public static void Preview()
    {
        var live = App.Services.GetRequiredService<ILiveActivityService>();
        live.Start();
        var window = new LincBubbleWindow(preview: true);
        window.ShowQuietly();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var step = 0;
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(7);
        timer.IsRepeating = true;
        _previewTimer = timer; // held: an unreferenced timer is collected and stops
        timer.Tick += (_, _) =>
        {
            switch (step++ % 9)
            {
                case 6:
                    live.Post(new LiveActivity("notification", LiveActivityKind.Notification, "", "Thandi", "Are we still meeting at 3? I'm at the library", "WhatsApp", null,
                        [new LiveAction("Mark as read", () => { }), new LiveAction("Reply", () => { }), new LiveAction("Dismiss", () => { })]));
                    live.EndAfter("notification", TimeSpan.FromSeconds(8));
                    break;
                case 7:
                    window.PreviewClick(); // the notification's card
                    break;
                case 8:
                    live.Post(new LiveActivity("clipboard", LiveActivityKind.Clipboard, "", "Copied from your phone", "https://github.com/deloweProjects/Linc", null, null));
                    live.EndAfter("clipboard", TimeSpan.FromSeconds(3.5));
                    break;
                case 2:
                    window.PreviewClick(); // the song's card
                    break;
                case 5:
                    window.PreviewClick(); // nothing live: the shortcuts
                    break;
                case 0:
                    live.Post(new LiveActivity("media", LiveActivityKind.Media, "", "Uhlanya Iwami", "Caeser - Topic", null, null,
                        [new LiveAction("Previous", () => { }, Glyph: ""), new LiveAction("Pause", () => { }, Glyph: ""), new LiveAction("Next", () => { }, Glyph: "")]));
                    break;
                case 3:
                    live.Post(new LiveActivity("share:1", LiveActivityKind.Share, "", "holiday-photos.zip", "Sending to Pixel 7 · 42%", "42%", 42,
                        [new LiveAction("Cancel", () => { }, Danger: true), new LiveAction("Open Share", () => { })]));
                    break;
                case 4:
                    live.End("share:1");
                    live.End("media");
                    break;
            }
        };
        timer.Start();
    }
}
