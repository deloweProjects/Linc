using Linc.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace Linc.Desktop.Views;

/// <summary>
/// The Linc Island: shows the most important <see cref="LiveActivity"/> as a compact pill, the
/// next one as a bubble beside it, and opens into a card with buttons when tapped (or by itself
/// for a ringing call). Shown only while Settings › Appearance › Linc Island is on and something
/// is live. Hosted at the top of Home's phone picture and in the title bar on the other pages
/// (<see cref="InTitleBar"/>; MainWindow sets <see cref="Suppressed"/> on Home).
/// </summary>
public sealed partial class DynamicIsland : UserControl
{
    private static readonly Duration Morph = new(TimeSpan.FromMilliseconds(460));
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(200));
    private static readonly TimeSpan AutoCollapse = TimeSpan.FromSeconds(10);

    private readonly ILiveActivityService _island;
    private readonly IDeviceRegistry _registry;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = AutoCollapse };
    private Storyboard? _morph;
    private LiveActivity? _shown;
    private string? _focusId;
    private bool _expanded;
    private bool _pointerOver;
    private bool _suppressed;
    private bool _visible;
    private Size _target;
    private string? _actionsKey;

    public DynamicIsland()
    {
        InitializeComponent();
        _island = App.Services.GetRequiredService<ILiveActivityService>();
        _registry = App.Services.GetRequiredService<IDeviceRegistry>();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_expanded && !_pointerOver && _shown?.AutoExpand != true)
            {
                SetExpanded(false);
            }
        };
        Loaded += (_, _) =>
        {
            _island.Changed += Render;
            _registry.AppearanceChanged += OnAppearanceChanged;
            Render();
        };
        Unloaded += (_, _) =>
        {
            _island.Changed -= Render;
            _registry.AppearanceChanged -= OnAppearanceChanged;
            _collapseTimer.Stop();
        };
    }

    /// <summary>The title-bar host: a slimmer pill.</summary>
    public bool InTitleBar { get; set; }

    /// <summary>
    /// Anchored at its bottom (Home, just above the phone card; owner 2026-11-01): the expanded card
    /// opens upward and the bubble lines up with the pill's bottom.
    /// </summary>
    public bool GrowsUp { get; set; }

    /// <summary>Shown whether or not Settings › Linc Island is on (the desktop bubble has its own setting).</summary>
    public bool IgnoreSetting { get; set; }

    /// <summary>Something is live and the island is showing it.</summary>
    public bool HasActivity => _visible;

    /// <summary>Hidden even with something live (the title-bar island while Home shows its own).</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            Render();
        }
    }

    /// <summary>The pill's size or place changed (MainWindow keeps the title bar clickable under it).</summary>
    public event Action? BoundsChanged;

    /// <summary>Where the island (pill and bubble) is, relative to <paramref name="relativeTo"/>, or null while hidden.</summary>
    public Rect? IslandBounds(UIElement relativeTo)
    {
        if (!_visible || Root.ActualWidth <= 0) return null;
        var width = Math.Max(Root.ActualWidth, _target.Width);
        var height = Math.Max(Root.ActualHeight, _target.Height);
        var origin = Root.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        // The pill grows from the centre, so widen around it to cover the size it is heading for.
        return new Rect(origin.X - (width - Root.ActualWidth) / 2, origin.Y, width, height);
    }

    private double CompactHeight => InTitleBar ? 28 : 32;
    private double ExpandedWidth => InTitleBar ? 320 : 300;
    private double MaxCompactWidth => InTitleBar ? 240 : 230;

    private void OnAppearanceChanged() => DispatcherQueue.TryEnqueue(Render);

    // ---- What to show ----

    private void Render()
    {
        var live = _island.Activities;
        bool on = (_registry.LiveIsland || IgnoreSetting) && !_suppressed && live.Count > 0;
        if (!on)
        {
            Hide();
            return;
        }

        var focus = _focusId is null ? null : live.FirstOrDefault(a => a.Id == _focusId);
        if (focus is null) _focusId = null;
        var top = focus ?? live[0];
        var next = live.FirstOrDefault(a => a.Id != top.Id);

        bool newTop = _shown?.Id != top.Id;
        if (newTop)
        {
            // A new activity at the top opens by itself only when it asks to (a ringing call).
            _expanded = top.AutoExpand || (_focusId is not null && _expanded);
        }
        bool wasVisible = _visible;
        _shown = top;
        Fill(top);
        FillBubble(_expanded ? null : next);
        ExpandedPanel.VerticalAlignment = GrowsUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Bubble.VerticalAlignment = GrowsUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;

        if (!_visible)
        {
            _visible = true;
            Root.Visibility = Visibility.Visible;
            Pill.Width = CompactHeight;
            Pill.Height = CompactHeight;
            Pill.Opacity = 0;
            FadeTo(Pill, 1);
            PopIn();
        }
        else if (newTop && wasVisible && !_expanded)
        {
            SlideIn(CompactRow, CompactShift); // the next thing slides in under the old one
        }
        Resize();
        VisibilityChanged?.Invoke();
    }

    private void Fill(LiveActivity a)
    {
        var accent = Accent(a.Kind);
        LeadDot.Fill = new SolidColorBrush(accent);
        LeadGlyph.Glyph = a.Glyph;
        bool ring = a.Kind == LiveActivityKind.Share && a.Progress is < 100;
        LeadRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        LeadDot.Visibility = ring ? Visibility.Collapsed : Visibility.Visible;
        LeadGlyph.Visibility = LeadDot.Visibility;
        if (ring) LeadRing.Value = a.Progress ?? 0;
        CompactTitle.Text = a.Title;
        CompactTrail.Text = a.Trailing ?? "";
        CompactTrail.Visibility = string.IsNullOrEmpty(a.Trailing) ? Visibility.Collapsed : Visibility.Visible;
        CompactTitle.MaxWidth = MaxCompactWidth - 90;

        ArtTile.Background = a.Art is { } art
            ? new ImageBrush { ImageSource = art, Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(accent);
        ArtGlyph.Visibility = a.Art is null ? Visibility.Visible : Visibility.Collapsed;
        ArtGlyph.Glyph = a.Glyph;
        ExpandedTitle.Text = a.Title;
        ExpandedSubtitle.Text = a.Subtitle ?? "";
        ExpandedSubtitle.Visibility = string.IsNullOrEmpty(a.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ExpandedProgress.Visibility = a.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ExpandedProgress.Value = a.Progress ?? 0;
        FillActions(a.Actions ?? []);
        Describe(a);
    }

    private void FillActions(IReadOnlyList<LiveAction> actions)
    {
        // A share's progress re-posts many times a second: keep the same buttons (a click in
        // flight must land) unless the activity or its buttons really changed.
        var key = _shown?.Id + "|" + string.Join("|", actions.Select(a => a.Label));
        if (key == _actionsKey) return;
        _actionsKey = key;
        ActionsRow.Children.Clear();
        ActionsRow.ColumnDefinitions.Clear();
        ActionsRow.Visibility = actions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            ActionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(0),
                Foreground = action.Danger ? new SolidColorBrush(Colors.White) : Theme("OnSecondaryContainerBrush", Colors.White),
                Background = action.Danger ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A)) : Theme("SecondaryContainerBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                Content = action.Glyph is { } glyph
                    ? new FontIcon { Glyph = glyph, FontSize = 14 }
                    : new TextBlock { Text = action.Label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, action.Label);
            if (action.Glyph is not null) ToolTipService.SetToolTip(button, action.Label);
            button.Click += (_, _) =>
            {
                try { action.Invoke(); }
                catch (Exception) { /* the feature reports its own failures */ }
                if (_shown?.AutoExpand != true) SetExpanded(false);
            };
            Grid.SetColumn(button, i);
            ActionsRow.Children.Add(button);
        }
    }

    private void FillBubble(LiveActivity? next)
    {
        if (next is null || InTitleBar)
        {
            if (Bubble.Visibility == Visibility.Visible) FadeTo(Bubble, 0, () => Bubble.Visibility = Visibility.Collapsed);
            return;
        }
        BubbleDot.Fill = new SolidColorBrush(Accent(next.Kind));
        BubbleGlyph.Glyph = next.Glyph;
        ToolTipService.SetToolTip(Bubble, next.Title);
        Bubble.Height = CompactHeight;
        Bubble.Width = CompactHeight;
        if (Bubble.Visibility != Visibility.Visible)
        {
            Bubble.Visibility = Visibility.Visible;
            FadeTo(Bubble, 1);
        }
    }

    private void Describe(LiveActivity a) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Pill, $"Linc Island: {a.Title}{(a.Subtitle is null ? "" : ", " + a.Subtitle)}");

    // ---- Shape and motion ----

    private void Resize()
    {
        double width, height;
        if (_expanded)
        {
            width = ExpandedWidth;
            ExpandedPanel.Width = width;
            ExpandedPanel.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Ceiling(ExpandedPanel.DesiredSize.Height) + 2;
        }
        else
        {
            CompactRow.Measure(new Size(double.PositiveInfinity, CompactHeight));
            width = Math.Clamp(Math.Ceiling(CompactRow.DesiredSize.Width) + 4, 120, MaxCompactWidth);
            height = CompactHeight;
        }
        Pill.CornerRadius = new CornerRadius(_expanded ? 26 : CompactHeight / 2);
        ExpandedPanel.IsHitTestVisible = _expanded;
        if (_target == default || (_target.Height > CompactHeight) != _expanded)
        {
            FadeTo(CompactRow, _expanded ? 0 : 1);
            FadeTo(ExpandedPanel, _expanded ? 1 : 0);
            if (_expanded) SlideIn(ExpandedPanel, ExpandedShift);
        }
        if (_target != new Size(width, height))
        {
            _target = new Size(width, height);
            Animate(width, height, null);
        }
        if (_expanded) _collapseTimer.Start(); else _collapseTimer.Stop();
    }

    private void Hide()
    {
        _shown = null;
        _focusId = null;
        _expanded = false;
        _collapseTimer.Stop();
        if (!_visible) return;
        _visible = false;
        _target = default;
        FadeTo(Bubble, 0, () => Bubble.Visibility = Visibility.Collapsed);
        FadeTo(Pill, 0);
        PopOut();
        Animate(CompactHeight, CompactHeight, () =>
        {
            if (!_visible)
            {
                Root.Visibility = Visibility.Collapsed;
                BoundsChanged?.Invoke();
                VisibilityChanged?.Invoke();
            }
        });
    }

    /// <summary>Raised when the island appears or goes (the desktop bubble resizes its window).</summary>
    public event Action? VisibilityChanged;

    /// <summary>Appearing: a drop that lands and settles (a spring on the pill's scale).</summary>
    private void PopIn()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(Pill);
            var spring = visual.Compositor.CreateSpringVector3Animation();
            visual.CenterPoint = new Vector3((float)CompactHeight / 2, (float)CompactHeight / 2, 0);
            spring.InitialValue = new Vector3(0.35f, 0.35f, 1);
            spring.FinalValue = Vector3.One;
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(55);
            visual.StartAnimation("Scale", spring);
        }
        catch (Exception)
        {
            // Composition unavailable (remote session): the fade alone still shows it arriving.
        }
    }

    /// <summary>Going: shrinks toward its centre as it fades.</summary>
    private void PopOut()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(Pill);
            var shrink = visual.Compositor.CreateVector3KeyFrameAnimation();
            visual.CenterPoint = new Vector3((float)Pill.ActualWidth / 2, (float)Pill.ActualHeight / 2, 0);
            shrink.InsertKeyFrame(1f, new Vector3(0.6f, 0.6f, 1));
            shrink.Duration = TimeSpan.FromMilliseconds(260);
            visual.StartAnimation("Scale", shrink);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>New content: rises a few pixels into place as it fades in (from below when the island grows up).</summary>
    private void SlideIn(UIElement element, TranslateTransform shift)
    {
        var from = GrowsUp ? 8 : -8;
        var slide = new DoubleAnimation { From = element == CompactRow ? 10 : from, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(320)), EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, "Y");
        var board = new Storyboard();
        board.Children.Add(slide);
        if (element == CompactRow)
        {
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(260)) };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            board.Children.Add(fade);
        }
        board.Begin();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded || _shown is null) return;
        _expanded = expanded;
        if (!expanded) _focusId = null;
        Render();
    }

    /// <summary>The pill's width and height, with a little overshoot, like a drop of ink settling.</summary>
    private void Animate(double width, double height, Action? done)
    {
        _morph?.Stop();
        var from = (Width: Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.Width, Height: Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.Height);
        Pill.Width = from.Width;
        Pill.Height = from.Height;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var w = new DoubleAnimation { From = from.Width, To = width, Duration = Morph, EasingFunction = ease, EnableDependentAnimation = true };
        var h = new DoubleAnimation { From = from.Height, To = height, Duration = Morph, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(w, Pill);
        Storyboard.SetTargetProperty(w, "Width");
        Storyboard.SetTarget(h, Pill);
        Storyboard.SetTargetProperty(h, "Height");
        var board = new Storyboard();
        board.Children.Add(w);
        board.Children.Add(h);
        board.Completed += (_, _) =>
        {
            Pill.Width = width;
            Pill.Height = height;
            BoundsChanged?.Invoke();
            done?.Invoke();
        };
        _morph = board;
        board.Begin();
        BoundsChanged?.Invoke();
    }

    private static void FadeTo(UIElement element, double to, Action? done = null)
    {
        var fade = new DoubleAnimation { To = to, Duration = Fade };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fade);
        board.Completed += (_, _) =>
        {
            element.Opacity = to;
            done?.Invoke();
        };
        board.Begin();
    }

    /// <summary>An app palette brush (kept live by ThemeSyncService, which retints it in place).</summary>
    private static Brush Theme(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    /// <summary>The kind's colour; music and files take the app's own primary colour.</summary>
    private static Color Accent(LiveActivityKind kind) =>
        kind is LiveActivityKind.Share or LiveActivityKind.Media && Theme("PrimaryBrush", default) is SolidColorBrush { Color: var primary } && primary.A > 0
            ? primary
            : FixedAccent(kind);

    private static Color FixedAccent(LiveActivityKind kind) => kind switch
    {
        LiveActivityKind.Call => Color.FromArgb(0xFF, 0x34, 0xC7, 0x59),
        LiveActivityKind.Link => Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A),
        LiveActivityKind.Share => Color.FromArgb(0xFF, 0x8F, 0x6A, 0xD8),
        LiveActivityKind.Mirror => Color.FromArgb(0xFF, 0x5B, 0x5B, 0xD6),
        LiveActivityKind.Battery => Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A),
        _ => Color.FromArgb(0xFF, 0xE0, 0x45, 0x7B),
    };

    // ---- Input ----

    private void OnPillTapped(object sender, TappedRoutedEventArgs e)
    {
        // Taps on the buttons inside belong to them.
        if (_expanded && e.OriginalSource is DependencyObject source && IsInside(source, ActionsRow)) return;
        SetExpanded(!_expanded);
    }

    private void OnBubbleTapped(object sender, TappedRoutedEventArgs e)
    {
        var next = _island.Activities.FirstOrDefault(a => a.Id != _shown?.Id);
        if (next is null) return;
        _focusId = next.Id;
        _expanded = true;
        Render();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOver = true;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        if (_expanded) _collapseTimer.Start();
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (n == container) return true;
        }
        return false;
    }
}
