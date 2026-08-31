using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Linc.AppGridProbe;

/// <summary>
/// Measures how many tiles the Apps grid's ItemsRepeater actually realizes for a large app list.
///
/// <para>Three arrangements, so the number means something:</para>
/// <list type="bullet">
///   <item><c>--virtualized</c> (default) — what HomePage.xaml ships: the repeater is the
///   ScrollViewer's direct content.</item>
///   <item><c>--wrapped</c> — the same with a StackPanel between them.</item>
///   <item><c>--unbounded</c> — no scrolling host at all.</item>
/// </list>
///
/// <para><b>What this measured, and it is not the folklore (M7b):</b> all three virtualize.
/// <see cref="ItemsRepeater"/> decides what to realize from <c>EffectiveViewportChanged</c> — the
/// viewport the framework propagates down from the window — <b>not</b> from the height it is
/// measured with. So an intervening StackPanel does not make it realize every item, and neither
/// does having no ScrollViewer: the window itself still supplies a viewport. The "a StackPanel
/// wrapper silently kills virtualization" rule is true of the old ItemsStackPanel/ItemsControl
/// world and NOT of ItemsRepeater. The nesting still matters for scrolling and for the card's
/// height, which is why applaunchsim keeps a structural check on it — but the icon-fetch cost
/// this milestone was really about was never the repeater's; it was the view model's eager sweep
/// over the whole list, which M7b removes.</para>
///
/// <para>Nothing here reads or writes any store, talks to a phone, or moves the pointer: it opens
/// a window, lets WinUI lay it out, scrolls it programmatically, prints numbers and exits.</para>
/// </summary>
internal static class Program
{
    /// <summary>Matches HomePage.xaml's UniformGridLayout exactly — different metrics, different count.</summary>
    internal const double TileWidth = 92;
    internal const double TileHeight = 98;
    internal const double ColumnSpacing = 6;
    internal const double RowSpacing = 10;

    /// <summary>The Apps card's own scroll viewport in a normal window, near enough.</summary>
    internal const int ViewportWidth = 640;
    internal const int ViewportHeight = 420;

    /// <summary>"A few hundred apps" — more than any real phone's launcher list.</summary>
    internal const int ItemCount = 500;

    internal static bool Wrapped;
    internal static bool Unbounded;
    internal static int ExitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        Wrapped = args.Contains("--wrapped", StringComparer.OrdinalIgnoreCase);
        Unbounded = args.Contains("--unbounded", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("=== Linc Apps-grid Virtualization Probe (appgridprobe) ===");
        Console.WriteLine(
            Unbounded ? "    mode: --unbounded   (no scrolling host at all — the negative control)"
            : Wrapped ? "    mode: --wrapped     (StackPanel between the ScrollViewer and the repeater)"
            : "    mode: --virtualized (repeater is the ScrollViewer's direct content — what ships)");

        try
        {
            // What the generated Main would have done for us; we supply our own, so by hand.
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Microsoft.UI.Xaml.Application.Start(callbackParams =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                // The Application subclass registers itself with the framework in its constructor.
                new ProbeApp();
            });
        }
        catch (Exception ex)
        {
            // A probe that dies silently would be indistinguishable from one that passed.
            Console.WriteLine($"    FAIL: the probe could not start WinUI: {ex}");
            return 2;
        }
        return ExitCode;
    }
}

public sealed partial class ProbeApp : Application
{
    private Window? _window;
    private ItemsRepeater? _repeater;
    private ScrollViewer? _scroller;

    private int _realized;
    private int _peak;
    private int _cleared;

    public ProbeApp() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var items = Enumerable.Range(0, Program.ItemCount).Select(i => $"App {i:D3}").ToList();

