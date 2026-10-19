using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;

namespace Linc.Desktop.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    /// <summary>The link notice shown in the phone card (replaces the old red banner).</summary>
    public AppShellViewModel Shell { get; }

    /// <summary>Empty-state CTA (M2b): open the guided wizard — the same door as the tab-strip "+".</summary>
    private void OnPairPhone(object sender, RoutedEventArgs e)
    {
        var shell = App.Services.GetRequiredService<Linc.Desktop.ViewModels.AppShellViewModel>();
        shell.IsOnboarding = true;
    }

    /// <summary>Shared tab: pick PC files and push them to the phone (the picker needs a HWND).</summary>
    private async void OnSendToPhone(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await ViewModel.SendFilesToPhoneAsync(files.Select(f => f.Path).ToList());
        }
    }

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        Shell = App.Services.GetRequiredService<AppShellViewModel>();
        InitializeComponent();
        ApplyEmptyState();

        // GradientStop.Color can't be a compiled-binding target, so the preview
        // card's palette gradient is wired here instead.
        ApplyGradient();
        ApplyPaneOrder();
        ApplyAppsHeight();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.GradientTop) or nameof(HomeViewModel.GradientBottom))
            {
                ApplyGradient();
            }
            else if (e.PropertyName is nameof(HomeViewModel.HasNoVisibleDevice))
            {
                ApplyEmptyState();
            }
            else if (e.PropertyName is nameof(HomeViewModel.PanesSwapped))
            {
                ApplyPaneOrder();
                if (_narrow) ApplyWidth(ActualWidth);
            }
            else if (e.PropertyName is nameof(HomeViewModel.AppsPaneHeight)
                     or nameof(HomeViewModel.ShowApps))
            {
                // Device switch, a HomeChanged echo (which ApplyAppsHeight filters out), or the
                // Apps section being switched off/on — which collapses or restores its rows
                // (M15b Part F).
                ApplyAppsHeight();
            }
        };

        // A small window gets one pane at a time (the owner: "isn't adjusting to small windows").
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        WidgetsPane.SizeChanged += (_, e) => ApplyStageLayout(e.NewSize.Width);

        // The clock on the stage: ticks while Home is on screen, follows Settings › Appearance.
        _clock.Tick += (_, _) => TickClock();
        Loaded += (_, _) =>
        {
            _registry.AppearanceChanged += OnAppearanceChanged;
            ApplyClock();
        };
        Unloaded += (_, _) =>
        {
            _registry.AppearanceChanged -= OnAppearanceChanged;
            _clock.Stop();
        };

        // The row heights only become meaningful once the page has a size; re-clamp then so a
        // persisted height saved on a bigger window cannot swallow the tabs panel on a smaller one.
        Loaded += (_, _) =>
        {
            // M15b Part F: guarded on ShowApps, because re-clamping here unconditionally would
            // put a pixel height back on a row that Apps-off had just collapsed to zero.
            if (ViewModel.ShowApps && ViewModel.AppsPaneHeight is { } h) SetAppsHeight(h);
        };

        // Seek: suppress position updates while the user holds the thumb; send one
        // seek when they release. handledEventsToo because Slider handles these itself.
        SeekSlider.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => ViewModel.BeginSeek()), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent,
            new PointerEventHandler((_, _) => ViewModel.SeekTo(SeekSlider.Value)), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler((_, _) => ViewModel.SeekTo(SeekSlider.Value)), handledEventsToo: true);

        // Resize cursor over the draggable divider.
        Splitter.PointerEntered += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Splitter.PointerExited += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

        // Same treatment for the horizontal divider between the tabs panel and Apps (M6c-3).
        AppsSplitter.PointerEntered += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        AppsSplitter.PointerExited += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    // ---- Apps grid virtualization + lazy icons (M7b) ----
    //
    // These two handlers are the ONLY icon trigger left. The repeater raises ElementPrepared for a
    // tile as it comes on screen and ElementClearing as its container is recycled, so on a phone
    // with a few hundred apps the view model is asked for a screenful of icons, not the list.

    private void OnAppTilePrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // The index is the repeater's own, into the same collection the view model holds.
        var app = args.Index >= 0 && args.Index < ViewModel.Apps.Count ? ViewModel.Apps[args.Index] : null;
        ViewModel.OnAppTileRealized(app);
    }

    private void OnAppTileClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        // The container's DataContext IS the AppVm being recycled (the repeater sets it), which
        // is more reliable than re-reading by index here — items may have moved under it.
        ViewModel.OnAppTileUnrealized((args.Element as FrameworkElement)?.DataContext as AppVm);
    }

    // ---- The stage: the phone's wallpaper at its own shape, with the clock ----

    private readonly IDeviceRegistry _registry = App.Services.GetRequiredService<IDeviceRegistry>();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };

    private void OnAppearanceChanged() => DispatcherQueue.TryEnqueue(ApplyClock);

    private void ApplyClock()
    {
        bool show = _registry.HomeClock;
        Clock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            TickClock();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("t");
        ClockDate.Text = now.ToString("dddd, d MMMM");
    }

    /// <summary>The stage's width beside the widgets (the owner: "a bit bigger"; was 340).</summary>
    private const double StageWidth = 384;

    /// <summary>Below this the pane can't hold the stage AND a widget column beside it.</summary>
    private const double StageBesideWidth = 700;
    private bool? _stageBeside;

    /// <summary>
    /// A phone wallpaper is portrait, so on a wide pane the stage is a tall card down the side
    /// showing it whole (cropping a portrait picture to a wide card loses most of it). On a narrow
    /// pane the stage sits on top of the widgets, sized to its clock and card, the wallpaper's
    /// middle behind them.
    /// </summary>
    private void ApplyStageLayout(double width)
    {
        bool beside = width >= StageBesideWidth;
        if (_stageBeside == beside) return;
        _stageBeside = beside;
        Grid.SetRow(Stage, 0);
        Grid.SetColumn(Stage, 0);
        Grid.SetRowSpan(Stage, beside ? 2 : 1);
        Grid.SetColumnSpan(Stage, beside ? 1 : 2);
        Stage.Width = beside ? StageWidth : double.NaN;
        Stage.VerticalAlignment = beside ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        ClockTime.FontSize = beside ? 104 : 68;
        Grid.SetRow(WidgetsArea, beside ? 0 : 1);
        Grid.SetColumn(WidgetsArea, beside ? 1 : 0);
        Grid.SetRowSpan(WidgetsArea, beside ? 2 : 1);
        Grid.SetColumnSpan(WidgetsArea, beside ? 1 : 2);
    }

    /// <summary>
    /// No phone at all: only the "Pair a phone" card shows, on the app's backdrop. The panes are
    /// made invisible rather than collapsed, because the narrow-window switch owns their
    /// Visibility (ApplyWidth) and the two must not fight.
    /// </summary>
    private void ApplyEmptyState()
    {
        bool empty = ViewModel.HasNoVisibleDevice;
        foreach (var pane in new UIElement[] { WidgetsPane, Splitter, RightPane, NarrowSwitch })
        {
            pane.Opacity = empty ? 0 : 1;
            pane.IsHitTestVisible = !empty;
        }
    }

    private void ApplyGradient()
    {
        GradientTopStop.Color = ViewModel.GradientTop;
        GradientBottomStop.Color = ViewModel.GradientBottom;
    }

    // ---- Apps / tabs split (M6c-3) ----
    //
    // The M6c-2 TabsPane height-sync is GONE, and must not come back: it only existed because the
    // right column was a ScrollViewer, which measures its content with infinite height. RightPane
    // is a Grid now, so its rows bound TabsPane for free and the tab lists keep scrolling
    // internally without anyone assigning a Height.

    /// <summary>Minimum for the tabs panel: it holds list content, so a few hundred px.</summary>
    private const double TabsMinHeight = 260;

    // The Apps minimum is "roughly two tile rows", derived from the tile metrics in the XAML's
    // UniformGridLayout rather than picked as a magic number, so retuning the tiles retunes it.
    private const double AppsTileHeight = 98;   // UniformGridLayout MinItemHeight
    private const double AppsTileRowSpacing = 10; // UniformGridLayout MinRowSpacing
    private const double AppsCardChrome = 70;   // card padding (20+20) + the "Apps" title + row spacing
    private const double AppsMinHeight = (AppsTileHeight * 2) + AppsTileRowSpacing + AppsCardChrome;

    /// <summary>The last Apps height this page applied, so a HomeChanged echo is a no-op.</summary>
    private double? _appliedAppsHeight;

    /// <summary>Total height the two panes plus the splitter share.</summary>
    private double SplitAvailableHeight => RightPane.ActualHeight - AppsSplitter.ActualHeight;

    /// <summary>
    /// Applies the persisted split (null = the default 65/35 star rows the XAML declares). Called on
    /// load, on device switch and on a HomeChanged echo; the echo is filtered by
    /// <see cref="_appliedAppsHeight"/> so re-applying our own saved value cannot re-enter a resize.
    /// </summary>
    private void ApplyAppsHeight()
    {
        // M15b Part F: Apps switched off gives its space away instead of leaving a gap. This is
        // checked FIRST — ahead of the echo filter below — because a collapse must never be
        // skipped as "a value we already applied". Both rows go to zero: the Apps row and the
        // splitter row, because hiding only the card would leave the split's share of the column
        // empty, which is the gap the owner reported. Note this is "removed", not the "disabled"
        // state the Apps grid's Opacity expresses on a pre-v16 phone — those are different
        // things and only one of them reclaims space (M6c).
        if (!ViewModel.ShowApps)
        {
            TabsRow.Height = new GridLength(1, GridUnitType.Star);
            AppsSplitterRow.Height = new GridLength(0, GridUnitType.Pixel);
            AppsRow.Height = new GridLength(0, GridUnitType.Pixel);
            // Forget what was applied, so switching Apps back on always re-applies the persisted
            // split rather than being filtered out as an echo of a height the rows no longer have.
            _appliedAppsHeight = null;
            return;
        }
        AppsSplitterRow.Height = GridLength.Auto;

        var height = ViewModel.AppsPaneHeight;
        if (_appliedAppsHeight is not null && height is not null
            && Math.Abs(_appliedAppsHeight.Value - height.Value) < 0.5)
        {
            return; // our own value, echoed back
        }
        _appliedAppsHeight = height;

        if (height is null)
        {
            TabsRow.Height = new GridLength(65, GridUnitType.Star);
            AppsRow.Height = new GridLength(35, GridUnitType.Star);
            return;
        }

        SetAppsHeight(height.Value);
    }

    /// <summary>Puts a clamped pixel height on the Apps row; the tabs row keeps the rest (star).</summary>
    private void SetAppsHeight(double requested)
    {
        var available = SplitAvailableHeight;
        // Before the first layout pass ActualHeight is 0; clamp only against the minimum then and
        // let the next drag/resize tighten it.
        var max = available > 0 ? Math.Max(AppsMinHeight, available - TabsMinHeight) : double.MaxValue;
        var clamped = Math.Clamp(requested, AppsMinHeight, max);
        TabsRow.Height = new GridLength(1, GridUnitType.Star);
        AppsRow.Height = new GridLength(clamped, GridUnitType.Pixel);
    }

    // Drag the horizontal divider: resize the Apps pane; the tabs panel (star height) takes the
    // rest. This is a SIBLING of OnSplitterDrag rather than shared code — the two differ in axis,
    // in which element they size, in their clamp domain and in whether they persist — but it keeps
    // the same shape deliberately: delta → clamp against a min/max derived from the container →
    // assign a pixel length.
    private void OnAppsSplitterDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        // Dragging DOWN grows the tabs panel, so it shrinks Apps.
        SetAppsHeight(AppsRow.ActualHeight - e.Delta.Translation.Y);
    }

    /// <summary>Persists the dragged split once, when the drag ends (not on every delta).</summary>
    private void OnAppsSplitterDragCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        var height = Math.Round(AppsRow.ActualHeight);
        _appliedAppsHeight = height;
        ViewModel.SaveAppsPaneHeight(height);
    }

    // ---- Pane order (M6b) ----

    private const double WidgetsMinWidth = 320;
    private const double PanelMinWidth = 360;

    /// <summary>
    /// The widgets pane's pixel width, kept across a flip so a dragged size survives it. Null until
    /// the divider is dragged: then it follows the window, about three fifths of it, so the stage
    /// and a column of widgets fit beside the activity panel.
    /// </summary>
    private double? _widgetsWidth;

    private double WidgetsWidth
    {
        get
        {
            if (_widgetsWidth is { } dragged) return dragged;
            var available = ActualWidth - 48 /* page padding */ - 14 /* splitter */;
            if (available <= 0) return 720;
            return Math.Clamp(available * 0.6, WidgetsMinWidth, Math.Max(WidgetsMinWidth, available - PanelMinWidth));
        }
    }

    /// <summary>The column definition currently holding the widgets pane.</summary>
    private ColumnDefinition WidgetsColumn => ViewModel.PanesSwapped ? RightColumn : LeftColumn;

    /// <summary>
    /// Puts the two panes on the sides the layout asks for (M6b). The fixed width follows the
    /// widgets pane rather than the column index — a naive Grid.Column swap would hand the widgets
    /// the star column and squeeze the tabbed panel into 430px — so the widths and MinWidths move
    /// with the panes. The splitter stays in column 1 either way.
    /// </summary>
    // ---- Narrow windows ----
    //
    // Below NarrowWidth the two panes can't both keep their minimums, so they stop sharing the
    // row: the switch above picks one (Phone = the widgets, Activity = the tabs and Apps), and it
    // fills the width. Widening the window puts both back, in the saved order and split.

    private const double NarrowWidth = 820;
    private bool _narrow;
    private bool _narrowShowsPanel;

    private void ApplyWidth(double width)
    {
        bool narrow = width > 0 && width < NarrowWidth;
        if (narrow == _narrow && !narrow)
        {
            if (_widgetsWidth is null) WidgetsColumn.Width = new GridLength(WidgetsWidth, GridUnitType.Pixel);
            return;
        }
        _narrow = narrow;
        NarrowSwitch.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        if (!narrow)
        {
            WidgetsPane.Visibility = Visibility.Visible;
            RightPane.Visibility = Visibility.Visible;
            ApplyPaneOrder();
            return;
        }
        ShowWidgetsButton.IsChecked = !_narrowShowsPanel;
        ShowPanelButton.IsChecked = _narrowShowsPanel;
        WidgetsPane.Visibility = _narrowShowsPanel ? Visibility.Collapsed : Visibility.Visible;
        RightPane.Visibility = _narrowShowsPanel ? Visibility.Visible : Visibility.Collapsed;
        // Whichever is shown takes the whole width, in whichever column it sits.
        var shownColumn = Grid.GetColumn(_narrowShowsPanel ? RightPane : WidgetsPane) == 0 ? LeftColumn : RightColumn;
        var hiddenColumn = ReferenceEquals(shownColumn, LeftColumn) ? RightColumn : LeftColumn;
        shownColumn.MinWidth = 0;
        shownColumn.Width = new GridLength(1, GridUnitType.Star);
        hiddenColumn.MinWidth = 0;
        hiddenColumn.Width = new GridLength(0);
    }

    private void OnShowWidgets(object sender, RoutedEventArgs e)
    {
        _narrowShowsPanel = false;
        ApplyWidth(ActualWidth);
    }

    private void OnShowPanel(object sender, RoutedEventArgs e)
    {
        _narrowShowsPanel = true;
        ApplyWidth(ActualWidth);
    }

    private void ApplyPaneOrder()
    {
        var swapped = ViewModel.PanesSwapped;
        Grid.SetColumn(WidgetsPane, swapped ? 2 : 0);
        // The whole right-hand container moves, not just TabsPane, so Apps travels with the tabs.
        Grid.SetColumn(RightPane, swapped ? 0 : 2);

        var widgets = swapped ? RightColumn : LeftColumn;
        var panel = swapped ? LeftColumn : RightColumn;
        widgets.MinWidth = WidgetsMinWidth;
        widgets.Width = new GridLength(WidgetsWidth, GridUnitType.Pixel);
        panel.MinWidth = PanelMinWidth;
        panel.Width = new GridLength(1, GridUnitType.Star);
    }

    // Drag the divider: resize the widgets pane; the tabbed panel (star width) takes the rest.
    // Which column that is, and which way the drag runs, both depend on the current pane order.
    private void OnSplitterDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var column = WidgetsColumn;
        // Dragging right widens the widgets pane when it sits on the left, narrows it when swapped.
        var delta = ViewModel.PanesSwapped ? -e.Delta.Translation.X : e.Delta.Translation.X;
        var available = ActualWidth - 48 /* page padding */ - Splitter.ActualWidth;
        var max = Math.Max(WidgetsMinWidth, available - PanelMinWidth);
        var width = Math.Clamp(column.ActualWidth + delta, WidgetsMinWidth, max);
        _widgetsWidth = width;
        column.Width = new GridLength(width, GridUnitType.Pixel);
    }
}
