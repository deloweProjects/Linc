using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Linc.Desktop.Views;

/// <summary>
/// Home's widgets as a grid that reflows instead of stretching (the owner: "don't stretch the
/// widgets, shift them around grid style under the phone"). Widgets keep a comfortable width
/// (<see cref="MinColumnWidth"/>–<see cref="MaxColumnWidth"/>); as the pane widens, another
/// column opens and each widget drops into the shortest one, like a masonry board. Children
/// marked <see cref="FullRowProperty"/> (the phone card, the options row) span every column and
/// start a fresh row under everything before them.
/// </summary>
public sealed class WidgetGrid : Panel
{
    public double MinColumnWidth { get; set; } = 300;
    public double MaxColumnWidth { get; set; } = 420;
    public double Spacing { get; set; } = 14;

    public static readonly DependencyProperty FullRowProperty =
        DependencyProperty.RegisterAttached("FullRow", typeof(bool), typeof(WidgetGrid), new PropertyMetadata(false, OnFullRowChanged));

    public static bool GetFullRow(DependencyObject o) => (bool)o.GetValue(FullRowProperty);
    public static void SetFullRow(DependencyObject o, bool value) => o.SetValue(FullRowProperty, value);

    private static void OnFullRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((d as FrameworkElement)?.Parent as WidgetGrid)?.InvalidateMeasure();

    private readonly List<Rect> _slots = [];

    private (int Columns, double ColumnWidth, double Offset) Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return (1, MaxColumnWidth, 0);
        int columns = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        double column = Math.Min(MaxColumnWidth, (width - Spacing * (columns - 1)) / columns);
        double used = columns * column + Spacing * (columns - 1);
        return (columns, column, Math.Max(0, (width - used) / 2)); // centred, never stretched
    }

    protected override Size MeasureOverride(Size available)
    {
        var (columns, columnWidth, offset) = Columns(available.Width);
        double fullWidth = columns * columnWidth + Spacing * (columns - 1);
        var heights = new double[columns];
        _slots.Clear();
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                _slots.Add(Rect.Empty);
                continue;
            }
            if (GetFullRow(child))
            {
                child.Measure(new Size(fullWidth, double.PositiveInfinity));
                double top = heights.Max();
                _slots.Add(new Rect(offset, top, fullWidth, child.DesiredSize.Height));
                double next = top + child.DesiredSize.Height + Spacing;
                for (int i = 0; i < columns; i++) heights[i] = next;
            }
            else
            {
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                int shortest = Array.IndexOf(heights, heights.Min());
                _slots.Add(new Rect(offset + shortest * (columnWidth + Spacing), heights[shortest], columnWidth, child.DesiredSize.Height));
                heights[shortest] += child.DesiredSize.Height + Spacing;
            }
        }
        double height = Math.Max(0, heights.Max() - Spacing);
        return new Size(double.IsInfinity(available.Width) ? fullWidth : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (int i = 0; i < Children.Count && i < _slots.Count; i++)
        {
            var slot = _slots[i];
            Children[i].Arrange(slot.IsEmpty ? new Rect(0, 0, 0, 0) : slot);
        }
        return final;
    }
}
