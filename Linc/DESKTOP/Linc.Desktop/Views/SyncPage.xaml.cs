using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Linc.Desktop.Views;

public sealed partial class SyncPage : Page
{
    public SyncViewModel ViewModel { get; }

    public SyncPage()
    {
        ViewModel = App.Services.GetRequiredService<SyncViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateLaneLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncViewModel.MessagesOn) or nameof(SyncViewModel.CallsOn))
        {
            UpdateLaneLayout();
        }
    }

    // Messages and Calls sit side by side; when only one lane is on, it spans both
    // columns so it fills the width. Visibility is handled by x:Bind in the XAML.
    private void UpdateLaneLayout()
    {
        bool messages = ViewModel.MessagesOn, calls = ViewModel.CallsOn;
        if (messages && calls)
        {
            Grid.SetColumn(MessagesCard, 0);
            Grid.SetColumnSpan(MessagesCard, 1);
            Grid.SetColumn(CallsCard, 1);
            Grid.SetColumnSpan(CallsCard, 1);
        }
        else if (messages)
        {
            Grid.SetColumn(MessagesCard, 0);
            Grid.SetColumnSpan(MessagesCard, 2);
        }
        else if (calls)
        {
            Grid.SetColumn(CallsCard, 0);
            Grid.SetColumnSpan(CallsCard, 2);
        }
    }

    private void OnAddPair(object sender, RoutedEventArgs e) => _ = EditPairAsync(null);

    private void OnEditPair(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderPairVm pair) _ = EditPairAsync(pair);
    }

    private async void OnRemovePair(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is not FolderPairVm pair) return;
            var dialog = new ContentDialog
            {
                Title = $"Stop syncing {pair.Name}?",
                Content = "Both folders and their files stay where they are; Linc just stops keeping them the same.",
                PrimaryButtonText = "Stop syncing",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) ViewModel.RemovePair(pair);
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "SyncPage.OnRemovePair");
        }
    }

    private static readonly (string Direction, string Label)[] Directions =
    [
        (FolderPair.Both, "Both ways"),
        (FolderPair.ToPhone, "PC → phone"),
        (FolderPair.ToPc, "Phone → PC"),
    ];

    /// <summary>The add/edit dialog: a name, the PC folder (Windows' picker), the phone folder
    /// (browsed on the phone, or typed), and which way files go.</summary>
    private async Task EditPairAsync(FolderPairVm? existing)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "Uses the PC folder's name", Text = existing?.Name ?? "" };
        var pcPath = new TextBox { Header = "Folder on this PC", PlaceholderText = "Choose a folder on this PC", Text = existing?.PcPath ?? "" };
        var pickPc = new Button { Content = "Choose…", VerticalAlignment = VerticalAlignment.Bottom };
        var phonePath = new TextBox { Header = "Folder on the phone", PlaceholderText = "/sdcard/Documents", Text = existing?.PhonePath ?? "/sdcard/Download" };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var direction = new RadioButtons { Header = "Which way", MaxColumns = 3 };
        foreach (var (_, label) in Directions) direction.Items.Add(label);
        direction.SelectedIndex = Math.Max(0, Array.FindIndex(Directions, d => d.Direction == (existing?.Direction ?? FolderPair.Both)));
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        void Hint() => hint.Text = direction.SelectedIndex switch
        {
            1 => "The PC's files are copied to the phone. Changes made on the phone stay on the phone.",
            2 => "The phone's files are copied to the PC. Changes made on the PC stay on the PC.",
            _ => "New and changed files copy both ways; when both changed, the newer one wins.",
        };
        Hint();
        direction.SelectionChanged += (_, _) => Hint();

        // Phone folder browser, folded away until Browse.
        var crumb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var up = new Button { Content = new FontIcon { Glyph = "\uE74A", FontSize = 12 }, Padding = new Thickness(8) };
        ToolTipService.SetToolTip(up, "Up");
        var folders = new ListView { Height = 200, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.Children.Add(up);
        Grid.SetColumn(crumb, 1);
        bar.Children.Add(crumb);
        var browser = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        browser.Children.Add(bar);
        browser.Children.Add(folders);
        var files = App.Services.GetRequiredService<IFileService>();
        string current = "/sdcard";
        async Task OpenAsync(string path)
        {
            crumb.Text = path;
            folders.Items.Clear();
            try
            {
                var entries = await files.ListAsync(path, CancellationToken.None);
                current = path;
                phonePath.Text = path;
                foreach (var entry in entries.Where(x => x.IsDirectory && !x.Name.StartsWith('.')).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Items.Add(new ListViewItem
                    {
                        Tag = entry.FullPath,
                        Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children = { new FontIcon { Glyph = "\uE8B7", FontSize = 14 }, new TextBlock { Text = entry.Name } },
                        },
                    });
                }
                if (folders.Items.Count == 0) folders.Items.Add(new ListViewItem { Content = "No folders inside", IsEnabled = false });
            }
            catch (Exception ex)
            {
                folders.Items.Add(new ListViewItem { Content = $"Couldn't open it: {ex.Message}", IsEnabled = false });
            }
        }
        folders.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is ListViewItem { Tag: string path }) _ = OpenAsync(path);
            else if (folders.ContainerFromItem(args.ClickedItem) is ListViewItem { Tag: string p }) _ = OpenAsync(p);
        };
        up.Click += (_, _) =>
        {
            var cut = current.TrimEnd('/').LastIndexOf('/');
            if (cut > 0) _ = OpenAsync(current[..cut]);
        };
        browse.Click += (_, _) =>
        {
            browser.Visibility = Visibility.Visible;
            var start = phonePath.Text.Trim();
            _ = OpenAsync(start.StartsWith('/') ? start.TrimEnd('/') : "/sdcard");
        };
        pickPc.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                pcPath.Text = folder.Path;
                if (string.IsNullOrWhiteSpace(name.Text)) name.PlaceholderText = FolderPair.NameFor(folder.Path);
            }
        };

        static Grid Row(TextBox box, Button button)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(box);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            return row;
        }

        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ErrorBrush"] };
        var body = new StackPanel { Spacing = 12, Width = 460 };
        body.Children.Add(name);
        body.Children.Add(Row(pcPath, pickPc));
        body.Children.Add(Row(phonePath, browse));
        body.Children.Add(browser);
        body.Children.Add(direction);
        body.Children.Add(hint);
        body.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Add a folder to sync" : $"Edit {existing.Name}",
            Content = new ScrollViewer { Content = body, MaxHeight = 560 },
            PrimaryButtonText = existing is null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var pc = pcPath.Text.Trim();
            var problem = string.IsNullOrEmpty(pc) ? "Choose a folder on this PC."
                : !System.IO.Directory.Exists(pc) ? "That PC folder doesn't exist."
                : string.IsNullOrWhiteSpace(phonePath.Text) ? "Choose a folder on the phone."
                : null;
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SavePair(existing, name.Text, pcPath.Text, phonePath.Text, Directions[Math.Max(0, direction.SelectedIndex)].Direction);
        }
    }
}
