using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.UI;

namespace Linc.Desktop.Views;

/// <summary>Where the bubble's pages read from: Linc's own services, or pretend ones in the preview.</summary>
public sealed record BubbleSources(INotificationSyncService Notifications, IClipboardSyncService Clipboard, IShareService Share, IFileService Files)
{
    public static BubbleSources Live() => new(
        App.Services.GetRequiredService<INotificationSyncService>(),
        App.Services.GetRequiredService<IClipboardSyncService>(),
        App.Services.GetRequiredService<IShareService>(),
        App.Services.GetRequiredService<IFileService>());
}

/// <summary>
/// The bubble's own small pages (owner, 2026-11-03: "I get a WhatsApp notification on the bubble,
/// I click, I just see 'WhatsApp has 30 notifications', nah, I want to see the message and even
/// type a reply from the bubble. Make it so I barely need to open the main Linc app."):
///
/// - <b>Notifications</b>: the phone's notifications by app, newest first; one opens to the whole
///   message with its buttons, Dismiss and, where the app allows it, a reply box.
/// - <b>Clipboard</b>, <b>Share</b> and <b>Files</b>: the few things those pages are mostly opened
///   for, each with "Open in Linc" for the rest.
///
/// They live in the bubble's own window (the Mini shape), so they open and close with the same
/// morph and fold away on a click elsewhere. The window never takes focus, except while a text box
/// is being typed in: then it takes it, and gives it back to the app that had it when done.
/// </summary>
public sealed partial class LincBubbleWindow
{
    private enum MiniPage { Notifications, Clipboard, Share, Files }

    private static readonly string[] PhoneFolders = ["Download", "DCIM/Camera", "Pictures/Screenshots", "Documents"];
    private const string PhoneStorage = "/storage/emulated/0";

    private readonly BubbleSources _src;
    private readonly StackPanel _miniFace = new() { Spacing = 10, Opacity = 0, Padding = new Thickness(14, 12, 14, 14), Visibility = Visibility.Collapsed, AllowDrop = true };
    private readonly ScaleTransform _miniZoom = new();
    private Button? _noteRow;
    private MiniPage? _mini;
    private string _miniStatus = "";
    // Notifications: the one opened, whether its reply box is out, what was typed so far.
    private string? _openKey;
    private bool _replyOpen;
    private string _replyDraft = "";
    // The page changed while a box was being typed in: rebuilt once typing ends, not under the caret.
    private bool _miniDirty;
    // Typing: the window holds the keyboard; who had it before.
    private bool _typing;
    private IntPtr _focusBefore;
    // A file picker is up: clicks in it are not "elsewhere".
    private bool _holdOpen;
    private readonly Dictionary<string, ImageSource?> _appIcons = [];
    // Files: the folder shown, its newest files, and downloads (progress, then the saved path).
    private string _filesFolder = PhoneFolders[0];
    private IReadOnlyList<RemoteEntry>? _filesEntries;
    private string? _filesListed; // the folder _filesEntries is from
    private string? _filesError;
    private CancellationTokenSource? _filesCts;
    private readonly Dictionary<string, double> _downloading = [];
    private readonly Dictionary<string, string> _downloaded = [];
    private readonly Dictionary<string, ProgressBar> _downloadBars = [];

    // ---- wiring (from the constructor) ----

