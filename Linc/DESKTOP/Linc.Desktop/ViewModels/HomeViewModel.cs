using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Linc.Desktop.ViewModels;

/// <summary>A tappable action chip on a notification (v6). Reply actions get the inline box instead.</summary>
public sealed partial class NotificationActionVm(
    string key, NotificationAction action, INotificationSyncService service)
{
    public string Title => action.Title;

    [RelayCommand]
    private Task FireAsync() => service.FireActionAsync(key, action.Index);
}

public sealed partial class NotificationVm : ObservableObject
{
    private readonly NotificationItem _item;
    private readonly INotificationSyncService _service;
    private readonly NotificationAction? _replyAction;

    public NotificationVm(NotificationItem item, INotificationSyncService service)
    {
        _item = item;
        _service = service;
        ReplyText = "";
        _replyAction = item.Actions?.FirstOrDefault(a => a.AllowsReply);
        Actions = item.Actions is null
            ? []
            : [.. item.Actions.Where(a => !a.AllowsReply)
                .Select(a => new NotificationActionVm(item.Key, a, service))];
    }

    public string? AppPackage => _item.AppPackage;
    public string App => _item.App;
    public string Title => _item.Title.Length > 0 ? _item.Title : _item.App;
    public string Text => _item.Text;
    public string Caption =>
        _item.ConversationTitle is { Length: > 0 } conversation && conversation != Title
            ? $"{_item.App} · {conversation} · {_item.PostedAt.LocalDateTime:t}"
            : $"{_item.App} · {_item.PostedAt.LocalDateTime:t}";

    public IReadOnlyList<NotificationActionVm> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public bool CanReply => _replyAction is not null;

    /// <summary>The app's icon, fetched over the bulk channel; null until it arrives.</summary>
    [ObservableProperty]
    public partial BitmapImage? Icon { get; set; }

    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;

    partial void OnIconChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasNoIcon));
    }

    [ObservableProperty]
    public partial string ReplyText { get; set; }

    [RelayCommand]
    private Task DismissAsync() => _service.DismissAsync(_item.Key);

    [RelayCommand]
    private async Task SendReplyAsync()
    {
        var text = ReplyText?.Trim() ?? "";
        if (_replyAction is null || text.Length == 0)
        {
            return;
        }
        if (await _service.ReplyAsync(_item.Key, _replyAction.Index, text))
        {
            ReplyText = "";
        }
    }
}

/// <summary>One row of the Shared widget — a file the phone sent to this PC.</summary>
public sealed partial class SharedFileVm(ReceivedShare share)
{
    public string Name => share.Name;
    public string LocalPath => share.LocalPath;
    public string Caption => $"From phone · {share.ReceivedAt.LocalDateTime:t}";

    /// <summary>Opens the saved file with whatever app Windows associates with it.</summary>
    [RelayCommand]
    private void Open()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(share.LocalPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The file was moved or deleted since it arrived; nothing useful to say.
        }
    }
}

/// <summary>One row of the clipboard-history widget. In-memory only.</summary>
public sealed partial class ClipVm(ClipEntry entry, IClipboardSyncService service, IConnectionManager connection)
{
    public string Text => entry.Text.Length > 120 ? entry.Text[..120] + "…" : entry.Text;
    public string Caption => $"{(entry.FromPhone ? "From phone" : "From this PC")} · {entry.At.LocalDateTime:t}";
    public bool IsUrl => System.Uri.TryCreate(entry.Text.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == "http" || u.Scheme == "https");

    [RelayCommand]
    private void Recopy() => service.Recopy(entry);

    /// <summary>continue.url: open this link on the phone (v10).</summary>
    [RelayCommand]
    private async Task ContinueOnPhoneAsync()
    {
        try { await connection.ContinueUrlAsync(entry.Text.Trim(), CancellationToken.None); }
        catch (LincException) { /* link is dying; nothing to surface here */ }
    }
}

/// <summary>One recent-photo thumbnail on the Home strip (v10). Click pulls the full image.</summary>
/// <summary>
/// One installed app in Home's Apps section (M6c). Since M7a the tile is <b>interactive</b>:
/// clicking it opens that app in its own PC window. The command is handed in by
/// <see cref="HomeViewModel"/> because a DataTemplate's <c>x:Bind</c> can only see the item, not
/// the page's view model.
/// </summary>
public sealed partial class AppVm(AppInfo info, System.Windows.Input.ICommand? launch = null)
    : ObservableObject
{
    /// <summary>Fired with this <see cref="AppVm"/> as the parameter when the tile is clicked.</summary>
    public System.Windows.Input.ICommand? Launch => launch;

    public string Package => info.Package;

    /// <summary>The phone's own localised name for the app — never derived from the package id.</summary>
    public string Label => info.Label;

    public bool IsSystem => info.IsSystem;

    /// <summary>Null until the icon is fetched or found in the cache — a missing icon is normal,
    /// so the view falls back to a placeholder rather than leaving a hole.</summary>
    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon { get; set; }

    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;

    partial void OnIconChanged(Microsoft.UI.Xaml.Media.Imaging.BitmapImage? value)
    {
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasNoIcon));
    }
}

public sealed partial class PhotoVm(PhotoItem item, IConnectionManager connection, IFileService files, ILogService log) : ObservableObject
{
    public string Id => item.Id;

    /// <summary>Phone-side path, kept so the offline copy can remember which photo this was.</summary>
    public string Path => item.Path;

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail { get; set; }

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc");
            System.IO.Directory.CreateDirectory(dir);
            var path = await files.PullAsync(item.Path, dir, new Progress<double>(), CancellationToken.None);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (LincException ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't fetch that photo: {ex.Message}");
        }
    }
}

/// <summary>
/// One row in the Sections flyout (M6a). Exposes a settable <see cref="IsVisible"/> that calls
/// <see cref="IDeviceRegistry.SaveHome"/> with a guard against the echo bug.
/// </summary>
public sealed partial class HomeSectionToggle : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    private readonly IDeviceRegistry _registry;

    public HomeSectionToggle(string id, string displayName, IDeviceRegistry registry)
    {
        Id = id;
        DisplayName = displayName;
        _registry = registry;
    }

    internal bool IsVisibleInternal
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
            // Guard against echo: only save when the value actually differs from registry state
            if (_registry.Home.IsVisible(Id) != value)
            {
                var current = _registry.Home;
                var updated = current.WithSection(Id, value);
                if (updated != current)
                {
                    _registry.SaveHome(updated);
                }
            }
        }
    }
}

