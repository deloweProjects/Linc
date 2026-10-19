using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Linc.Desktop.Views;

/// <summary>
/// Small attached behaviours that let every page fit a small window (the owner: "resize only
/// got better on Home, do it for all"). Window size classes as Material 3 uses them: under
/// 600 px compact, under 840 medium, then expanded.
/// <list type="bullet">
/// <item><c>PageLayout.Adaptive</c> on a page's root: its padding tightens with its width
/// (12 compact, 20 medium, the XAML's own value when there is room).</item>
/// <item><c>PageLayout.StackBelow</c> on a two-column Grid: under that width the second column
/// moves below the first.</item>
/// <item><c>PageLayout.HideBelow</c> on any element: collapsed while the window is narrower
/// (for secondary details such as a file's date).</item>
/// </list>
/// </summary>
public static class PageLayout
{
    public const double Compact = 600, Medium = 840;

    // ---- Adaptive padding ------------------------------------------------------------------

    public static readonly DependencyProperty AdaptiveProperty =
        DependencyProperty.RegisterAttached("Adaptive", typeof(bool), typeof(PageLayout), new PropertyMetadata(false, OnAdaptiveChanged));

    public static bool GetAdaptive(DependencyObject o) => (bool)o.GetValue(AdaptiveProperty);
    public static void SetAdaptive(DependencyObject o, bool value) => o.SetValue(AdaptiveProperty, value);

    private static readonly DependencyProperty WidePaddingProperty =
        DependencyProperty.RegisterAttached("WidePadding", typeof(Thickness?), typeof(PageLayout), new PropertyMetadata(null));

    private static void OnAdaptiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.SizeChanged += (_, args) => ApplyPadding(element, args.NewSize.Width);
    }

    private static void ApplyPadding(FrameworkElement element, double width)
    {
        var wide = element.GetValue(WidePaddingProperty) as Thickness? ?? Read(element);
        element.SetValue(WidePaddingProperty, wide);
        double side = width < Compact ? 12 : width < Medium ? 20 : wide.Left;
        double top = width < Compact ? 12 : width < Medium ? Math.Min(20, wide.Top) : wide.Top;
        Write(element, new Thickness(side, top, side, width < Medium ? top : wide.Bottom));
    }

    private static Thickness Read(FrameworkElement e) => e switch
    {
        Grid g => g.Padding,
        StackPanel s => s.Padding,
        Border b => b.Padding,
        Control c => c.Padding,
        _ => new Thickness(0),
    };

    private static void Write(FrameworkElement e, Thickness t)
    {
        switch (e)
        {
            case Grid g: g.Padding = t; break;
            case StackPanel s: s.Padding = t; break;
            case Border b: b.Padding = t; break;
            case Control c: c.Padding = t; break;
        }
    }

    // ---- Two columns that stack ------------------------------------------------------------

    public static readonly DependencyProperty StackBelowProperty =
        DependencyProperty.RegisterAttached("StackBelow", typeof(double), typeof(PageLayout), new PropertyMetadata(0.0, OnStackBelowChanged));

    public static double GetStackBelow(DependencyObject o) => (double)o.GetValue(StackBelowProperty);
    public static void SetStackBelow(DependencyObject o, double value) => o.SetValue(StackBelowProperty, value);

    private static void OnStackBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || grid.ColumnDefinitions.Count < 2) return;
        var secondWidth = grid.ColumnDefinitions[1].Width;
        bool stacked = false;
        grid.SizeChanged += (_, args) =>
        {
            bool want = args.NewSize.Width > 0 && args.NewSize.Width < GetStackBelow(grid);
            if (want == stacked) return;
            stacked = want;
            while (grid.RowDefinitions.Count < 2) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions[1].Width = want ? new GridLength(0) : secondWidth;
            foreach (var child in grid.Children.OfType<FrameworkElement>())
            {
                // Remember where each child came from, so widening puts it back.
                if (child.GetValue(HomeColumnProperty) is not int home)
                {
                    home = Grid.GetColumn(child);
                    child.SetValue(HomeColumnProperty, home);
                }
                if (home == 1)
                {
                    Grid.SetColumn(child, want ? 0 : 1);
                    Grid.SetRow(child, want ? 1 : 0);
                }
            }
            grid.RowSpacing = want ? Math.Max(grid.RowSpacing, grid.ColumnSpacing) : grid.RowSpacing;
        };
    }

    private static readonly DependencyProperty HomeColumnProperty =
        DependencyProperty.RegisterAttached("HomeColumn", typeof(object), typeof(PageLayout), new PropertyMetadata(null));

    // ---- Hidden on a narrow window ---------------------------------------------------------

    public static readonly DependencyProperty HideBelowProperty =
        DependencyProperty.RegisterAttached("HideBelow", typeof(double), typeof(PageLayout), new PropertyMetadata(0.0, OnHideBelowChanged));

    public static double GetHideBelow(DependencyObject o) => (double)o.GetValue(HideBelowProperty);
    public static void SetHideBelow(DependencyObject o, double value) => o.SetValue(HideBelowProperty, value);

    private static void OnHideBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        void Apply()
        {
            if (element.XamlRoot is { } root)
                element.Visibility = root.Size.Width < GetHideBelow(element) ? Visibility.Collapsed : Visibility.Visible;
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Apply();
        element.Loaded += (_, _) =>
        {
            Apply();
            if (element.XamlRoot is { } root) root.Changed += OnRootChanged;
        };
        element.Unloaded += (_, _) =>
        {
            if (element.XamlRoot is { } root) root.Changed -= OnRootChanged;
        };
    }
}
