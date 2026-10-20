using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Linc.Desktop.Views;

/// <summary>
/// Home shrinks to fit instead of cramming (the owner: "Rather shrink the elements to fit as
/// display starts cramming like that"). Its child is laid out at a size at least
/// <see cref="DesignWidth"/> × <see cref="DesignHeight"/> and drawn scaled down by the same
/// factor, so the stage, the phone card and the widgets keep their proportions instead of
/// overlapping or being cut off. At or above the design size nothing changes: the scale is 1 and
/// the child gets exactly the space it always had.
/// <list type="bullet">
/// <item>Height shrinks smoothly down to <see cref="MinScale"/>; below that the content stops
/// shrinking and the <see cref="Host"/> ScrollViewer scrolls it, as a last resort.</item>
/// <item>Width shrinks down to <see cref="MinWidthScale"/>; a window narrower than that is too
/// small to keep the full layout legibly, so the page's own narrow layouts (stage on top, one
/// pane at a time) take over again at full size.</item>
/// </list>
/// The scale is a RenderTransform, so it costs no layout: the child's ActualWidth/ActualHeight
/// stay in its own (unscaled) units, which is what HomePage's width rules read.
/// </summary>
public sealed class FitScale : Panel
{
    /// <summary>The width Home's two panes need to keep the stage beside the widgets.</summary>
    public double DesignWidth { get; set; } = 1130;

    /// <summary>The height the stage needs for its clock and the phone's card without overlapping.</summary>
    public double DesignHeight { get; set; } = 680;

    /// <summary>The smallest scale for height; below it the host scrolls.</summary>
    public double MinScale { get; set; } = 0.7;

    /// <summary>The smallest scale for width; below it width stops driving the scale.</summary>
    public double MinWidthScale { get; set; } = 0.8;

    /// <summary>The scale applied in the last layout pass (1 = untouched).</summary>
    public double Scale { get; private set; } = 1;

    private ScrollViewer? _host;

    /// <summary>
    /// The ScrollViewer this panel sits in. It measures its content with an infinite height, so
    /// the viewport's real height is read from it instead, and a change of it re-measures.
    /// </summary>
    public ScrollViewer? Host
    {
        get => _host;
        set
        {
            if (_host is not null) _host.SizeChanged -= OnHostSizeChanged;
            _host = value;
            if (_host is not null) _host.SizeChanged += OnHostSizeChanged;
            InvalidateMeasure();
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateMeasure();

    private double ScaleFor(double width, double height)
    {
        double byWidth = width / DesignWidth;
        if (byWidth < MinWidthScale) byWidth = 1; // too narrow even shrunk: the narrow layouts take over
        double scale = Math.Min(1, Math.Min(byWidth, height / DesignHeight));
        if (scale > 0.98) scale = 1; // a hair under the design size isn't worth softening the text for
        return Math.Max(MinScale, scale);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? DesignWidth : available.Width;
        double height = !double.IsInfinity(available.Height) ? available.Height
            : _host is { ActualHeight: > 0 } host ? host.ActualHeight
            : DesignHeight;
        double scale = ScaleFor(width, height);
        Scale = scale;
        // Shrunk all the way: the content keeps the height it needs and the host scrolls it.
        double outerHeight = Math.Max(height, DesignHeight * MinScale);
        var logical = new Size(width / scale, outerHeight / scale);
        foreach (var child in Children)
        {
            child.Measure(logical);
        }
        return new Size(width, outerHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = Scale;
        var logical = new Size(finalSize.Width / scale, finalSize.Height / scale);
        foreach (var child in Children)
        {
            if (child.RenderTransform is not ScaleTransform transform)
            {
                transform = new ScaleTransform();
                child.RenderTransform = transform;
            }
            transform.ScaleX = scale;
            transform.ScaleY = scale;
            child.Arrange(new Rect(0, 0, logical.Width, logical.Height));
        }
        return finalSize;
    }
}
