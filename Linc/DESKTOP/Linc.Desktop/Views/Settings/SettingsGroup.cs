using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Linc.Desktop.Views.Settings;

/// <summary>
/// A card of related settings on a Settings page: a title, an optional explanation, and its
/// <see cref="SettingRow"/>s. The search shows a group whole when its title matches, and only its
/// matching rows otherwise.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public sealed class SettingsGroup : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsGroup), new PropertyMetadata("", (d, _) => ((SettingsGroup)d).ShowText()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsGroup), new PropertyMetadata("", (d, _) => ((SettingsGroup)d).ShowText()));

    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.Register(
        nameof(Keywords), typeof(string), typeof(SettingsGroup), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>A help line under the title; follows Show explanations.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    /// <summary>The rows (and anything else) in the card.</summary>
    public UIElementCollection Items => _items.Children;

    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly TextBlock _description;
    private readonly StackPanel _items = new();
    private bool _markedHelp;

    public SettingsGroup()
    {
        IsTabStop = false;
        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 40,
            Foreground = SettingRow.Brush("OnSurfaceVariantBrush"),
            Margin = new Thickness(4, 0, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        _description = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SettingRow.Brush("OnSurfaceVariantBrush"),
            Margin = new Thickness(4, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_title);
        panel.Children.Add(_description);
        panel.Children.Add(_items);
        _root = new Border
        {
            Background = SettingRow.Brush("SurfaceContainerBrush"),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(14, 16, 14, 10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = panel,
        };
        Content = _root;
    }

    internal string SearchText => $"{Title} {Keywords}";

    internal void SetShown(bool shown) => _root.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    private void ShowText()
    {
        _title.Text = (Title ?? "").ToUpperInvariant();
        _title.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = Description ?? "";
        if (string.IsNullOrEmpty(Description))
        {
            _description.Visibility = Visibility.Collapsed;
        }
        else if (!_markedHelp)
        {
            _markedHelp = true;
            Explain.SetIsHelp(_description, true);
        }
    }
}
