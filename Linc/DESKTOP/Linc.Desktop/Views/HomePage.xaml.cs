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
        InitializeComponent();

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
            else if (e.PropertyName is nameof(HomeViewModel.PanesSwapped))
            {
                ApplyPaneOrder();
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

    /// <summary>The widgets pane's pixel width, kept across a flip so a dragged size survives it.</summary>
    private double _widgetsWidth = 430;

    /// <summary>The column definition currently holding the widgets pane.</summary>
    private ColumnDefinition WidgetsColumn => ViewModel.PanesSwapped ? RightColumn : LeftColumn;

    /// <summary>
    /// Puts the two panes on the sides the layout asks for (M6b). The fixed width follows the
    /// widgets pane rather than the column index — a naive Grid.Column swap would hand the widgets
    /// the star column and squeeze the tabbed panel into 430px — so the widths and MinWidths move
    /// with the panes. The splitter stays in column 1 either way.
    /// </summary>
    private void ApplyPaneOrder()
    {
        var swapped = ViewModel.PanesSwapped;
        Grid.SetColumn(WidgetsPane, swapped ? 2 : 0);
        // The whole right-hand container moves, not just TabsPane, so Apps travels with the tabs.
        Grid.SetColumn(RightPane, swapped ? 0 : 2);

        var widgets = swapped ? RightColumn : LeftColumn;
        var panel = swapped ? LeftColumn : RightColumn;
        widgets.MinWidth = WidgetsMinWidth;
        widgets.Width = new GridLength(_widgetsWidth, GridUnitType.Pixel);
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
        _widgetsWidth = Math.Clamp(column.ActualWidth + delta, WidgetsMinWidth, max);
        column.Width = new GridLength(_widgetsWidth, GridUnitType.Pixel);
    }
}