    private void WireMini()
    {
        _miniFace.RenderTransform = _miniZoom;
        _src.Notifications.Changed += OnNotificationsChanged;
        _src.Clipboard.HistoryChanged += OnMiniSourceChanged;
        _src.Share.TransferChanged += OnTransferChanged;
        _miniFace.DragOver += (_, e) =>
        {
            if (_mini == MiniPage.Share && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Send to your phone";
            }
        };
        _miniFace.Drop += async (_, e) =>
        {
            if (_mini != MiniPage.Share || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            SendFiles([.. items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))]);
        };
        // Clicked away while typing: the keyboard is the other app's again, and the bubble folds
        // as it would after any click elsewhere.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || !_typing) return;
            EndTyping(giveBack: false);
            if (!_pointerOver && _shape != Shape.Bubble)
            {
                _leaveTimer.Interval = LeaveGrace;
                _leaveTimer.Start();
            }
        };
    }

    private void UnwireMini()
    {
        _src.Notifications.Changed -= OnNotificationsChanged;
        _src.Clipboard.HistoryChanged -= OnMiniSourceChanged;
        _src.Share.TransferChanged -= OnTransferChanged;
        _filesCts?.Cancel();
    }

    private void OnNotificationsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshNoteRow();
        if (_mini == MiniPage.Notifications) RebuildMini();
    });

    private void OnMiniSourceChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_mini == MiniPage.Clipboard) RebuildMini();
    });

    private void OnTransferChanged(PhoneTransfer t) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_mini == MiniPage.Share) RebuildMini();
    });

    /// <summary>The panel's notifications row: how many, the newest, and a tap lists them all.</summary>
    private Button BuildNoteRow()
    {
        _noteRow = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 10, 6),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            Background = Theme("SurfaceContainerHighestBrush"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_noteRow, "Notifications");
        _noteRow.Click += (_, _) => OpenMini(MiniPage.Notifications);
        RefreshNoteRow();
        return _noteRow;
    }

    private void RefreshNoteRow()
    {
        if (_noteRow is null) return;
        var items = _src.Notifications.Items;
        var newest = items.OrderByDescending(n => n.PostedAt).FirstOrDefault();
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Accent(LiveActivityKind.Notification)),
            Child = new FontIcon { Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Colors.White) },
        });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = "Notifications", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush") });
        words.Children.Add(new TextBlock
        {
            Text = newest is null ? "Nothing from your phone" : $"{newest.App}: {TitleOf(newest)}",
            FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (items.Count > 0)
        {
            trail.Children.Add(new Border
            {
                MinWidth = 22, Height = 20, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(10),
                Background = Theme("PrimaryBrush"),
                Child = new TextBlock { Text = items.Count.ToString(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }
        trail.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush") });
        Grid.SetColumn(trail, 2);
        row.Children.Add(trail);
        _noteRow.Content = row;
    }

    // ---- opening and closing ----

    private void OpenMini(MiniPage page, string? openKey = null)
    {
        if (_mini != page) _miniStatus = "";
        _mini = page;
        _openKey = openKey;
        _replyOpen = false;
        _replyDraft = "";
        if (page == MiniPage.Files) _ = LoadFilesAsync(_filesFolder); // fresh each time; the last list shows meanwhile
        BuildMini();
        if (_shape == Shape.Mini) Resize(animate: true);
        else SetShape(Shape.Mini);
    }

    /// <summary>A notification on the island, clicked: its whole message, not a card that says only who.</summary>
    private void OpenNotification(LiveActivity a)
    {
        var items = _src.Notifications.Items.OrderByDescending(n => n.PostedAt).ToList();
        var match = items.FirstOrDefault(n => TitleOf(n) == a.Title && n.App == a.Trailing)
            ?? items.FirstOrDefault(n => n.App == a.Trailing);
        OpenMini(MiniPage.Notifications, match?.Key);
    }

    /// <summary>Linc on that page, in front at once (minimised or hidden in the tray, too).</summary>
    private void OpenInLinc(string? tag)
    {
        EndTyping(giveBack: false);
        SetShape(Shape.Bubble);
        if (App.MainWindowInstance is not MainWindow main) return;
        try
        {
            main.ShowWindowCommand.Execute(null); // shows, restores, activates
            main.ShowPage(tag);
            // The click on the bubble was this process's input, so Windows lets it bring Linc forward.
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(main));
        }
        catch (Exception)
        {
            // Linc still opens on that page; it may only flash in the taskbar.
        }
    }

    /// <summary>The shape left Mini: nothing stays typed into, nothing keeps loading.</summary>
    private void LeftMini()
    {
        EndTyping(giveBack: true);
        _mini = null;
        _replyOpen = false;
        _miniDirty = false;
    }

    // ---- typing ----

    /// <summary>
    /// The reply box (or the link box) is being typed in: the window drops WS_EX_NOACTIVATE and takes
    /// the keyboard, remembering who had it. The window region is untouched, so outside the shape
    /// clicks still go to the app below.
    /// </summary>
    private void BeginTyping(Control box)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!_typing)
            {
                _typing = true;
                _leaveTimer.Stop();
                var before = GetForegroundWindow();
                _focusBefore = before == hwnd ? IntPtr.Zero : before;
                SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) & ~(nint)0x08000000); // no more WS_EX_NOACTIVATE
                SetForegroundWindow(hwnd);
                Activate();
            }
        }
        catch (Exception)
        {
            // Couldn't take the keyboard: the box shows, typing goes where it went.
        }
        box.Focus(FocusState.Programmatic);
    }

    /// <summary>Done typing: never activated again, and the keyboard back to the app that had it.</summary>
    private void EndTyping(bool giveBack)
    {
        if (!_typing) return;
        _typing = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x08000000); // WS_EX_NOACTIVATE
            if (giveBack && _focusBefore != IntPtr.Zero && IsWindow(_focusBefore) && GetForegroundWindow() == hwnd)
            {
                SetForegroundWindow(_focusBefore);
            }
        }
        catch (Exception)
        {
            // The bubble may keep the keyboard until the next click; nothing breaks.
        }
        _focusBefore = IntPtr.Zero;
        StayOnTop();
        if (_miniDirty && _mini is not null)
        {
            _miniDirty = false;
            DispatcherQueue.TryEnqueue(RebuildMini);
        }
    }

    /// <summary>A text box that takes the keyboard when clicked (or the moment it shows, with <paramref name="now"/>).</summary>
    private TextBox TypingBox(string placeholder, string text, bool now)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            Text = text,
            CornerRadius = new CornerRadius(16),
            MinHeight = 34,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96,
        };
        box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginTyping(box)), handledEventsToo: true);
        box.GotFocus += (_, _) => { if (!_typing) BeginTyping(box); };
        if (now) box.Loaded += (_, _) => BeginTyping(box);
        return box;
    }

    // ---- building the page ----

    private void RebuildMini()
    {
        if (_mini is null || _shape != Shape.Mini)
        {
            return;
        }
        if (_typing)
        {
            _miniDirty = true;
            return;
        }
        BuildMini();
        Resize(animate: false);
    }

    private void BuildMini()
    {
        if (_mini is not { } page) return;
        var offset = (_miniFace.Children.OfType<ScrollViewer>().FirstOrDefault())?.VerticalOffset ?? 0;
        _miniFace.Children.Clear();
        _miniFace.Width = CardWidth;
        switch (page)
        {
            case MiniPage.Notifications:
                _miniFace.Children.Add(MiniHeader("Notifications", "Home", "Open notifications in Linc"));
                BuildNotifications();
                break;
            case MiniPage.Clipboard:
                _miniFace.Children.Add(MiniHeader("Clipboard", "Clipboard", "Open the clipboard history in Linc"));
                BuildClipboard();
                break;
            case MiniPage.Share:
                _miniFace.Children.Add(MiniHeader("Share", "Share", "Open Share in Linc"));
                BuildShare();
                break;
            case MiniPage.Files:
                _miniFace.Children.Add(MiniHeader("Phone files", "Files", "Open this folder in Linc"));
                BuildFiles();
                break;
        }
        if (_miniStatus.Length > 0)
        {
            _miniFace.Children.Add(new TextBlock { Text = _miniStatus, FontSize = 12, Foreground = Theme("OnSurfaceVariantBrush"), TextWrapping = TextWrapping.Wrap });
        }
        if (_miniFace.Children.OfType<ScrollViewer>().FirstOrDefault() is { } scroll && offset > 0)
        {
            scroll.Loaded += (_, _) => scroll.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    /// <summary>Back to the panel, the page's name, and "Open in Linc" for everything else.</summary>
    private Grid MiniHeader(string title, string linkTag, string linkTip)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = RoundButton("", "Back to the shortcuts");
        back.Click += (_, _) =>
        {
            LeftMini();
            SetShape(Shape.Panel);
        };
        header.Children.Add(back);
        var name = new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var open = new Button
        {
            Height = 32, Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme("SecondaryContainerBrush"),
            Foreground = Theme("OnSecondaryContainerBrush"),
            Content = Labelled("", "Open in Linc"),
        };
        ToolTipService.SetToolTip(open, linkTip);
        open.Click += async (_, _) =>
        {
            if (_mini == MiniPage.Files && !_preview && App.Services.GetService<FilesViewModel>() is { } files)
            {
                OpenInLinc(linkTag);
                try { await files.NavigateAsync(FolderPath(_filesFolder)); } catch (Exception) { /* the page says why */ }
                return;
            }
            OpenInLinc(linkTag);
        };
        Grid.SetColumn(open, 2);
        header.Children.Add(open);
        return header;
    }

    private static StackPanel Labelled(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return content;
    }

    private static Button RoundButton(string glyph, string name)
    {
        var button = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme("SecondaryContainerBrush"),
            Foreground = Theme("OnSecondaryContainerBrush"),
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static Button PillButton(string label, bool primary, string? glyph = null)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(0),
            Background = Theme(primary ? "PrimaryBrush" : "SecondaryContainerBrush"),
            Foreground = Theme(primary ? "OnPrimaryBrush" : "OnSecondaryContainerBrush"),
            Padding = new Thickness(6, 0, 6, 0),
            Content = glyph is null ? new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } : Labelled(glyph, label),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private static Grid ButtonRow(params Button[] buttons)
    {
        var row = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(buttons[i], i);
            row.Children.Add(buttons[i]);
        }
        return row;
    }

    private static ScrollViewer Scroller(UIElement content) => new()
    {
        Content = content,
        MaxHeight = 380,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
        Padding = new Thickness(0, 0, 4, 0),
    };

    private static TextBlock Quiet(string text) =>
        new() { Text = text, FontSize = 12, Foreground = Theme("OnSurfaceVariantBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) };

    private void Say(string text)
    {
        _miniStatus = text;
        RebuildMini();
    }

    // ---- Notifications ----

    private static string TitleOf(NotificationItem n) => n.Title.Length > 0 ? n.Title : n.App;

    /// <summary>
    /// By app, the app with the newest first, and newest first inside it. An app's summary ("WhatsApp:
    /// 30 messages from 5 chats", titled with the app's own name) is left out when its messages are there.
    /// </summary>
    private void BuildNotifications()
    {
        var groups = _src.Notifications.Items
            .GroupBy(n => n.App)
            .Select(g =>
            {
                var all = g.OrderByDescending(n => n.PostedAt).ToList();
                var real = all.Where(n => !string.Equals(n.Title.Trim(), n.App.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                return real.Count > 0 && real.Count < all.Count ? real : all;
            })
            .OrderByDescending(g => g[0].PostedAt)
            .ToList();
        if (groups.Count == 0)
        {
            _miniFace.Children.Add(Quiet("No notifications from your phone right now."));
            return;
        }
        var list = new StackPanel { Spacing = 6 };
        FrameworkElement? opened = null;
        foreach (var group in groups)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, list.Children.Count > 0 ? 6 : 0, 0, 0) };
            head.Children.Add(AppIcon(group[0], 20));
            head.Children.Add(new TextBlock { Text = group[0].App, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceVariantBrush"), VerticalAlignment = VerticalAlignment.Center });
            if (group.Count > 1)
            {
                head.Children.Add(new TextBlock { Text = group.Count.ToString(), FontSize = 12, Foreground = Theme("OnSurfaceVariantBrush"), VerticalAlignment = VerticalAlignment.Center });
            }
            list.Children.Add(head);
            foreach (var n in group)
            {
                FrameworkElement row = n.Key == _openKey ? NotificationOpen(n) : NotificationRow(n);
                if (n.Key == _openKey) opened = head; // its app's name stays in sight above it
                list.Children.Add(row);
            }
        }
        _miniFace.Children.Add(Scroller(list));
        if (opened is not null)
        {
            opened.Loaded += (_, _) => opened.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        }
    }

    private Button NotificationRow(NotificationItem n)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = TitleOf(n), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (n.Text.Length > 0)
        {
            words.Children.Add(new TextBlock { Text = n.Text, FontSize = 12, Foreground = Theme("OnSurfaceVariantBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        }
        row.Children.Add(words);
        var time = new TextBlock { Text = When(n.PostedAt), FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(time, 1);
        row.Children.Add(time);
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0),
            Background = Theme("SurfaceContainerHighestBrush"),
            Content = row,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{n.App}: {TitleOf(n)}");
        button.Click += (_, _) =>
        {
            _openKey = n.Key;
            _replyOpen = false;
            _replyDraft = "";
            _miniStatus = "";
            BuildMini();
            Resize(animate: true);
        };
        return button;
    }

    /// <summary>One notification, opened: who, the whole message, its buttons, and the reply box.</summary>
    private Border NotificationOpen(NotificationItem n)
    {
        var body = new StackPanel { Spacing = 8 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var who = new StackPanel();
        who.Children.Add(new TextBlock { Text = TitleOf(n), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), TextWrapping = TextWrapping.Wrap });
        var caption = n.ConversationTitle is { Length: > 0 } conversation && conversation != TitleOf(n)
            ? $"{conversation} · {n.PostedAt.LocalDateTime:t}"
            : $"{n.PostedAt.LocalDateTime:t}";
        who.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush") });
        top.Children.Add(who);
        var fold = RoundButton("", "Fold it up");
        fold.Width = fold.Height = 28;
        fold.Click += (_, _) =>
        {
            EndTyping(giveBack: true);
            _openKey = null;
            _replyOpen = false;
            BuildMini();
            Resize(animate: true);
        };
        Grid.SetColumn(fold, 1);
        top.Children.Add(fold);
        body.Children.Add(top);
        if (n.Text.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = n.Text, FontSize = 13, Foreground = Theme("OnSurfaceBrush"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        var reply = n.Actions?.FirstOrDefault(a => a.AllowsReply);
        var buttons = new List<Button>();
        foreach (var action in (n.Actions ?? []).Where(a => !a.AllowsReply).Take(2))
        {
            var b = PillButton(action.Title, primary: false);
            b.Click += async (_, _) =>
            {
                var ok = await _src.Notifications.FireActionAsync(n.Key, action.Index);
                Say(ok ? $"{action.Title}: done on your phone." : $"Couldn't {action.Title.ToLowerInvariant()} — is the phone connected?");
            };
            buttons.Add(b);
        }
        if (reply is not null && !_replyOpen)
        {
            var b = PillButton(reply.Title.Length > 0 ? reply.Title : "Reply", primary: true, "");
            b.Click += (_, _) =>
            {
                _replyOpen = true;
                _miniStatus = "";
                BuildMini();
                Resize(animate: true);
            };
            body.Children.Add(b);
        }
        var dismiss = PillButton("Dismiss", primary: false);
        dismiss.Click += async (_, _) =>
        {
            EndTyping(giveBack: true);
            _openKey = null;
            _replyOpen = false;
            await _src.Notifications.DismissAsync(n.Key); // Changed rebuilds without it
        };
        buttons.Add(dismiss);

        if (reply is not null && _replyOpen)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = TypingBox($"Reply to {TitleOf(n)}", _replyDraft, now: true);
            box.TextChanged += (_, _) => _replyDraft = box.Text;
            var failed = new TextBlock { Text = "Couldn't send the reply — is the phone connected?", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A)), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var send = RoundButton("", "Send the reply");
            send.Width = send.Height = 34;
            send.Background = Theme("PrimaryBrush");
            send.Foreground = Theme("OnPrimaryBrush");
            async Task SendAsync()
            {
                var text = box.Text.Trim();
                if (text.Length == 0) return;
                send.IsEnabled = false;
                var ok = await _src.Notifications.ReplyAsync(n.Key, reply.Index, text);
                send.IsEnabled = true;
                if (!ok)
                {
                    failed.Visibility = Visibility.Visible; // the draft stays, to try again
                    Resize(animate: false);
                    return;
                }
                _replyDraft = "";
                _replyOpen = false;
                EndTyping(giveBack: true);
                Say($"Replied to {TitleOf(n)}.");
            }
            send.Click += async (_, _) => await SendAsync();
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    await SendAsync();
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    _replyOpen = false;
                    EndTyping(giveBack: true);
                    BuildMini();
                    Resize(animate: true);
                }
            };
            line.Children.Add(box);
            Grid.SetColumn(send, 1);
            line.Children.Add(send);
            body.Children.Add(line);
            body.Children.Add(failed);
        }
        body.Children.Add(ButtonRow([.. buttons]));
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 12),
            CornerRadius = new CornerRadius(16),
            Background = Theme("SurfaceContainerHighestBrush"),
            BorderBrush = Theme("PrimaryBrush"),
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }

    private static string When(DateTimeOffset at)
    {
        var ago = DateTimeOffset.Now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min";
        return at.LocalDateTime.Date == DateTime.Today ? at.LocalDateTime.ToString("t") : at.LocalDateTime.ToString("d MMM");
    }

    /// <summary>The app's icon from the phone (fetched once per app), its initial until then.</summary>
    private Border AppIcon(NotificationItem n, double size)
    {
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(Accent(LiveActivityKind.Notification)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var package = n.AppPackage;
        if (package is not null && _appIcons.TryGetValue(package, out var icon) && icon is not null)
        {
            tile.Background = new ImageBrush { ImageSource = icon, Stretch = Stretch.UniformToFill };
            return tile;
        }
        tile.Child = new TextBlock
        {
            Text = n.App.Length > 0 ? n.App[..1].ToUpperInvariant() : "?",
            FontSize = size * 0.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (package is not null && !_preview && !_appIcons.ContainsKey(package))
        {
            _appIcons[package] = null; // asked once
            _ = LoadIconAsync(package);
        }
        return tile;
    }

    private async Task LoadIconAsync(string package)
    {
        try
        {
            if (App.Services.GetService<IConnectionManager>() is not { } connection) return;
            var bytes = await connection.FetchAppIconAsync(package, CancellationToken.None);
            if (bytes is not { Length: > 0 }) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    var image = new BitmapImage { DecodePixelWidth = 48 };
                    await image.SetSourceAsync(stream);
                    _appIcons[package] = image;
                    if (_mini == MiniPage.Notifications) RebuildMini();
                }
                catch (Exception)
                {
                    // Not a picture Windows can read: the initial stays.
                }
            });
        }
        catch (Exception)
        {
            // No icon: the initial stays.
        }
    }

    // ---- Clipboard ----

    private void BuildClipboard()
    {
        var clips = _src.Clipboard.History.OrderByDescending(c => c.At).Take(3).ToList();
        var list = new StackPanel { Spacing = 6 };
        if (clips.Count == 0)
        {
            list.Children.Add(Quiet("Nothing copied yet. Copy something on either device and it shows here."));
        }
        foreach (var clip in clips)
        {
            var words = new StackPanel { Spacing = 2 };
            words.Children.Add(new TextBlock { Text = clip.Text.Trim(), FontSize = 13, Foreground = Theme("OnSurfaceBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock
            {
                Text = $"{(clip.FromPhone ? "From your phone" : clip.Sent ? "Sent to your phone" : "From this PC")} · {When(clip.At)} · tap to copy here",
                FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"),
            });
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(0),
                Background = Theme("SurfaceContainerHighestBrush"),
                Content = words,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Copy here: " + clip.Text);
            var entry = clip;
            button.Click += (_, _) =>
            {
                try
                {
                    _src.Clipboard.Recopy(entry);
                    Say("Copied to this PC.");
                }
                catch (Exception)
                {
                    Say("Windows didn't let Linc use the clipboard just now; try again.");
                }
            };
            list.Children.Add(button);
        }
        _miniFace.Children.Add(list);
        var sync = PillButton("Sync now", primary: false, "");
        sync.Click += async (_, _) =>
        {
            sync.IsEnabled = false;
            var ok = await _src.Clipboard.SyncNowAsync();
            Say(ok ? "Both clipboards are in step." : "Couldn't sync — is the phone connected?");
        };
        var send = PillButton("Send to phone", primary: true, "");
        send.Click += (_, _) =>
        {
            (App.MainWindowInstance as MainWindow)?.SendClipboardToPhone();
            Say("Sending this PC's clipboard to your phone…");
        };
        _miniFace.Children.Add(ButtonRow(sync, send));
    }

    // ---- Share ----

    private void BuildShare()
    {
        var pick = PillButton("Send a file…", primary: true, "");
        pick.Click += async (_, _) => await PickFilesAsync();
        _miniFace.Children.Add(pick);

        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = TypingBox("A link or text for your phone", "", now: false);
        var send = RoundButton("", "Send it to the phone");
        send.Width = send.Height = 34;
        async Task SendAsync()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) return;
            EndTyping(giveBack: true);
            await SendTextAsync(text);
        }
        send.Click += async (_, _) => await SendAsync();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await SendAsync();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                EndTyping(giveBack: true);
            }
        };
        line.Children.Add(box);
        Grid.SetColumn(send, 1);
        line.Children.Add(send);
        _miniFace.Children.Add(line);

        var transfers = _src.Share.Transfers.Take(4).ToList();
        if (transfers.Count == 0)
        {
            _miniFace.Children.Add(Quiet("Nothing moving right now. Drop files here to send them to your phone."));
            return;
        }
        var list = new StackPanel { Spacing = 6 };
        foreach (var t in transfers)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel { Spacing = 4 };
            words.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme("OnSurfaceBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var state = t.State switch
            {
                PhoneTransferState.Sending => $"{(t.ToPhone ? "To your phone" : "From your phone")} · {(int)Math.Clamp(t.Progress, 0, 100)}%",
                PhoneTransferState.Done => t.ToPhone ? "On your phone" : "Saved on this PC",
                PhoneTransferState.Cancelled => "Cancelled",
                _ => t.Message ?? "Didn't go through",
            };
            words.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (t.State == PhoneTransferState.Sending)
            {
                words.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = t.Progress, Foreground = Theme("PrimaryBrush"), Background = Theme("OutlineVariantBrush") });
            }
            row.Children.Add(words);
            if (t.State == PhoneTransferState.Sending)
            {
                var cancel = RoundButton("", "Cancel " + t.Name);
                var id = t.Id;
                cancel.Click += (_, _) => _src.Share.Cancel(id);
                Grid.SetColumn(cancel, 1);
                row.Children.Add(cancel);
            }
            list.Children.Add(new Border { Padding = new Thickness(12, 8, 8, 8), CornerRadius = new CornerRadius(14), Background = Theme("SurfaceContainerHighestBrush"), Child = row });
        }
        _miniFace.Children.Add(Scroller(list));
    }

    private async Task PickFilesAsync()
    {
        _holdOpen = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is { Count: > 0 }) SendFiles([.. files.Select(f => f.Path)]);
        }
        catch (Exception)
        {
            Say("The file picker didn't open; drop the files here instead.");
        }
        finally
        {
            _holdOpen = false;
            StayOnTop();
        }
    }

    /// <summary>Over Linc when connected; otherwise Linc's Share page offers Quick Share, as the tray's Send does.</summary>
    private void SendFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (_preview)
        {
            Say($"Sending {paths.Count} file(s)… (preview)");
            return;
        }
        if (App.Services.GetService<ShareViewModel>() is not { } share) return;
        if (share.SendFiles(paths)) Say(share.Message);
        else OpenInLinc("Share"); // queued for Quick Share: pick a device there
    }

    /// <summary>A link opens on the phone, other text lands on its clipboard; queued if the phone is away (the Share page's own send).</summary>
    private async Task SendTextAsync(string text)
    {
        if (_preview)
        {
            Say("Sent (preview).");
            return;
        }
        if (App.Services.GetService<ShareViewModel>() is not { } share) return;
        share.TextToPhone = text;
        await share.SendTextCommand.ExecuteAsync(null);
        Say(share.Message.Length > 0 ? share.Message : "Sent to your phone.");
    }

    // ---- Files ----

    private static string FolderPath(string folder) => $"{PhoneStorage}/{folder}";

    private static string FolderName(string folder) => folder switch
    {
        "DCIM/Camera" => "Camera",
        "Pictures/Screenshots" => "Screenshots",
        _ => folder,
    };

    private void BuildFiles()
    {
        var chips = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < PhoneFolders.Length; i++)
        {
            var folder = PhoneFolders[i];
            var on = folder == _filesFolder;
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 30, Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(0),
                Background = Theme(on ? "PrimaryBrush" : "SecondaryContainerBrush"),
                Foreground = Theme(on ? "OnPrimaryBrush" : "OnSecondaryContainerBrush"),
                Content = new TextBlock { Text = FolderName(folder), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            chip.Click += (_, _) =>
            {
                if (folder == _filesFolder) return;
                _filesFolder = folder;
                _miniStatus = "";
                _ = LoadFilesAsync(folder);
            };
            Grid.SetColumn(chip, i);
            chips.Children.Add(chip);
        }
        _miniFace.Children.Add(chips);
        _downloadBars.Clear();
        if (_filesError is not null)
        {
            _miniFace.Children.Add(Quiet(_filesError));
            return;
        }
        if (_filesEntries is null)
        {
            _miniFace.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, Margin = new Thickness(0, 12, 0, 12) });
            return;
        }
        if (_filesEntries.Count == 0)
        {
            _miniFace.Children.Add(Quiet($"No files in {FolderName(_filesFolder)}."));
            return;
        }
        var list = new StackPanel { Spacing = 6 };
        foreach (var entry in _filesEntries)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = Theme("OnSurfaceVariantBrush"), VerticalAlignment = VerticalAlignment.Center });
            var words = new StackPanel { Spacing = 3 };
            words.Children.Add(new TextBlock { Text = entry.Name, FontSize = 13, Foreground = Theme("OnSurfaceBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = $"{SizeOf(entry.Size)} · {When(entry.Modified)}", FontSize = 11, Foreground = Theme("OnSurfaceVariantBrush") });
            if (_downloading.TryGetValue(entry.FullPath, out var progress))
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = progress, Foreground = Theme("PrimaryBrush"), Background = Theme("OutlineVariantBrush") };
                _downloadBars[entry.FullPath] = bar;
                words.Children.Add(bar);
            }
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            Button action;
            if (_downloaded.TryGetValue(entry.FullPath, out var local))
            {
                action = RoundButton("", "Open " + entry.Name);
                action.Click += (_, _) => OpenLocal(local);
            }
            else
            {
                action = RoundButton("", "Download " + entry.Name);
                action.IsEnabled = !_downloading.ContainsKey(entry.FullPath);
                var e = entry;
                action.Click += async (_, _) => await DownloadAsync(e);
            }
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
            list.Children.Add(new Border { Padding = new Thickness(12, 8, 8, 8), CornerRadius = new CornerRadius(14), Background = Theme("SurfaceContainerHighestBrush"), Child = row });
        }
        _miniFace.Children.Add(Scroller(list));
    }

    /// <summary>The folder's newest files (not its folders), newest first.</summary>
    private async Task LoadFilesAsync(string folder)
    {
        _filesCts?.Cancel();
        var cts = _filesCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (folder != _filesListed) _filesEntries = null;
        _filesError = null;
        RebuildMini();
        try
        {
            var entries = await _src.Files.ListAsync(FolderPath(folder), cts.Token);
            if (cts != _filesCts) return;
            _filesListed = folder;
            _filesEntries = [.. entries.Where(e => !e.IsDirectory && !e.Name.StartsWith('.')).OrderByDescending(e => e.Modified).Take(8)];
        }
        catch (LincException ex)
        {
            if (cts == _filesCts) _filesError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            if (cts == _filesCts) _filesError = "The phone took too long to answer.";
        }
        catch (Exception ex)
        {
            if (cts == _filesCts) _filesError = $"Couldn't read {FolderName(folder)} ({ex.Message}).";
        }
        if (cts == _filesCts) RebuildMini();
    }

    private static string SizeOf(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Into Downloads\Linc, as the Files page saves them.</summary>
    private async Task DownloadAsync(RemoteEntry entry)
    {
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Linc");
        _downloading[entry.FullPath] = 0;
        RebuildMini();
        try
        {
            Directory.CreateDirectory(downloads);
            var progress = new Progress<double>(p =>
            {
                if (!_downloading.ContainsKey(entry.FullPath)) return; // a late report after it finished
                _downloading[entry.FullPath] = p;
                if (_downloadBars.TryGetValue(entry.FullPath, out var bar)) bar.Value = p;
            });
            var local = await _src.Files.PullAsync(entry.FullPath, downloads, progress, CancellationToken.None);
            _downloaded[entry.FullPath] = local;
            _miniStatus = $"Saved to Downloads\\Linc\\{System.IO.Path.GetFileName(local)}";
        }
        catch (Exception ex)
        {
            _miniStatus = ex is LincException ? ex.Message : $"Couldn't download {entry.Name} ({ex.Message}).";
        }
        _downloading.Remove(entry.FullPath);
        RebuildMini();
    }

    private void OpenLocal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Say("That file was moved or deleted since.");
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
}
