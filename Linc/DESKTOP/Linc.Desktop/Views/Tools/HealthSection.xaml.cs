using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Linc.Desktop.Views.Tools;

/// <summary>Tools › Health. The view model keeps the samples; this draws the two graphs.</summary>
public sealed partial class HealthSection : UserControl
{
    public HealthToolViewModel ViewModel { get; } = App.Services.GetRequiredService<HealthToolViewModel>();

    public HealthSection()
    {
        InitializeComponent();
        Loaded += (_, _) => { ViewModel.SamplesChanged += Redraw; Redraw(); };
        Unloaded += (_, _) => ViewModel.SamplesChanged -= Redraw;
        CpuGraph.SizeChanged += (_, _) => Redraw();
        MemoryGraph.SizeChanged += (_, _) => Redraw();
    }

    private void Redraw()
    {
        Draw(CpuLine, CpuGraph, ViewModel.CpuSamples);
        Draw(MemoryLine, MemoryGraph, ViewModel.MemorySamples);
    }

    // The newest sample sits at the right edge; the graph fills from the right as samples arrive.
    private static void Draw(Polyline line, FrameworkElement area, IReadOnlyList<double> samples)
    {
        var points = new PointCollection();
        double w = area.ActualWidth, h = area.ActualHeight;
        if (w > 0 && h > 0 && samples.Count > 1)
        {
            var step = w / (HealthToolViewModel.GraphSamples - 1);
            var start = w - step * (samples.Count - 1);
            for (var i = 0; i < samples.Count; i++)
            {
                var y = h - 3 - (h - 6) * Math.Clamp(samples[i], 0, 100) / 100.0;
                points.Add(new Point(start + step * i, y));
            }
        }
        line.Points = points;
    }

    private async void OnStopApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string package }) await ViewModel.StopAppAsync(package);
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.DetailsText);
        Clipboard.SetContent(package);
    }
}
