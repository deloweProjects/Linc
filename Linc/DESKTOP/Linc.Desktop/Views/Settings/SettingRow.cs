using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Linc.Desktop.Views.Settings;

/// <summary>
/// One setting on a Settings page (the owner, 2026-11-03: "fix PC app settings UI bro, you can do
/// better"): a rounded tile with its name, its explanation underneath, and its switch, button or
/// picker on the right. <see cref="Below"/> holds anything wide (a row of fonts, a QR code, a list).
/// <para>The name always shows; the explanation follows Settings › Appearance › Show explanations
/// (<see cref="Explain"/>) unless <see cref="IsHelp"/> is false. Settings' search box reads
/// <see cref="Header"/>, <see cref="Description"/> and <see cref="Keywords"/>, so a new setting is
/// found by its name with nothing else to register.</para>
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed class SettingRow : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).ShowText()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).ShowText()));

    public static readonly DependencyProperty IsHelpProperty = DependencyProperty.Register(
        nameof(IsHelp), typeof(bool), typeof(SettingRow), new PropertyMetadata(true, (d, _) => ((SettingRow)d).ShowText()));

    public static readonly DependencyProperty IsWarningProperty = DependencyProperty.Register(
        nameof(IsWarning), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, (d, e) =>
            ((SettingRow)d)._description.Foreground = Brush(e.NewValue is true ? "ErrorBrush" : "OnSurfaceVariantBrush")));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(SettingRow), new PropertyMetadata(null, (d, e) => ((SettingRow)d)._action.Content = e.NewValue));

    public static readonly DependencyProperty BelowProperty = DependencyProperty.Register(
        nameof(Below), typeof(object), typeof(SettingRow), new PropertyMetadata(null, (d, e) => ((SettingRow)d).ShowBelow(e.NewValue)));

    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.Register(
        nameof(Keywords), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    /// <summary>The setting's name, always shown.</summary>
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The line that explains it; hidden while explanations are off (unless <see cref="IsHelp"/> is false).</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>False for a description that is a status or a warning, which always shows.</summary>
    public bool IsHelp { get => (bool)GetValue(IsHelpProperty); set => SetValue(IsHelpProperty, value); }

    /// <summary>The description is a warning, in the error colour (the shell box: "mistakes here can affect your phone").</summary>
    public bool IsWarning { get => (bool)GetValue(IsWarningProperty); set => SetValue(IsWarningProperty, value); }

    /// <summary>The control on the right: a switch, a button, a picker.</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    /// <summary>Anything wide, under the name: radio rows, a QR code, a list.</summary>
    public object? Below { get => GetValue(BelowProperty); set => SetValue(BelowProperty, value); }

    /// <summary>Other words the search box should find it by ("dark", "theme" for the colours).</summary>
    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    private readonly Border _root;
    private readonly TextBlock _header;
    private readonly TextBlock _description;
    private readonly ContentPresenter _action;
    private readonly ContentPresenter _below;
    private bool _markedHelp;

    public SettingRow()
    {
        IsTabStop = false;
        _header = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("OnSurfaceBrush"),
        };
        _description = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("OnSurfaceVariantBrush"),
            Visibility = Visibility.Collapsed,
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_header);
        text.Children.Add(_description);

        _action = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _below = new ContentPresenter { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(_action, 1);
        grid.Children.Add(_action);
        Grid.SetRow(_below, 1);
        Grid.SetColumnSpan(_below, 2);
        grid.Children.Add(_below);

        // The gap lives on the tile, not in the parent's Spacing, so a row the search hides
        // leaves no gap behind.
        _root = new Border
        {
            Background = Brush("SurfaceContainerHighBrush"),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
        Content = _root;
    }

    /// <summary>Everything the search box matches against.</summary>
    internal string SearchText => $"{Header} {Description} {Keywords}";

    /// <summary>The search's own show/hide, apart from Visibility (which pages bind, e.g. the bubble's size only while it's on).</summary>
    internal void SetShown(bool shown) => _root.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    private void ShowText()
    {
        _header.Text = Header ?? "";
        _description.Text = Description ?? "";
        if (string.IsNullOrEmpty(Description))
        {
            _description.Visibility = Visibility.Collapsed;
        }
        else if (IsHelp)
        {
            if (!_markedHelp)
            {
                _markedHelp = true;
                Explain.SetIsHelp(_description, true); // shows or hides it with Show explanations
            }
        }
        else
        {
            _description.Visibility = Visibility.Visible;
        }
    }

    private void ShowBelow(object? content)
    {
        _below.Content = content;
        _below.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var brush) ? brush as Brush : null;
}