/// <summary>
/// The Home page (docs/ROADMAP.md M14): phone preview card, quick actions, media
/// widget, clipboard history, and the notification shade — the app's daily surface.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly INotificationSyncService _service;
    private readonly IMediaSyncService _media;
    private readonly IConnectionManager _connection;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IClipboardSyncService _clipboard;
    private readonly IFileService _files;
    private readonly IThemeSyncService _theme;
    private readonly IMirrorService _mirror;
    private readonly IDeviceRegistry _registry;
    private readonly IShareService _share;
    private readonly IDeviceCacheService _cache;
    private readonly IAppLaunchService _appLaunch;
    private readonly IDesktopModeService _desktopMode;
    private readonly ILogService _log;
    private readonly LincStore _store;

    /// <summary>
    /// The per-device app cache (M6c). Its root comes from the registry, never from
    /// <see cref="Environment.GetFolderPath"/> — that is what keeps a harness on a temp root out
    /// of the owner's real <c>cache\</c> (D-057/D-058).
    /// </summary>
    private readonly AppCatalog _appCatalog;

    private readonly Dictionary<string, BitmapImage?> _appIcons = [];
    private readonly HashSet<string> _iconFetchesInFlight = [];
    private string? _artId;

    // Media position estimation between status pushes.
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _basePositionMs;
    private long _basePositionAtTick;
    private bool _seekDragging;

    public ObservableCollection<NotificationVm> Items { get; } = [];
    public ObservableCollection<ClipVm> Clips { get; } = [];
    public ObservableCollection<PhotoVm> Photos { get; } = [];

    /// <summary>Files the phone shared, newest first — the Shared widget (M00 follow-up).</summary>
    public ObservableCollection<SharedFileVm> ReceivedShares { get; } = [];

    public bool HasReceivedShares => ReceivedShares.Count > 0;
    public bool HasNoReceivedShares => ReceivedShares.Count == 0;

    // ---- Offline device memory (M02, D-032) ----

    /// <summary>
    /// Always 1.0 (M9d-1, §2.4). Dimming was D-032's original offline affordance; it is now the
    /// banner instead (<see cref="ShowOfflineBanner"/>/<see cref="OfflineBannerText"/>, §2.5).
    /// Kept as a property, not deleted — HomePage.xaml still binds it and other code reads it.
    /// </summary>
    public double ContentOpacity => 1.0;

    private void RefreshCachedState()
    {
        OnPropertyChanged(nameof(ContentOpacity));
    }

    // ---- sync_cache offline banner (M9d-1, §2.5) ----
    //
    // This banner is the single offline statement on Home (M13f §7): the older per-pane banner
    // that duplicated it — and rendered behind the tab buttons — was removed with its
    // IsShowingCached/LastSeenText state. It tracks what LoadCachedLaneWidgetsAsync actually
    // restored from sync_cache (messages/calls/photos) and is what §2.5 asks for.

    /// <summary>updated_utc of every row LoadCachedLaneWidgetsAsync last restored, across all
    /// three kinds, for the active serial. Empty until a read-through has actually run.</summary>
    private IReadOnlyList<DateTimeOffset> _restoredCacheTimestamps = [];

    /// <summary>"Showing what was last synced at {time}.", or null if nothing was restored (§2.5).
    /// Pure formatting lives in <see cref="HomeOfflineBanner"/> so tools\homecachesim can exercise
    /// it directly. The banner states cache age only — the link's own state is the Phone card's
    /// ConnectionCaption, once (M15b D2).</summary>
    public string? OfflineBannerText => HomeOfflineBanner.FormatText(_registry.PairedSerial, _restoredCacheTimestamps);

    /// <summary>Bound to the banner's Visibility (x:Bind only converts bool, never string, §BRAIN.md).</summary>
    public bool ShowOfflineBanner => !IsConnected && OfflineBannerText is not null;

    private void RefreshOfflineBanner()
    {
        OnPropertyChanged(nameof(OfflineBannerText));
        OnPropertyChanged(nameof(ShowOfflineBanner));
    }

    public HomeViewModel(
        INotificationSyncService service,
        IMediaSyncService media,
        IConnectionManager connection,
        IConnectionSupervisor supervisor,
        IClipboardSyncService clipboard,
        IFileService files,
        IThemeSyncService theme,
        IMirrorService mirror,
        IDeviceRegistry registry,
        IShareService share,
        IDeviceCacheService cache,
        IAppLaunchService appLaunch,
        IDesktopModeService desktopMode,
        ILogService log,
        LincStore store)
    {
        _share = share;
        _cache = cache;
        _appLaunch = appLaunch;
        _desktopMode = desktopMode;
        _store = store;
        _service = service;
        _media = media;
        _connection = connection;
        _supervisor = supervisor;
        _clipboard = clipboard;
        _files = files;
        _theme = theme;
        _mirror = mirror;
        _registry = registry;
        _log = log;
        _appCatalog = new AppCatalog(registry.RootPath, log);
        ReplyText = "";
        DialNumber = "";
        IncomingText = "";
        SelectedTab = "notifications";
        ShareStatus = "";
        AppsMessage = "";

        MediaArt = null;
        QuickActionMessage = "";
        BatteryText = "—";
        SignalText = "—";
        IsDndOn = false;
        SoundMode = "normal";
        MediaPositionMs = 0;
        MediaDurationMs = 0;

        _service.Start();
        _media.Start();
        _service.Changed += Rebuild; // service raises on the UI thread
        _media.Changed += RefreshMedia;
        _clipboard.HistoryChanged += RebuildClips;
        _theme.PaletteChanged += RefreshGradient;
        _theme.WallpaperChanged += () =>
        {
            OnPropertyChanged(nameof(Wallpaper));
            OnPropertyChanged(nameof(HasWallpaper));
        };
        _positionTimer.Tick += (_, _) => TickPosition();

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _supervisor.StateChanged += () => dispatcher.TryEnqueue(() =>
        {
            RefreshDeviceCard();
            RefreshLaneWidgets();
            RefreshCachedState();
            // Apps: pull on connect and reconcile (D-058 — there is no install/removal push).
            // RefreshApps() also re-raises the empty-state text, which changes with the link.
            RefreshApps();
            _ = RefreshAppsAsync();
            // 2.7: a window pointed at a phone that is gone is an orphan. Same precedent as
            // MirrorViewModel, which stops the mirror on exactly this event.
            if (!IsConnected)
            {
                _phoneMetrics = null;
                _phoneMetricsSerial = null;
                if (_appLaunch.OpenPackages.Count > 0)
                {
                    // M15b D2: about the windows, not the link — the Phone card is already
                    // saying the link went down, and this fired at the same instant.
                    AppsMessage = "Those app windows closed.";
                    _ = _appLaunch.CloseAllAsync();
                }
            }
        });
        _appLaunch.ErrorRaised += message => dispatcher.TryEnqueue(() => AppsMessage = message);
        _appLaunch.WindowsChanged += () => dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(HasOpenAppWindows));
            OnPropertyChanged(nameof(OpenAppWindowsText));
        });
        _supervisor.StatusUpdated += status => dispatcher.TryEnqueue(() => OnStatusUpdated(status));
        _connection.CompanionMessageReceived += envelope => dispatcher.TryEnqueue(() => OnCompanionMessage(envelope));
        _registry.SyncLanesChanged += () => dispatcher.TryEnqueue(RefreshLaneWidgets);
        // The Home empty-state CTA appears/disappears as phones are paired or every tab is closed.
        _registry.KnownDevicesChanged += () => dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(HasNoVisibleDevice)));
        _registry.ActiveDeviceChanged += () => dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(HasNoVisibleDevice)));
        // §2.6: switching device tabs while offline must show the NEW tab's cache, never a blend
        // of the old one's. RefreshLaneWidgets already keys its cache read off _registry.PairedSerial,
        // which SetActiveDevice updates before this event fires, so re-running it here is enough.
        _registry.ActiveDeviceChanged += () => dispatcher.TryEnqueue(RefreshLaneWidgets);
        _share.ShareReceived += received => dispatcher.TryEnqueue(() => AddShare(received));
        foreach (var already in _share.Received)
        {
            AddShare(already); // anything the sweep pulled before this page existed
        }
        // Render the remembered device immediately, so a launch with the phone away shows the
        // last-known page dimmed instead of an empty one (M02, D-032). Live data overwrites it
        // through the very same path the moment the phone answers.
        if (!IsConnected && _cache.Current is { } cached)
        {
            if (cached.Status is { } remembered)
            {
                OnStatusUpdated(remembered);
            }
            foreach (var photo in cached.Photos ?? [])
            {
                var vm = new PhotoVm(new PhotoItem(photo.Id, photo.Path, 0), _connection, _files, _log);
                Photos.Add(vm);
                _ = LoadThumbAsync(vm); // falls back to the remembered thumbnail while offline
            }
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasNoPhotos));
            OnPropertyChanged(nameof(ShowPhotosWidget));
        }
        RefreshCachedState();

