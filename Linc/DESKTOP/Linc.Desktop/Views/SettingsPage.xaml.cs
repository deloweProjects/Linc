using System.Collections.ObjectModel;
using Linc.Desktop.ViewModels;
using Linc.Desktop.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Linc.Desktop.Views;

/// <summary>
/// Settings: a home of cards that open the collections' own pages, and a search over all of them
/// (the owner, 2026-11-03: "fix PC app settings UI bro, you can do better"). Every collection's
/// page is made once with this page and only shown or hidden, so its switches stay bound and the
/// search can read every <see cref="SettingRow"/> without opening anything.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    /// <summary>The home's cards, in order.</summary>
    public ObservableCollection<SettingsCategoryVm> Categories { get; } = [];

    /// <summary>
    /// The collections, in card order. A new collection is a UserControl in Views/Settings, a line
    /// here, and its card's one-line summary in <see cref="SettingsViewModel.SummaryFor"/>. A new
    /// setting is a <see cref="SettingRow"/> in its collection's page: the search finds it by itself.
    /// </summary>
    private static readonly (string Key, string Glyph, string Title, Func<UserControl> Create)[] Collections =
    [
        ("connection", "", "Connection", () => new ConnectionSettings()),
        ("finding", "", "Finding your phone", () => new FindingSettings()),
        ("devices", "", "Phones", () => new DevicesSettings()),
        ("appearance", "", "Appearance", () => new AppearanceSettings()),
        ("mirror", "", "Mirror and sound", () => new MirrorSettings()),
        ("notifications", "", "Notifications", () => new NotificationSettings()),
        ("clipboard", "", "Clipboard", () => new ClipboardSettings()),
        ("startup", "", "Startup and tray", () => new StartupSettings()),
        ("advanced", "", "Advanced", () => new AdvancedSettings()),
        ("about", "", "About", () => new AboutSettings()),
    ];

    /// <summary>A collection to open when Settings next appears ("about"); <see cref="MainWindow.ShowPage"/> sets it.</summary>
    public static string? OpenOnArrival { get; set; }

    private sealed record Group(SettingsGroup? Card, List<SettingRow> Rows);

    /// <param name="Decor">What a page shows besides its cards (About's banner), hidden while searching.</param>
    private sealed record Section(SettingsCategoryVm Category, FrameworkElement Wrapper, FrameworkElement SearchHeader, List<Group> Groups, List<UIElement> Decor);

    private readonly List<Section> _sections = [];

    /// <summary>The open collection's key, or null on the home.</summary>
    private string? _open;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        foreach (var (key, glyph, title, create) in Collections)
        {
            var category = new SettingsCategoryVm(key, glyph, title, Open);
            if (key != "about")
            {
                Categories.Add(category); // About has the banner at the top instead of a card
            }
            var page = create();
            var header = SearchHeader(category);
            var wrapper = new StackPanel { Visibility = Visibility.Collapsed };
            wrapper.Children.Add(header);
            wrapper.Children.Add(page);
            Sections.Children.Add(wrapper);
            var decor = page.Content is Panel root
                ? root.Children.Where(c => c is not SettingsGroup and not SettingRow).ToList()
                : [];
            _sections.Add(new Section(category, wrapper, header, Groups(page), decor));
        }

        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            RefreshSummaries();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
        // The mouse's back button goes back too, as it does in a browser.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && GoBack())
            {
                e.Handled = true;
            }
        }), true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (OpenOnArrival is { } key)
        {
            OpenOnArrival = null;
            Open(key);
        }
    }

    // ---- home and the collections' pages ------------------------------------------------

    /// <summary>Opens one collection's page (a card's key); an unknown key shows the home.</summary>
    public void Open(string key)
    {
        if (_sections.All(s => s.Category.Key != key))
        {
            ShowHome();
            return;
        }
        _open = key;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = ""; // its TextChanged comes back through Show()
        }
        Show();
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void ShowHome()
    {
        _open = null;
        Show();
    }

    /// <summary>Lays the page out for what's on: a search, an open collection, or the home.</summary>
    private void Show()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            ShowSearch(query);
            return;
        }
        NoMatches.Visibility = Visibility.Collapsed;
        var open = _sections.FirstOrDefault(s => s.Category.Key == _open);
        HomeView.Visibility = open is null ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = open is null ? Visibility.Collapsed : Visibility.Visible;
        TitleIcon.Visibility = open is null ? Visibility.Collapsed : Visibility.Visible;
        TitleIcon.Glyph = open?.Category.Glyph ?? "";
        TitleText.Text = open?.Category.Title ?? "Settings";
        foreach (var section in _sections)
        {
            section.Wrapper.Visibility = section == open ? Visibility.Visible : Visibility.Collapsed;
            section.SearchHeader.Visibility = Visibility.Collapsed;
            section.Decor.ForEach(d => d.Visibility = Visibility.Visible);
            foreach (var group in section.Groups)
            {
                group.Card?.SetShown(true);
                group.Rows.ForEach(r => r.SetShown(true));
            }
        }
        if (open is null)
        {
            RefreshSummaries();
        }
    }

    /// <summary>Back from a search or a collection; false when already on the home.</summary>
    private bool GoBack()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            return true;
        }
        if (_open is not null)
        {
            ShowHome();
            return true;
        }
        return false;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnAboutBannerClick(object sender, RoutedEventArgs e) => Open("about");

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => args.Handled = GoBack();

    private void OnFindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void RefreshSummaries()
    {
        foreach (var category in Categories)
        {
            category.Summary = ViewModel.SummaryFor(category.Key);
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_open is null)
        {
            DispatcherQueue.TryEnqueue(RefreshSummaries);
        }
    }

    // ---- search -------------------------------------------------------------------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        Show();
        Scroller.ChangeView(null, 0, null, disableAnimation: true); // results start at the top
    }

    /// <summary>
    /// Every collection's page at once, each under its name, showing only what matches: a whole
    /// collection when its name matches, a whole card when its title does, and the matching rows
    /// otherwise. Every word typed has to be found.
    /// </summary>
    private void ShowSearch(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string text) => words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));

        HomeView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        TitleIcon.Visibility = Visibility.Collapsed;
        TitleText.Text = "Settings";
        foreach (var section in _sections)
        {
            bool sectionHit = Matches(section.Category.Title);
            bool anyGroup = false;
            foreach (var group in section.Groups)
            {
                bool groupHit = sectionHit || (group.Card is { } card && Matches(card.SearchText));
                bool anyRow = false;
                foreach (var row in group.Rows)
                {
                    bool hit = groupHit || Matches(row.SearchText);
                    row.SetShown(hit);
                    anyRow |= hit;
                }
                bool show = groupHit || anyRow;
                group.Card?.SetShown(show);
                anyGroup |= show;
            }
            section.Wrapper.Visibility = anyGroup ? Visibility.Visible : Visibility.Collapsed;
            section.SearchHeader.Visibility = Visibility.Visible;
            section.Decor.ForEach(d => d.Visibility = Visibility.Collapsed);
        }
        bool none = _sections.All(s => s.Wrapper.Visibility == Visibility.Collapsed);
        NoMatches.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesText.Text = $"No setting matches “{query}”.\nTry another word, like Wi-Fi, bubble or font.";
    }

    /// <summary>The collection's name above its results while searching; a click opens it.</summary>
    private FrameworkElement SearchHeader(SettingsCategoryVm category)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { Glyph = category.Glyph, FontSize = 16 });
        content.Children.Add(new TextBlock { Text = category.Title, Style = (Style)Application.Current.Resources["TitleText"], VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["QuietButton"],
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 12, 6),
            Margin = new Thickness(0, 4, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(button, $"Open {category.Title}");
        button.Click += (_, _) => Open(category.Key);
        return button;
    }

    /// <summary>
    /// A page's cards and their rows, read once from what its XAML made (no visual tree needed, so
    /// it works while the page is hidden). Rows outside any card form a group of their own.
    /// </summary>
    private static List<Group> Groups(DependencyObject page)
    {
        var groups = new List<Group>();
        var loose = new List<SettingRow>();
        void Walk(DependencyObject node, List<SettingRow> into)
        {
            switch (node)
            {
                case SettingsGroup card:
                    var rows = new List<SettingRow>();
                    foreach (var child in card.Items) Walk(child, rows);
                    groups.Add(new Group(card, rows));
                    return;
                case SettingRow row:
                    into.Add(row);
                    return;
                case Panel panel:
                    foreach (var child in panel.Children) Walk(child, into);
                    return;
                case Border { Child: { } child }:
                    Walk(child, into);
                    return;
                case UserControl { Content: { } child }:
                    Walk(child, into);
                    return;
            }
        }
        Walk(page, loose);
        if (loose.Count > 0)
        {
            groups.Add(new Group(null, loose));
        }
        return groups;
    }
}
