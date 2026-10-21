using Linc.Desktop.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Windows.UI;

namespace Linc.Desktop.Views.Tools;

/// <summary>Tools › Apps. Logic is in <see cref="AppsToolViewModel"/>.</summary>
public sealed partial class AppsSection : UserControl
{
    public AppsToolViewModel ViewModel { get; } = App.Services.GetRequiredService<AppsToolViewModel>();

    public AppsSection()
    {
        InitializeComponent();
    }

    // Without the Linc app there are no icons, so each app gets a tile in a colour of its own,
    // the same every time (from its package name).
    private static readonly Color[] TileColors =
    [
        ColorHelper.FromArgb(255, 0x5B, 0x47, 0xB0), ColorHelper.FromArgb(255, 0x1D, 0x8A, 0x5B), ColorHelper.FromArgb(255, 0xC0, 0x56, 0x7D),
        ColorHelper.FromArgb(255, 0x2F, 0x6F, 0xC0), ColorHelper.FromArgb(255, 0xB0, 0x6A, 0x1F), ColorHelper.FromArgb(255, 0x0E, 0x83, 0x8C),
        ColorHelper.FromArgb(255, 0x8A, 0x4F, 0xB8), ColorHelper.FromArgb(255, 0xA8, 0x3E, 0x3E),
    ];

    private static readonly Dictionary<int, SolidColorBrush> TileBrushes = [];

    public static Brush TileBrush(string package)
    {
        var hash = 17;
        foreach (var c in package ?? "") hash = unchecked(hash * 31 + c);
        var index = (int)((uint)hash % (uint)TileColors.Length);
        if (!TileBrushes.TryGetValue(index, out var brush)) TileBrushes[index] = brush = new SolidColorBrush(TileColors[index]);
        return brush;
    }

    private void OnCopyPackage(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPackage.Length == 0) return;
        var package = new DataPackage();
        package.SetText(ViewModel.SelectedPackage);
        Clipboard.SetContent(package);
    }
}