RefreshGradient();
        RefreshDeviceCard();
        RefreshLaneWidgets();

        // Cached apps go up immediately — instant, synchronous, no phone needed (M6c). If we are
        // already connected the page was created after the connect fired, so kick the refresh too
        // (the M1 lazy-load bug: these view models are built on first navigation, well after the
        // launch-time connect that would otherwise have been the only trigger).
        LoadAppsFromCache();
        if (IsConnected)
        {
            _ = RefreshAppsAsync();
        }

        // ---- Home sections (M6a) ----
        _sectionToggles = BuildSectionToggles();
        _registry.HomeChanged += () => _dispatcher.TryEnqueue(RefreshSections);
        _registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(RefreshSections);
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private readonly IReadOnlyList<HomeSectionToggle> _sectionToggles;

    public IReadOnlyList<HomeSectionToggle> SectionToggles => _sectionToggles;

    private IReadOnlyList<HomeSectionToggle> BuildSectionToggles()
    {
        var ids = HomeLayout.AllSectionIds;
        var toggles = new HomeSectionToggle[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var toggle = new HomeSectionToggle(id, HomeLayout.DisplayName(id), _registry)
            {
                IsVisibleInternal = _registry.Home.IsVisible(id)
            };
            toggles[i] = toggle;
        }
        return toggles;
    }

    private void RefreshSections()
    {
        foreach (var toggle in _sectionToggles)
        {
            toggle.IsVisibleInternal = _registry.Home.IsVisible(toggle.Id);
        }
        OnPropertyChanged(nameof(ShowPhone));
        OnPropertyChanged(nameof(ShowQuickActions));
        OnPropertyChanged(nameof(ShowApps));
        OnPropertyChanged(nameof(ShowMedia));
        OnPropertyChanged(nameof(ShowPhotos));
        OnPropertyChanged(nameof(ShowShared));
        OnPropertyChanged(nameof(ShowClipboard));
        OnPropertyChanged(nameof(ShowMediaWidget));
        OnPropertyChanged(nameof(ShowPhotosWidget));
        OnPropertyChanged(nameof(ShowSharedWidget));
        OnPropertyChanged(nameof(AllSectionsHidden));
        OnPropertyChanged(nameof(PanesSwapped));
        OnPropertyChanged(nameof(AppsPaneHeight));
    }

    // Visibility properties bound by the cards in HomePage.xaml
    public bool ShowPhone => _registry.Home.IsVisible("phone");
    public bool ShowQuickActions => _registry.Home.IsVisible("quickactions");
    public bool ShowApps => _registry.Home.IsVisible("apps");
    public bool ShowMedia => _registry.Home.IsVisible("media");
    public bool ShowPhotos => _registry.Home.IsVisible("photos");
    public bool ShowShared => _registry.Home.IsVisible("shared");
    public bool ShowClipboard => _registry.Home.IsVisible("clipboard");

    // Combined visibility: section enabled AND data available (for Media, Photos, Shared).
    // Each is composed of two independent sources, so EVERY site that raises either half must
    // also raise the combined name — RefreshSections covers the layout half, and the data half is
    // raised alongside HasMedia / HasPhotos / HasReceivedShares (M6a regression, fixed in M6b).
    public bool ShowMediaWidget => ShowMedia && HasMedia;
    public bool ShowPhotosWidget => ShowPhotos && HasPhotos;
    public bool ShowSharedWidget => ShowShared && HasReceivedShares;

    public bool AllSectionsHidden =>
        !ShowPhone && !ShowQuickActions && !ShowApps && !ShowMedia && !ShowPhotos && !ShowShared && !ShowClipboard;

    [RelayCommand]
    private void SetSectionVisible((string Id, bool Visible) arg)
    {
        var current = _registry.Home;
        var updated = current.WithSection(arg.Id, arg.Visible);
        if (updated == current) return; // no-op guard
        _registry.SaveHome(updated);
    }

    // ---- Pane order (M6b) ----

    /// <summary>True when the widgets pane sits on the right and the tabbed panel on the left.</summary>
    public bool PanesSwapped => _registry.Home.PanesSwapped;

    /// <summary>
    /// "Swap sides": flips the two panes and persists it for the active device. The target is
    /// always derived from the registry's current value and only written when it differs, so the
    /// HomeChanged echo (which re-raises <see cref="PanesSwapped"/>) cannot re-trigger the write.
    /// </summary>
    [RelayCommand]
    private void SwapPanes()
    {
        var current = _registry.Home;
        var updated = current.WithPanesSwapped(!current.PanesSwapped);
        if (updated == current) return; // no-op guard: never save a value the registry already holds
        _registry.SaveHome(updated);
    }

    // ---- Apps / tabs split (M6c-3) ----

    /// <summary>Persisted height of the Apps pane; null = the page's default split.</summary>
    public double? AppsPaneHeight => _registry.Home.AppsPaneHeight;

    /// <summary>
    /// Persists the dragged split for the active device. Same echo guard as
    /// <see cref="SwapPanes"/>: the target is derived from the registry's current value and only
    /// written when it differs (WithAppsPaneHeight rounds and returns <c>this</c> on no change), so
    /// the HomeChanged echo — which re-raises <see cref="AppsPaneHeight"/> — cannot re-enter the
    /// write. The view filters the echo a second time against the height it last applied, so it
    /// cannot turn into a resize either.
    /// </summary>
    public void SaveAppsPaneHeight(double height)
    {
        var current = _registry.Home;
        var updated = current.WithAppsPaneHeight(height);
        if (updated == current) return; // no-op guard: never save a value the registry already holds
        _registry.SaveHome(updated);
    }

    // ---- Right-panel tabs (M10): Notifications | Photos | Messages | Calls ----

    [ObservableProperty] public partial string SelectedTab { get; set; }

    public bool IsNotificationsTab => SelectedTab == "notifications";
    public bool IsPhotosTab => SelectedTab == "photos";
    public bool IsMessagesTab => SelectedTab == "messages";
    public bool IsCallsTab => SelectedTab == "calls";
    public bool IsSharedTab => SelectedTab == "shared";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsNotificationsTab));
        OnPropertyChanged(nameof(IsPhotosTab));
        OnPropertyChanged(nameof(IsMessagesTab));
        OnPropertyChanged(nameof(IsCallsTab));
        OnPropertyChanged(nameof(IsSharedTab));
    }

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ---- Installed apps (v16, M6c / D-058) ----
    //
    // Cache-first: Load() is synchronous and instant, so the card has content the moment Home
    // appears, and the refresh reconciles behind it when the phone answers. There is deliberately
    // no install/removal push event, so a connect is the only refresh trigger.
    //
    // NO ECHO LOOP: nothing in this path writes to the registry, so it cannot raise HomeChanged
    // and re-enter RefreshSections. The only write is to the app cache, which raises nothing. The
    // pull itself is guarded by _appsRefreshInFlight so overlapping StateChanged events (a
    // reconnect storm) queue one request, not a pile of them; and icon fetches are guarded by
    // _appIconsInFlight plus _appIconMisses, so a package with no icon is asked for once, ever —
    // a missing icon is normal and must not become a retry loop.

    public ObservableCollection<AppVm> Apps { get; } = [];

    private readonly HashSet<string> _appIconsInFlight = [];
    private readonly HashSet<string> _appIconMisses = [];
    private bool _appsRefreshInFlight;

    public bool HasApps => Apps.Count > 0;

    /// <summary>Card body visibility: the list, or one of the two empty states.</summary>
    public bool HasNoApps => Apps.Count == 0;

    /// <summary>
    /// True when the connected phone speaks v16. The card is <b>disabled, not hidden</b>, on an
    /// older phone (M6c): hiding it would leave the owner wondering where the section went.
    /// </summary>
    public bool AppsSupported => AppsPayload.IsSupported(_supervisor.Device?.Companion?.V);

    /// <summary>Dims the card on a pre-v16 phone — the "disabled, don't hide" affordance.</summary>
    public double AppsOpacity => AppsSupported ? 1.0 : 0.55;

    /// <summary>
    /// The empty-state line, in plain language and never mentioning a version number — and, since
    /// M15b D2, never a statement about the link either. The disconnected branch used to read
    /// "Connect your phone to see the apps installed on it.", which rendered at the same moment as
    /// the Phone card's caption and the offline banner. The Phone card owns that sentence; this
    /// card says only what is true of the LIST.
    /// </summary>
    public string AppsEmptyText =>
        !IsConnected ? "No apps to show yet."
        : !AppsSupported ? AppsPayload.UnsupportedReason
        : "No apps to show yet — Linc is asking your phone.";

    // ---- Opening an app in its own window (M7a, D-059) ----

    /// <summary>
    /// The Apps section's own status line: why a launch failed, or that a disconnect took the
    /// windows with it. Never raw scrcpy output (2.6).
    /// </summary>
    [ObservableProperty]
    public partial string AppsMessage { get; set; }

    public bool HasAppsMessage => AppsMessage.Length > 0;

    partial void OnAppsMessageChanged(string value) => OnPropertyChanged(nameof(HasAppsMessage));

    public bool HasOpenAppWindows => _appLaunch.OpenWindowCount > 0;

    /// <summary>
    /// "2 app windows open" — the modest affordance M7b asks for, and deliberately nothing more:
    /// no per-window list, no thumbnails, no docking. It is shown only while something is open.
    /// </summary>
    public string OpenAppWindowsText => _appLaunch.OpenWindowCount switch
    {
        0 => "",
        1 => "1 app window open",
        var n => $"{n} app windows open",
    };

    /// <summary>Closes every open app window — the same path a disconnect takes (2.7).</summary>
    [RelayCommand]
    private async Task CloseAllAppWindowsAsync()
    {
        try
        {
            await _appLaunch.CloseAllAsync();
            AppsMessage = "";
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"Couldn't close the app windows: {ex.Message}");
            AppsMessage = "Linc couldn't close the app windows. Close them from the taskbar instead.";
        }
    }

    /// <summary>
    /// The phone's real shape, read once per serial. Cached because the launch path must not turn
    /// into an ADB round trip per click, and cleared on disconnect so a different phone is measured
    /// afresh.
    /// </summary>
    private (int Width, int Height, int Dpi)? _phoneMetrics;
    private string? _phoneMetricsSerial;

    /// <summary>
    /// Packages whose launch is in flight. A launch is only ever started by a click — no status
    /// refresh, no StateChanged handler, nothing on the pull path touches it — but a double click
    /// arrives twice before the process is registered, and this is what keeps that to one window.
    /// </summary>
    private readonly HashSet<string> _launchesInFlight = [];

    /// <summary>
    /// Opens one app in its own PC window. Disabled while disconnected or on a pre-v16 phone (the
    /// app list itself is stale then, and scrcpy would fail with an unreadable message anyway).
    /// </summary>
    [RelayCommand]
    private async Task LaunchAppAsync(AppVm? app)
    {
        if (app is null)
        {
            return;
        }
        if (!IsConnected || _supervisor.Device is not { } device)
        {
            // M15b D2: an instruction about this app, not a statement of link state. Unlike the
            // others this one only appears on a click, so it never stacked with the Phone card —
            // reworded anyway so Home has exactly one sentence about the connection.
            AppsMessage = "Open this app again once your phone is linked.";
            return;
        }
        if (!AppsSupported)
        {
            AppsMessage = AppsPayload.UnsupportedReason;
            return;
        }
        if (!_launchesInFlight.Add(app.Package))
        {
            return; // already opening this one
        }
        try
        {
            if (_appLaunch.IsOpen(app.Package))
            {
                // 2.2: focus the window that exists rather than opening a second one.
                await _appLaunch.LaunchAsync(device.Serial, app.Package, app.Label, _phoneMetrics);
                AppsMessage = "";
                return;
            }

            AppsMessage = $"Opening {app.Label}…";
            var metrics = await GetPhoneMetricsAsync(device);
            await _appLaunch.LaunchAsync(device.Serial, app.Package, app.Label, metrics);
            AppsMessage = "";
        }
        catch (LincException ex)
        {
            AppsMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"Couldn't open {app.Package}: {ex.Message}");
            AppsMessage = $"Linc couldn't open {app.Label} on this PC.";
        }
        finally
        {
            _launchesInFlight.Remove(app.Package);
        }
    }

    /// <summary>
    /// The phone's <c>wm size</c>/<c>wm density</c>, cached per serial. Null when it cannot be read
    /// — the window then uses AppLaunchService's phone-shaped fallback rather than refusing to open.
    /// </summary>
    private async Task<(int Width, int Height, int Dpi)?> GetPhoneMetricsAsync(ConnectedDevice device)
    {
        if (_phoneMetrics is not null && _phoneMetricsSerial == device.Serial)
        {
            return _phoneMetrics;
        }
        var adbDevice = new AdvancedSharpAdbClient.Models.DeviceData
        {
            Serial = device.Serial,
            State = AdvancedSharpAdbClient.Models.DeviceState.Online
        };
        var metrics = await _desktopMode.GetPhoneDisplayMetricsAsync(adbDevice, CancellationToken.None);
        if (metrics is not null)
        {
            _phoneMetrics = metrics;
            _phoneMetricsSerial = device.Serial;
        }
        return metrics;
    }

    private void RefreshApps()
    {
        OnPropertyChanged(nameof(AppsSupported));
        OnPropertyChanged(nameof(AppsOpacity));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasNoApps));
        OnPropertyChanged(nameof(AppsEmptyText));
    }

    /// <summary>Fills the list from the on-disk cache — synchronous, no network, never throws.</summary>
    /// <remarks>
    /// M9e (A2.5): this used to key off <c>_supervisor.Device?.Serial</c>, which is only set once a
    /// live connection has already succeeded — but the one call site is in the constructor, before
    /// any connection has had a chance to complete, so that guard always no-opped and the cached
    /// Apps list never appeared until after a live phone answered at least once this session
    /// (contradicting this method's own "synchronous, no network" doc above). Keyed off
    /// <see cref="IDeviceRegistry.PairedSerial"/> instead — available immediately from
    /// settings.json, same source <see cref="LoadCachedLaneWidgetsAsync"/> already uses.
    /// </remarks>
    private void LoadAppsFromCache()
    {
        if (_registry.PairedSerial is not { } serial)
        {
            return;
        }
        var cached = AppsPayload.SortForDisplay(_appCatalog.Load(serial));
        if (cached.Count == 0)
        {
            return; // nothing cached yet; the refresh will fill it
        }
        Apps.Clear();
        ResetRealizedTiles();
        foreach (var app in cached)
        {
            Apps.Add(new AppVm(app, LaunchAppCommand));
        }
        RefreshApps();
        // No icon sweep here any more (M7b): the repeater asks for the icons of the tiles it
        // realizes, which on a 300-app phone is a screenful rather than 300 bulk requests.
    }

    private async Task RefreshAppsAsync()
    {
        if (_appsRefreshInFlight || !IsConnected || !AppsSupported)
        {
            return;
        }
        if (_supervisor.Device?.Serial is not { } serial)
        {
            return;
        }
        _appsRefreshInFlight = true;
        try
        {
            var fresh = await _connection.GetAppsAsync(CancellationToken.None);
            // Reconcile, don't replace: the log line says what actually changed on the phone.
            _appCatalog.Reconcile(serial, fresh);

            Apps.Clear();
            ResetRealizedTiles();
            foreach (var app in AppsPayload.SortForDisplay(fresh))
            {
                Apps.Add(new AppVm(app, LaunchAppCommand));
            }
            RefreshApps();
        }
        catch (LincException ex)
        {
            // Never silent (BRAIN.md): the cached list stays on screen and the reason is logged.
            _log.Log(LogLevel.Warn, $"Couldn't list the phone's apps: {ex.Message}");
        }
        finally
        {
            _appsRefreshInFlight = false;
        }
    }

    // ---- Realized tiles: the lazy-icon trigger, and the virtualization proof (M7b) ----
    //
    // M6c fetched an icon for every app in the list, because the repeater realized every tile and
    // "only what is displayed" therefore meant "all of it" (the BRAIN note). Now the grid actually
    // virtualizes, so the trigger is the repeater itself: ElementPrepared realizes a tile and asks
    // for that one icon; ElementClearing recycles it.
    //
    // All three M6c guards survive unchanged and are what make scrolling cheap: _appIconMisses
    // means a package with no icon is asked ONCE, ever, however many times its tile is realized;
    // _appIconsInFlight means a fast scroll back and forth cannot queue the same fetch twice; and
    // an AppVm keeps its BitmapImage when its tile is recycled, so scrolling back re-shows a
    // decoded icon with no round trip and no disk read.

    /// <summary>
    /// Packages whose tiles the repeater has realized and not yet cleared — the live
    /// virtualization measure (M7b). A set, not a counter: realized-minus-recycled can drift
    /// (the "28 tile(s) of 15 app(s)" log), a set of what is actually on screen cannot — a tile
    /// is either realized or it isn't, and a package has exactly one tile.
    /// </summary>
    private readonly HashSet<string> _realizedPackages = [];

    /// <summary>Tiles the repeater has realized and not yet cleared — the virtualization measure.</summary>
    public int RealizedTileCount => _realizedPackages.Count;

    /// <summary>
    /// The most tiles realized at once this session. Exposed (and logged) because "it virtualizes"
    /// is a claim that needs a number behind it: with a few hundred apps this must stay near a
    /// screenful, not near the list length.
    /// </summary>
    public int PeakRealizedTileCount { get; private set; }

    /// <summary>
    /// One tile came on screen. The ONLY place an icon fetch is now started.
    /// </summary>
    public void OnAppTileRealized(AppVm? app)
    {
        if (app is not null && _realizedPackages.Add(app.Package) &&
            _realizedPackages.Count > PeakRealizedTileCount)
        {
            PeakRealizedTileCount = _realizedPackages.Count;
            _log.Log(LogLevel.Info,
                $"Apps: {PeakRealizedTileCount} tile(s) realized at once, of {Apps.Count} app(s).");
        }
        OnPropertyChanged(nameof(RealizedTileCount));
        OnPropertyChanged(nameof(PeakRealizedTileCount));

        if (app is null || app.Icon is not null || _appIconMisses.Contains(app.Package))
        {
            return;
        }
        if (_appIconsInFlight.Add(app.Package))
        {
            _ = LoadAppIconForAppAsync(app);
        }
    }

    /// <summary>
    /// One tile went off screen and its container is being recycled. Nothing is cancelled: an
    /// in-flight fetch that lands after the tile scrolled away still fills the AppVm and still
    /// caches the PNG, so scrolling back is free.
    /// </summary>
    public void OnAppTileUnrealized(AppVm? app)
    {
        if (app is not null)
        {
            _realizedPackages.Remove(app.Package);
        }
        OnPropertyChanged(nameof(RealizedTileCount));
    }

    /// <summary>
    /// The list changed under the repeater, so every previous realization is void. Called where
    /// Apps is rebuilt; the repeater re-prepares whatever is on screen straight after.
    /// </summary>
    private void ResetRealizedTiles()
    {
        _realizedPackages.Clear();
        OnPropertyChanged(nameof(RealizedTileCount));
    }

    private async Task LoadAppIconForAppAsync(AppVm vm)
    {
        try
        {
            if (_supervisor.Device?.Serial is not { } serial)
            {
                return;
            }
            if (_appCatalog.LoadIcon(serial, vm.Package) is { Length: > 0 } cached)
            {
                vm.Icon = await ToImageAsync(cached);
                return;
            }
            if (!IsConnected)
            {
                return; // offline and uncached: the placeholder stands until the phone is back
            }
            var bytes = await _connection.FetchBulkAsync("appIcon", vm.Package, CancellationToken.None);
            if (bytes is not { Length: > 0 })
            {
                // A missing icon is normal. Remember the miss so this is asked once, not per refresh.
                _appIconMisses.Add(vm.Package);
                return;
            }
            _appCatalog.SaveIcon(serial, vm.Package, bytes);
            vm.Icon = await ToImageAsync(bytes);
        }
        catch (Exception ex) when (ex is LincException or ArgumentException or InvalidOperationException)
        {
            // Undecodable or unreachable: placeholder, one log line, no retry storm.
            _appIconMisses.Add(vm.Package);
            _log.Log(LogLevel.Warn, $"Couldn't load the icon for {vm.Package}: {ex.Message}");
        }
        finally
        {
            _appIconsInFlight.Remove(vm.Package);
        }
    }

    // ---- Recent photos (v10) ----

    public bool HasPhotos => Photos.Count > 0;
    public bool HasNoPhotos => Photos.Count == 0;

    private async Task LoadPhotosAsync()
    {
        if (_supervisor.Device is not { Companion.V: >= 10 })
        {
            return; // pre-v10 phone: no photos surface
        }
        try
        {
            var recent = await _connection.GetRecentPhotosAsync(15, CancellationToken.None);
            Photos.Clear();
            foreach (var item in recent)
            {
                var vm = new PhotoVm(item, _connection, _files, _log);
                Photos.Add(vm);
                _ = LoadThumbAsync(vm);
            }
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasNoPhotos));
            OnPropertyChanged(nameof(ShowPhotosWidget));

            // §2.1 write-through: identity only (id/path/takenAt) — thumbnail bytes stay in the
            // existing M6c/M02 file cache, not sync_cache.
            if (_supervisor.Device?.Serial is { } serial)
            {
                var rows = recent
                    .Select(p => (p.Id, JsonSerializer.Serialize(new CachedSyncPhoto(p.Id, p.Path, p.TakenAt))))
                    .ToList();
                _ = WriteThroughCacheAsync(serial, HomeCacheKinds.Photo, rows);
            }
        }
        catch (LincException)
        {
            // No photos surface (e.g. grant missing); the strip stays empty.
        }
    }

    private async Task LoadThumbAsync(PhotoVm vm)
    {
        try
        {
            var bytes = await _connection.FetchBulkAsync("photo", vm.Id, CancellationToken.None);
            if (bytes is { Length: > 0 })
            {
                // Remember it, so the strip still has pictures next launch with the phone away.
                if (_supervisor.Device?.Serial is { } serial)
                {
                    _cache.SavePhoto(serial, vm.Id, vm.Path, bytes);
                }
                vm.Thumbnail = await ToImageAsync(bytes);
                return;
            }
        }
        catch (Exception)
        {
            // Cosmetic; fall through to whatever was remembered.
        }

        if (_supervisor.Device?.Serial is { } cachedSerial
            && _cache.LoadPhotoThumbnail(cachedSerial, vm.Id) is { Length: > 0 } remembered)
        {
            vm.Thumbnail = await ToImageAsync(remembered);
        }
    }

    // ---- Shared-files widget (M00 follow-up: see files arrive from the phone) ----

    private void AddShare(ReceivedShare share)
    {
        if (ReceivedShares.Any(s => s.LocalPath == share.LocalPath))
        {
            return;
        }
        ReceivedShares.Insert(0, new SharedFileVm(share));
        while (ReceivedShares.Count > 8)
        {
            ReceivedShares.RemoveAt(ReceivedShares.Count - 1);
        }
        OnPropertyChanged(nameof(HasReceivedShares));
        OnPropertyChanged(nameof(HasNoReceivedShares));
        OnPropertyChanged(nameof(ShowSharedWidget));
    }

    /// <summary>Status line under the Shared tab's send button.</summary>
    [ObservableProperty] public partial string ShareStatus { get; set; }

    /// <summary>True while a send is in flight — drives the progress bar on the Shared tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSending))]
    public partial bool IsSending { get; set; }

    public bool IsNotSending => !IsSending;

    /// <summary>Sends PC files to the phone's Share tab (the picker lives in the code-behind).</summary>
    public async Task SendFilesToPhoneAsync(IReadOnlyList<string> paths)
    {
        IsSending = true;
        try
        {
            var done = 0;
            foreach (var path in paths)
            {
                var name = System.IO.Path.GetFileName(path);
                try
                {
                    ShareStatus = paths.Count > 1
                        ? $"Sending {name} ({done + 1} of {paths.Count})…"
                        : $"Sending {name}…";
                    await _share.SendFileToPhoneAsync(path, CancellationToken.None);
                    done++;
                    ShareStatus = paths.Count > 1
                        ? $"Sent {done} of {paths.Count} to your phone"
                        : $"Sent {name} to your phone";
                }
                catch (LincException ex)
                {
                    ShareStatus = ex.Message;
                }
                catch (Exception)
                {
                    ShareStatus = $"Couldn't send {name} to your phone.";
                }
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Reveals the folder received files land in.</summary>
    [RelayCommand]
    private void OpenSharedFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_share.ReceivedFolder);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_share.ReceivedFolder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _log.Log(LogLevel.Warn, "Couldn't open the shared-files folder.");
        }
    }

    // ---- Messages & Calls widgets (M10: the Sync lanes surfaced on Home) ----

    public ObservableCollection<ConversationVm> Conversations { get; } = [];
    public ObservableCollection<CallVm> RecentCalls { get; } = [];

    /// <summary>
    /// The Messages widget shows whenever its lane is on (M9d-1, §2.3) — a lane the user turned
    /// on stays on while offline; what changes is where the rows come from (sync_cache), not
    /// whether the panel exists. Dropped the old <c>Companion.V</c> device-version gate too: it
    /// read <c>_supervisor.Device</c>, which is null the moment the phone disconnects, so keeping
    /// it would have silently forced this back off exactly while offline — the one case this
    /// task exists to fix. See the M9d-1 report for this call.
    /// </summary>
    public bool MessagesOn => _registry.SyncLane("messages");
    public bool CallsOn => _registry.SyncLane("calls");
    public bool MessagesOff => !MessagesOn;
    public bool CallsOff => !CallsOn;

    public bool HasConversations => Conversations.Count > 0;
    public bool NoConversations => Conversations.Count == 0;
    public bool HasCalls => RecentCalls.Count > 0;

    [ObservableProperty] public partial ConversationVm? Selected { get; set; }
    [ObservableProperty] public partial string ReplyText { get; set; }
    [ObservableProperty] public partial string DialNumber { get; set; }
    [ObservableProperty] public partial bool IncomingRinging { get; set; }
    [ObservableProperty] public partial string IncomingText { get; set; }

    public bool CanReply => Selected is not null;
    partial void OnSelectedChanged(ConversationVm? value) => OnPropertyChanged(nameof(CanReply));

    /// <summary>sync_cache's retention cap — no row-count control exists for this table (§2.9).</summary>
    private const int MaxCachedRowsPerKind = 200;

    private void RefreshLaneWidgets()
    {
        if (IsConnected)
        {
            _ = LoadPhotosAsync();
            if (MessagesOn) { _ = LoadMessagesAsync(); } else { Conversations.Clear(); Selected = null; }
            if (CallsOn) { _ = LoadCallsAsync(); } else { RecentCalls.Clear(); }
        }
        else
        {
            // D-032/§2.2: the phone going away must never blank Home. What used to Clear() the
            // three lists here now reads them back from sync_cache instead — see
            // LoadCachedLaneWidgetsAsync. (Kept out of this branch on purpose: a crude source-text
            // check in tools\homecachesim greps exactly this branch for a reverted Clear() call.)
            IncomingRinging = false;
            _ = LoadCachedLaneWidgetsAsync();
        }
        OnPropertyChanged(nameof(MessagesOn));
        OnPropertyChanged(nameof(CallsOn));
        OnPropertyChanged(nameof(MessagesOff));
        OnPropertyChanged(nameof(CallsOff));
        OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(NoConversations));
        OnPropertyChanged(nameof(HasCalls));
        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(HasNoPhotos));
        OnPropertyChanged(nameof(ShowPhotosWidget));
        RefreshOfflineBanner();
    }

    /// <summary>
    /// §2.2's read-through: repopulates Photos/Conversations/RecentCalls from sync_cache for the
    /// active serial instead of leaving them empty. Runs on construction and on every transition
    /// out of Connected (both go through <see cref="RefreshLaneWidgets"/>), and again whenever the
    /// active device tab changes while offline (§2.6 — keyed off <see cref="IDeviceRegistry.PairedSerial"/>,
    /// which is per-tab, never a blend). Fire-and-forget with a catch-all (§2.7): sync_cache reads
    /// never throw by contract, but nothing here may reach the UI thread's exception handler either
    /// way.
    /// <para><b>M9e:</b> the construction-time call races <c>App.xaml.cs</c>'s fire-and-forget
    /// <see cref="LincStore.EnsureSchemaAsync"/> — on a cold launch this method can run before the
    /// store marks itself <see cref="LincStore.IsAvailable"/>, so every read below silently comes
    /// back empty and nothing ever retries. <see cref="LincStore.EnsureSchemaAsync"/> is idempotent
    /// (its own doc: "safe to call at startup"), so awaiting it here first closes the race without
    /// duplicating any work App.xaml.cs already does.</para>
    /// </summary>
    private async Task LoadCachedLaneWidgetsAsync()
    {
        var serial = _registry.PairedSerial;
        if (serial is null)
        {
            Photos.Clear();
            Conversations.Clear();
            Selected = null;
            RecentCalls.Clear();
            _restoredCacheTimestamps = [];
            RefreshOfflineBanner();
            return;
        }

        await _store.EnsureSchemaAsync();

        try
        {
            var photoRows = _store.ListSyncCacheRows(serial, HomeCacheKinds.Photo, MaxCachedRowsPerKind);
            var convoRows = _store.ListSyncCacheRows(serial, HomeCacheKinds.Conversation, MaxCachedRowsPerKind);
            var callRows = _store.ListSyncCacheRows(serial, HomeCacheKinds.Call, MaxCachedRowsPerKind);

            Photos.Clear();
            foreach (var row in photoRows)
            {
                if (JsonSerializer.Deserialize<CachedSyncPhoto>(row.PayloadJson) is not { } cached)
                {
                    continue;
                }
                var vm = new PhotoVm(new PhotoItem(cached.Id, cached.Path, cached.TakenAt), _connection, _files, _log);
                Photos.Add(vm);
                _ = LoadThumbAsync(vm); // thumbnail bytes still come from the M6c/M02 file cache, unchanged
            }

            var previousSelected = Selected?.Address;
            Conversations.Clear();
            foreach (var row in convoRows)
            {
                if (JsonSerializer.Deserialize<CachedConversation>(row.PayloadJson) is not { } cached)
                {
                    continue;
                }
                var convo = new ConversationVm(cached.Address);
                foreach (var m in cached.Messages)
                {
                    convo.Messages.Add(new MessageVm(new SmsMessage(cached.Address, m.Body, m.Date, m.Incoming)));
                }
                convo.Snippet = cached.Messages.Count > 0 ? cached.Messages[^1].Body : "";
                Conversations.Add(convo);
            }
            Selected = Conversations.FirstOrDefault(c => c.Address == previousSelected) ?? Conversations.FirstOrDefault();

            RecentCalls.Clear();
            foreach (var row in callRows)
            {
                if (JsonSerializer.Deserialize<CachedCall>(row.PayloadJson) is not { } cached)
                {
                    continue;
                }
                RecentCalls.Add(new CallVm(new CallEntry(cached.Number, cached.Type, cached.Date, cached.Duration), _connection));
            }

            _restoredCacheTimestamps = [.. photoRows.Select(r => r.UpdatedUtc), .. convoRows.Select(r => r.UpdatedUtc), .. callRows.Select(r => r.UpdatedUtc)];

            OnPropertyChanged(nameof(HasConversations));
            OnPropertyChanged(nameof(NoConversations));
            OnPropertyChanged(nameof(HasCalls));
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasNoPhotos));
            OnPropertyChanged(nameof(ShowPhotosWidget));
        }
        catch (Exception ex)
        {
            // §2.7: never let a bad row reach the UI as a crash. Empty is the same fallback as no
            // store at all.
            _log.Log(LogLevel.Warn, $"Home: could not read the offline cache; showing empty. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
            Photos.Clear();
            Conversations.Clear();
            Selected = null;
            RecentCalls.Clear();
            _restoredCacheTimestamps = [];
        }

        RefreshOfflineBanner();
    }

    /// <summary>
    /// §2.1's write-through: upserts one row per item into sync_cache, then prunes to the 200-row
    /// cap (§2.9). Fire-and-forget from each Load*Async (§2.7) with its own catch-all — a cache
    /// write must never be able to break the live load it rides along with. Logs the kind and row
    /// count only, never a key or payload (§2.8 — a conversation's key is an address, a call's
    /// embeds a number).
    /// </summary>
    private async Task WriteThroughCacheAsync(string serial, string kind, IReadOnlyList<(string Key, string PayloadJson)> rows)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, payload) in rows)
            {
                await _store.UpsertSyncCacheRowAsync(serial, kind, key, payload, now);
            }
            await _store.PruneSyncCacheAsync(serial, kind, MaxCachedRowsPerKind);
            _log.Log(LogLevel.Info, $"Home: cached {rows.Count} {kind} row(s) for serial={serial}.");
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"Home: sync_cache write-through failed for kind={kind}; cache stays stale. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
        }
    }

    private async Task LoadMessagesAsync()
    {
        try
        {
            var messages = await _connection.SmsListAsync(60, CancellationToken.None);
            var previous = Selected?.Address;
            Conversations.Clear();
            var cacheRows = new List<(string Key, string PayloadJson)>();
            foreach (var group in messages.GroupBy(m => m.Address).OrderByDescending(g => g.Max(m => m.Date)))
            {
                var convo = new ConversationVm(group.Key);
                var ordered = group.OrderBy(m => m.Date).ToList();
                foreach (var m in ordered)
                {
                    convo.Messages.Add(new MessageVm(m));
                }
                convo.Snippet = group.OrderByDescending(m => m.Date).First().Body;
                Conversations.Add(convo);

                // §2.1 write-through: the WHOLE thread lives under one row keyed by the address,
                // so a re-load upserts the same key instead of growing one row per message.
                var cached = new CachedConversation(group.Key,
                    [.. ordered.Select(m => new CachedMessage(m.Body, m.Incoming, m.Date))]);
                cacheRows.Add((group.Key, JsonSerializer.Serialize(cached)));
            }
            Selected = Conversations.FirstOrDefault(c => c.Address == previous) ?? Conversations.FirstOrDefault();
            OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(NoConversations));

            if (_supervisor.Device?.Serial is { } serial)
            {
                _ = WriteThroughCacheAsync(serial, HomeCacheKinds.Conversation, cacheRows);
            }
        }
        catch (LincException)
        {
            // Permission missing on the phone; the widget stays empty (the Sync page explains it).
        }
    }

    private void OnSmsReceived(Protocol.Envelope envelope)
    {
        var address = (string?)envelope.Payload["address"];
        var body = (string?)envelope.Payload["body"];
        if (string.IsNullOrEmpty(address) || body is null)
        {
            return;
        }
        var message = new SmsMessage(address, body,
            (long?)envelope.Payload["date"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
        var convo = Conversations.FirstOrDefault(c => c.Address == address);
        if (convo is null)
        {
            convo = new ConversationVm(address);
            Conversations.Insert(0, convo);
            OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(NoConversations));
        }
        convo.Messages.Add(new MessageVm(message));
        convo.Snippet = body;
    }

    [RelayCommand]
    private async Task SendReplyAsync()
    {
        var body = ReplyText?.Trim() ?? "";
        if (Selected is null || body.Length == 0)
        {
            return;
        }
        try
        {
            await _connection.SmsSendAsync(Selected.Address, body, CancellationToken.None);
            Selected.Messages.Add(new MessageVm(new SmsMessage(Selected.Address, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false)));
            Selected.Snippet = body;
            ReplyText = "";
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    private async Task LoadCallsAsync()
    {
        try
        {
            var log = await _connection.CallLogAsync(30, CancellationToken.None);
            RecentCalls.Clear();
            var cacheRows = new List<(string Key, string PayloadJson)>();
            foreach (var c in log)
            {
                RecentCalls.Add(new CallVm(c, _connection));
                // §2.1 write-through: CallEntry carries no id, so Number+Date is the natural
                // per-event identity (see CachedCall's doc comment).
                var key = $"{c.Number}|{c.Date}";
                cacheRows.Add((key, JsonSerializer.Serialize(new CachedCall(c.Number, c.Type, c.Date, c.Duration))));
            }
            OnPropertyChanged(nameof(HasCalls));

            if (_supervisor.Device?.Serial is { } serial)
            {
                _ = WriteThroughCacheAsync(serial, HomeCacheKinds.Call, cacheRows);
            }
        }
        catch (LincException)
        {
            // Permission missing; the widget stays empty.
        }
    }

    private void OnCallIncoming(Protocol.Envelope envelope)
    {
        var ringing = (bool?)envelope.Payload["ringing"] ?? false;
        var number = (string?)envelope.Payload["number"];
        IncomingRinging = ringing;
        IncomingText = string.IsNullOrEmpty(number) ? "Incoming call" : $"Incoming call from {number}";
        if (!ringing && IsConnected)
        {
            _ = LoadCallsAsync(); // refresh the log so the just-ended call shows
        }
    }

    [RelayCommand]
    private async Task DialAsync()
    {
        var number = DialNumber?.Trim() ?? "";
        if (number.Length == 0)
        {
            return;
        }
        try
        {
            await _connection.CallDialAsync(number, CancellationToken.None);
            DialNumber = "";
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        try
        {
            await _connection.CallDeclineAsync(CancellationToken.None);
            IncomingRinging = false;
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    // ---- Companion events: share-to-PC (v10), SMS (v11), incoming call (v12) ----

    private void OnCompanionMessage(Protocol.Envelope envelope)
    {
        switch (envelope.Type)
        {
            case Protocol.MessageType.SmsReceived when MessagesOn:
                OnSmsReceived(envelope);
                return;
            case Protocol.MessageType.CallIncoming when CallsOn:
                OnCallIncoming(envelope);
                return;
            case Protocol.MessageType.ShareItem:
                break;
            default:
                return;
        }
        var kind = (string?)envelope.Payload["kind"];
        var text = (string?)envelope.Payload["text"];
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (kind == "url" && Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
            catch (Exception) { /* no default browser; the message below still shows it */ }
            QuickActionMessage = $"Opened a link from your phone: {text}";
        }
        else
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch (Exception) { /* clipboard busy */ }
            QuickActionMessage = "Copied text sent from your phone to this PC's clipboard.";
        }
        _log.Log(LogLevel.Info, "Received a shared item from the phone");
    }

    // ---- Phone preview card ----

    public bool IsConnected => _supervisor.State == LinkState.Connected;
    public bool IsDisconnected => !IsConnected;

    /// <summary>
    /// No phone has a visible tab — first run, or every tab was closed (M2b). Drives the Home
    /// empty-state "Pair a phone" call to action, the always-reachable door to the wizard that
    /// stands in for the hidden tab strip.
    /// </summary>
    public bool HasNoVisibleDevice => !_registry.KnownDevices.Any(d => !d.Hidden);

    /// <summary>
    /// The phone's name — never its connection state. M15b D2: the fallback used to be
    /// "No phone connected", which put a second link statement directly above
    /// <see cref="ConnectionCaption"/>, the one place that is supposed to own it. Naming the
    /// remembered phone is both non-duplicative and more useful: the card reads
    /// "Pixel 7 / Looking for your phone…" instead of "No phone connected / Looking for your phone…".
    /// </summary>
    public string ModelName => _supervisor.Device?.Model ?? _registry.PairedModel ?? "Phone";
    public string ConnectionCaption => _supervisor.State switch
    {
        LinkState.Connected => _supervisor.Device?.Transport switch
        {
            LinkTransport.AdbUsb => "Connected over USB",
            LinkTransport.DirectTls => "Connected directly — no ADB",
            _ => "Connected over Wi-Fi",
        },
        LinkState.Connecting => "Connecting…",
        LinkState.Searching => "Looking for your phone…",
        LinkState.Paused => "Disconnected — the link is paused",
        _ => "Pair a phone to get started",
    };

    /// <summary>
    /// The far end of the live link, when it is meaningful to say it: Direct TLS connects
    /// straight to the phone, so its peer address IS the phone's address. On an ADB link the
    /// socket is a loopback forward and naming 127.0.0.1 as the phone would be a lie (v18),
    /// so this stays null there — the card simply omits the line (Part C1).
    /// </summary>
    public string? LinkAddress =>
        _supervisor.State == LinkState.Connected &&
        _supervisor.Device?.Transport == LinkTransport.DirectTls &&
        _connection.PeerAddress is { } peer
            ? peer
            : null;

    public bool HasLinkAddress => LinkAddress is not null;

    [ObservableProperty]
    public partial string BatteryText { get; set; }

    [ObservableProperty]
    public partial string SignalText { get; set; }

    [ObservableProperty]
    public partial Windows.UI.Color GradientTop { get; set; }

    [ObservableProperty]
    public partial Windows.UI.Color GradientBottom { get; set; }

    private void RefreshGradient()
    {
        GradientTop = _theme.BrushColor("PrimaryContainerBrush") ?? Windows.UI.Color.FromArgb(255, 0xE6, 0xE6, 0xE6);
        GradientBottom = _theme.BrushColor("TertiaryContainerBrush") ?? Windows.UI.Color.FromArgb(255, 0xD9, 0xD9, 0xD9);
    }

    private void RefreshDeviceCard()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ConnectionCaption));
        OnPropertyChanged(nameof(LinkAddress));
        OnPropertyChanged(nameof(HasLinkAddress));
        OnPropertyChanged(nameof(QuickActionsEnabled));
        OnPropertyChanged(nameof(ScreenshotEnabled));
        OnPropertyChanged(nameof(ShowAdbHint));
        if (!IsConnected)
        {
            BatteryText = "—";
            SignalText = "—";
        }
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        BatteryText = status.Charging ? $"{status.Battery}% ⚡" : $"{status.Battery}%";
        SignalText = status.WifiSignalLevel is { } level
            ? $"Wi-Fi {SignalWord(level)}"
            : "Not on Wi-Fi";
        if (status.DndEnabled is { } dnd)
        {
            IsDndOn = dnd;
        }
        if (status.SoundMode is { } mode)
        {
            SoundMode = mode;
        }
    }

    // ---- Wallpaper backdrop (v8, D-021; owned by ThemeSyncService since M10) ----

    /// <summary>Tiny pre-blurred wallpaper thumbnail behind the widgets section.</summary>
    public BitmapImage? Wallpaper => _theme.Wallpaper;
    public bool HasWallpaper => _theme.Wallpaper is not null;

    // ---- Sound profile (v8) ----

    [ObservableProperty]
    public partial string SoundMode { get; set; }

    public bool IsSoundNormal => SoundMode == "normal";
    public bool IsSoundVibrate => SoundMode == "vibrate";
    public bool IsSoundSilent => SoundMode == "silent";

    partial void OnSoundModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSoundNormal));
        OnPropertyChanged(nameof(IsSoundVibrate));
        OnPropertyChanged(nameof(IsSoundSilent));
    }

    [RelayCommand]
    private async Task SetSoundAsync(string mode)
    {
        try
        {
            await _connection.SetSoundModeAsync(mode, CancellationToken.None);
            SoundMode = mode;
            QuickActionMessage = mode switch
            {
                "vibrate" => "Phone set to vibrate.",
                "silent" => "Phone silenced.",
                _ => "Phone sound is on.",
            };
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartMirrorAsync()
    {
        if (_supervisor.Device is not { } device)
        {
            return;
        }
        try
        {
            QuickActionMessage = "Opening the mirror…";
            await _mirror.StartAsync(device.Serial, $"Linc — {device.Model}", _registry.Mirror);
            QuickActionMessage = "";
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    private static string SignalWord(int level) => level switch
    {
        <= 0 => "very weak",
        1 => "weak",
        2 => "okay",
        3 => "good",
        _ => "excellent",
    };

    // ---- Quick actions ----

    /// <summary>Ring/DND need protocol v7 (an updated phone app); mirror/screenshot need ADB (until M17).</summary>
    public bool QuickActionsEnabled => IsConnected && _supervisor.Device is { Companion.V: >= 7 };
    public bool ScreenshotEnabled => IsConnected && _connection.HasAdb;

    /// <summary>On a direct (no-ADB) link, tell the user what turning debugging on would unlock.</summary>
    public bool ShowAdbHint => IsConnected && !_connection.HasAdb;
    public string AdbHintText =>
        "Connected directly — files, photos and everything here work. For screen " +
        "mirroring and screenshots, also turn on USB or wireless debugging.";

    [ObservableProperty]
    public partial string QuickActionMessage { get; set; }

    public bool HasQuickActionMessage => QuickActionMessage.Length > 0;

    partial void OnQuickActionMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasQuickActionMessage));

    [ObservableProperty]
    public partial bool IsDndOn { get; set; }

    [RelayCommand]
    private async Task RingPhoneAsync()
    {
        try
        {
            await _connection.LocateDeviceAsync(CancellationToken.None);
            QuickActionMessage = "Ringing — press again to stop.";
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleDndAsync()
    {
        var target = !IsDndOn;
        try
        {
            await _connection.SetDndAsync(target, CancellationToken.None);
            IsDndOn = target;
            QuickActionMessage = target ? "Do Not Disturb is on." : "Do Not Disturb is off.";
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message; // includes the grant guidance from the phone
        }
    }

    [RelayCommand]
    private async Task PullScreenshotAsync()
    {
        try
        {
            QuickActionMessage = "Taking a screenshot…";
            var path = await _files.PullScreenshotAsync(CancellationToken.None);
            QuickActionMessage = $"Saved to {path}";
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (LincException ex)
        {
            QuickActionMessage = ex.Message;
        }
    }

    // ---- Media widget ----

    public bool HasMedia => _media.Current is not null;
    public string MediaTitle => _media.Current?.Title ?? "Nothing playing";
    public string MediaSubtitle
    {
        get
        {
            var state = _media.Current;
            if (state is null)
            {
                return "";
            }
            return state.Artist is { Length: > 0 } artist && state.Album is { Length: > 0 } album
                ? $"{artist} · {album}"
                : state.Artist ?? state.Album ?? "";
        }
    }
    public string PlayPauseGlyph => _media.Current?.Playing == true ? "" : "";

    public bool HasSeek => _media.Current?.DurationMs is > 0;

    [ObservableProperty]
    public partial double MediaPositionMs { get; set; }

    [ObservableProperty]
    public partial double MediaDurationMs { get; set; }

    public string MediaTimeText =>
        $"{FormatMs((long)MediaPositionMs)} / {FormatMs((long)MediaDurationMs)}";

    [ObservableProperty]
    public partial BitmapImage? MediaArt { get; set; }

    public bool HasMediaArt => MediaArt is not null;
    public bool HasNoMediaArt => MediaArt is null;

    partial void OnMediaArtChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(HasMediaArt));
        OnPropertyChanged(nameof(HasNoMediaArt));
    }

    [RelayCommand]
    private Task PlayPauseAsync() =>
        _media.ControlAsync(_media.Current?.Playing == true ? "pause" : "play");

    [RelayCommand]
    private Task NextTrackAsync() => _media.ControlAsync("next");

    [RelayCommand]
    private Task PreviousTrackAsync() => _media.ControlAsync("prev");

    /// <summary>Called by the page when the user releases the seek thumb.</summary>
    public async void SeekTo(double positionMs)
    {
        _seekDragging = false;
        _basePositionMs = (long)positionMs;
        _basePositionAtTick = Environment.TickCount64;
        await _media.ControlAsync("seek", (long)positionMs);
    }

    /// <summary>Called by the page when the user grabs the seek thumb.</summary>
    public void BeginSeek() => _seekDragging = true;

    private void RefreshMedia()
    {
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(ShowMediaWidget));
        OnPropertyChanged(nameof(MediaTitle));
        OnPropertyChanged(nameof(MediaSubtitle));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(HasSeek));

        var state = _media.Current;

        // Remember the metadata only — never the album art, and never anything sensitive (D-032).
        if (_supervisor.Device?.Serial is { } serial)
        {
            _cache.SaveMedia(serial, state is null
                ? null
                : new CachedMedia(state.Title, state.Artist, state.Playing, state.AppPackage));
        }

        MediaDurationMs = state?.DurationMs ?? 0;
        _basePositionMs = state?.PositionMs ?? 0;
        _basePositionAtTick = Environment.TickCount64;
        if (!_seekDragging)
        {
            MediaPositionMs = Math.Min(_basePositionMs, (long)MediaDurationMs);
            OnPropertyChanged(nameof(MediaTimeText));
        }

        if (state?.Playing == true)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }

        var artId = state?.ArtId;
        if (artId != _artId)
        {
            _artId = artId;
            MediaArt = null;
            if (artId is not null)
            {
                _ = LoadMediaArtAsync(artId);
            }
        }
    }

    private void TickPosition()
    {
        if (_seekDragging || MediaDurationMs <= 0)
        {
            return;
        }
        var estimated = _basePositionMs + (Environment.TickCount64 - _basePositionAtTick);
        MediaPositionMs = Math.Min(estimated, MediaDurationMs);
        OnPropertyChanged(nameof(MediaTimeText));
    }

    private static string FormatMs(long ms)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private async Task LoadMediaArtAsync(string artId)
    {
        var image = await FetchImageAsync("albumArt", artId);
        if (_artId == artId) // still the current track
        {
            MediaArt = image;
        }
    }

    // ---- Clipboard history ----

    public bool HasClips => Clips.Count > 0;
    public bool HasNoClips => Clips.Count == 0;

    private void RebuildClips()
    {
        Clips.Clear();
        foreach (var entry in _clipboard.History)
        {
            Clips.Add(new ClipVm(entry, _clipboard, _connection));
        }
        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(HasNoClips));
    }

    // ---- Notification shade ----

    public string HeaderText => Items.Count == 0
        ? "Notifications"
        : $"Notifications ({Items.Count})";

    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => Items.Count == 0;

    private void Rebuild()
    {
        Items.Clear();
        foreach (var item in _service.Items)
        {
            var vm = new NotificationVm(item, _service);
            if (item.AppPackage is { } package)
            {
                if (_appIcons.TryGetValue(package, out var cached))
                {
                    vm.Icon = cached;
                }
                else if (_iconFetchesInFlight.Add(package))
                {
                    _ = LoadAppIconAsync(package);
                }
            }
            Items.Add(vm);
        }
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    private async Task LoadAppIconAsync(string package)
    {
        var image = await FetchImageAsync("appIcon", package);
        _appIcons[package] = image;
        _iconFetchesInFlight.Remove(package);
        if (image is not null)
        {
            foreach (var vm in Items.Where(vm => vm.AppPackage == package))
            {
                vm.Icon = image;
            }
        }
    }

    /// <summary>Fetches PNG bytes over the bulk channel and decodes them; null on any failure.</summary>
    private static async Task<BitmapImage> ToImageAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private async Task<BitmapImage?> FetchImageAsync(string kind, string id)
    {
        try
        {
            var bytes = await _connection.FetchBulkAsync(kind, id, CancellationToken.None);
            if (bytes is not { Length: > 0 })
            {
                return null;
            }
            return await ToImageAsync(bytes);
        }
        catch (Exception)
        {
            return null; // cosmetic only — never let an icon fetch surface an error
        }
    }
}
