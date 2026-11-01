using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Linc.Desktop.Views;

/// <summary>
/// PC ⇄ phone with three dots running the way a file is going (owner, 2026-11-01: "I gotta see
/// what's being shared"). Shown beside a moving file on the Share page; stops when collapsed.
/// </summary>
public sealed class TransferFlight : UserControl
{
    private readonly Canvas _track = new() { Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse[] _dots = new Ellipse[3];
    private Storyboard? _board;

    /// <summary>True: PC → phone (dots run right, towards the phone). False: phone → PC.</summary>
    public bool ToPhone { get; set; } = true;

    public TransferFlight()
    {
        var grid = new Grid { ColumnSpacing = 10, Height = 22, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pc = new FontIcon { Glyph = "", FontSize = 16, Foreground = Brush("PrimaryBrush") };
        var phone = new FontIcon { Glyph = "", FontSize = 16, Foreground = Brush("PrimaryBrush") };
        Grid.SetColumn(_track, 1);
        Grid.SetColumn(phone, 2);
        grid.Children.Add(pc);
        grid.Children.Add(_track);
        grid.Children.Add(phone);
        _track.Children.Add(new Rectangle { Height = 2, Fill = Brush("OutlineVariantBrush"), RadiusX = 1, RadiusY = 1 });
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse { Width = 8, Height = 8, Fill = Brush("PrimaryBrush"), RenderTransform = new TranslateTransform() };
            Canvas.SetTop(_dots[i], 1);
            _track.Children.Add(_dots[i]);
        }
        Content = grid;
        _track.SizeChanged += (_, _) => Restart();
        Unloaded += (_, _) => _board?.Stop();
        Loaded += (_, _) => Restart();
    }

    private void Restart()
    {
        _board?.Stop();
        var width = _track.ActualWidth;
        if (width < 20) return;
        if (_track.Children[0] is Rectangle line)
        {
            line.Width = width;
            Canvas.SetTop(line, 4);
        }
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var period = TimeSpan.FromMilliseconds(1200);
        for (int i = 0; i < _dots.Length; i++)
        {
            var from = ToPhone ? 0 : width - 8;
            var to = ToPhone ? width - 8 : 0;
            var begin = TimeSpan.FromMilliseconds(period.TotalMilliseconds * i / _dots.Length);
            var move = new DoubleAnimation { From = from, To = to, Duration = period, BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(move, _dots[i].RenderTransform);
            Storyboard.SetTargetProperty(move, "X");
            board.Children.Add(move);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = period, BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(period.TotalMilliseconds * 0.2), Value = 1 });
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(period.TotalMilliseconds * 0.8), Value = 1 });
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = period, Value = 0 });
            Storyboard.SetTarget(fade, _dots[i]);
            Storyboard.SetTargetProperty(fade, "Opacity");
            board.Children.Add(fade);
            _dots[i].Opacity = 0;
        }
        _board = board;
        board.Begin();
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