        _repeater = new ItemsRepeater
        {
            ItemsSource = items,
            Layout = new UniformGridLayout
            {
                MinItemWidth = Program.TileWidth,
                MinItemHeight = Program.TileHeight,
                MinColumnSpacing = Program.ColumnSpacing,
                MinRowSpacing = Program.RowSpacing,
                ItemsStretch = UniformGridLayoutItemsStretch.Fill,
            },
            // A tile shaped like the real one: a 48px icon plate and a label under it.
            ItemTemplate = (DataTemplate)XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <StackPanel Spacing="8" Padding="4,6">
                    <Border Width="48" Height="48" CornerRadius="12" HorizontalAlignment="Center"
                            Background="#22808080" />
                    <TextBlock Text="{Binding}" FontSize="11" TextAlignment="Center"
                               HorizontalAlignment="Center" />
                  </StackPanel>
                </DataTemplate>
                """),
        };
        _repeater.ElementPrepared += (_, _) =>
        {
            _realized++;
            _peak = Math.Max(_peak, _realized);
        };
        _repeater.ElementClearing += (_, _) =>
        {
            _realized--;
            _cleared++;
        };

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = Program.ViewportWidth,
            Height = Program.ViewportHeight,
        };

        UIElement root;
        if (Program.Unbounded)
        {
            // Intended as the negative control: NO scrolling host, the repeater in a StackPanel
            // measured with infinite height — the shape the Apps card had before M6c-3. It still
            // virtualizes (see the class comment): the window's own effective viewport reaches the
            // repeater regardless. Kept because that result is the point.
            var wrapper = new StackPanel { Width = Program.ViewportWidth };
            wrapper.Children.Add(_repeater);
            root = wrapper;
        }
        else
        {
            if (Program.Wrapped)
            {
                var wrapper = new StackPanel();
                wrapper.Children.Add(_repeater);
                _scroller.Content = wrapper;
            }
            else
            {
                _scroller.Content = _repeater;
            }
            root = _scroller;
        }

        _window = new Window { Title = "appgridprobe" };
        _window.Content = new Grid { Children = { root } };
        _window.Activate();

        Console.WriteLine($"    tree: {DescribeTree(root)}");

        _ = RunAsync(items.Count);
    }

    private async Task RunAsync(int itemCount)
    {
        var columns = (int)Math.Floor(
            (Program.ViewportWidth + Program.ColumnSpacing) / (Program.TileWidth + Program.ColumnSpacing));
        var rows = (int)Math.Ceiling(
            Program.ViewportHeight / (Program.TileHeight + Program.RowSpacing));

        await SettleAsync();
        Console.WriteLine(
            $"    {itemCount} items, viewport {Program.ViewportWidth}x{Program.ViewportHeight} " +
            $"(~{columns} columns x ~{rows} visible rows)");
        Console.WriteLine($"    realized after first layout : {_realized} (peak {_peak})");

        // Scroll to the middle and to the end: a repeater that virtualizes recycles containers,
        // so the peak stays flat while the number of PREPARED events climbs.
        if (!Program.Unbounded)
        {
            _scroller!.ChangeView(null, _scroller.ScrollableHeight / 2, null, disableAnimation: true);
            await SettleAsync();
            Console.WriteLine($"    realized after scrolling 50% : {_realized} (peak {_peak}, {_cleared} recycled)");

            _scroller.ChangeView(null, _scroller.ScrollableHeight, null, disableAnimation: true);
            await SettleAsync();
            Console.WriteLine($"    realized at the end          : {_realized} (peak {_peak}, {_cleared} recycled)");
        }

        // The bar M7b sets: a screenful plus a margin. The margin is not arbitrary — ItemsRepeater
        // keeps VerticalCacheLength viewports of realized elements either side of the visible one,
        // and that property defaults to 2.0, so a correctly virtualizing grid settles at up to five
        // viewports' worth. One extra row of slack on top of that. The failing case does not come
        // anywhere near this bound: it realizes every one of the items.
        var screenful = columns * rows;
        var bound = (screenful * 5) + columns;
        var pass = _peak <= bound;
        Console.WriteLine();
        Console.WriteLine($"    a screenful is ~{screenful} tiles; the bound for this run is {bound}.");
        Console.WriteLine(pass
            ? $"    PASS: never more than {_peak} tiles realized at once, of {itemCount}."
            : $"    FAIL: {_peak} tiles realized at once, of {itemCount} — the grid is NOT virtualizing.");

        Console.WriteLine();
        Console.WriteLine($"RESULT peak={_peak} items={itemCount} bound={bound} verdict={(pass ? "PASS" : "FAIL")}");
        Program.ExitCode = pass ? 0 : 1;
        _window!.Close();
        Exit();
    }

    /// <summary>The arrangement actually under test, printed so a run's numbers are unambiguous.</summary>
    private static string DescribeTree(UIElement root) => root switch
    {
        ScrollViewer { Content: StackPanel } => "ScrollViewer > StackPanel > ItemsRepeater",
        ScrollViewer => "ScrollViewer > ItemsRepeater",
        StackPanel => "StackPanel > ItemsRepeater (no scrolling host)",
        _ => root.GetType().Name,
    };

    /// <summary>Waits for WinUI to finish a layout pass (and the one it schedules after it).</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100);
        }
    }
}
