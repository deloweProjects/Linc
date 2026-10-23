using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Cross-cutting shell state: tray text and the link notice (<see cref="LinkNotice"/>) that Home's
/// phone card and the Device page's header show. Also responsible for eagerly starting the background
/// sync services (clipboard, notifications) at app launch — these must run
/// regardless of which page the user has open, so they can't wait for their
/// page to be navigated to.
/// </summary>
public partial class AppShellViewModel : ObservableObject
{
    private readonly IConnectionSupervisor _supervisor;
    private readonly IOnboardingState _onboardingState;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDeviceRegistry _registry;
    private readonly UpdateCheckService _updates;

    public AppShellViewModel(
        IConnectionSupervisor supervisor,
        IClipboardSyncService clipboardSync,
        IBatteryAlertService batteryAlerts,
        ISyncEngine syncEngine,
        IPcMediaService pcMedia,
        IPcMirrorService pcMirror,
        IPcControlService pcControl,
        IHotspotLinkService hotspotLink,
        IShareService share,
        Linc.Desktop.QuickShare.IQuickShareService quickShare,
        IDeviceCacheService cache,
        OutboxService outbox,
        HomeViewModel home,
        IOnboardingState onboardingState,
        IPhoneSetupService phoneSetup,
        IDeviceRegistry registry,
        UpdateCheckService updates,
        PcToolsService pcTools,
        SyncRemote syncRemote)
    {
        // Reverse mirror (M05): start answering the phone's pc.mirror.* / pc.displays.get
        // requests now, so the phone can view the PC without the Device page being open
        // (same reasoning as the other push services here — see below).
        pcMirror.Attach();
        // Quick controls (v17, M4b): same reasoning — the phone's Tools page must be able to
        // lock/sleep/volume/brightness this PC without the Device page being open.
        pcControl.Attach();
        pcTools.Attach(); // v27 (D-097): the phone's PC tools — look, files, terminal
        syncRemote.Attach(); // v30: the phone's Sync page — lanes and folder pairs
        // Hotspot link-up (v18, M13b): adb.announce is unsolicited and arrives as soon as the
        // phone's control connection comes up, so this has to be listening from launch — the
        // same reasoning as the two above, and more so, since nobody navigates to it at all.
        hotspotLink.Attach();
        // Quick Share: nearby phones (with or without Linc) find this PC from launch, like
        // Google's own Windows app — nobody should have to open a page to become visible.
        quickShare.Start();

        // Keep the offline copy current so a disconnect never blanks the UI (M02, D-032).
        supervisor.StatusUpdated += status =>
        {
            if (supervisor.Device is { } device)
            {
                cache.Save(device.Serial, device.Model, status);
            }
        };

        _supervisor = supervisor;
        _onboardingState = onboardingState;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // These must start now, not on first page visit — notifications keep
        // relaying/toasting, clipboard keeps syncing, and battery alerts keep
        // watching even if the user never opens the Home page. HomeViewModel's
        // own constructor already calls its services' Start(); injecting it here
        // just forces DI to construct it eagerly (it's a singleton).
        // The connection itself must start at launch, not when a page happens to be opened.
        // This used to live in DeviceViewModel's constructor, so after Home became the landing
        // page (M14) a fresh launch never connected at all until the user clicked Device — which
        // looked like "the phone keeps disconnecting" and starved every push feature.
        supervisor.Start();

        clipboardSync.Start();
        batteryAlerts.Start();
        // M9c (§2.9): the outbox flush must run on a reconnect regardless of the open page, so
        // it belongs here beside the other eagerly-started services, never on the Sync page VM
        // (the supervisor once lived in the Device page and the app never connected unless you
        // opened it — same trap). Start() subscribes to StateChanged; the flush is fire-and-forget
        // and never blocks or throws into the UI (§2.10).
        outbox.Start();
        // Folder/Photos sync must reconcile regardless of the open page too; its
        // constructor already wired triggers and kicked a first pass — injecting it
        // just forces eager construction (M18c, D-027).
        _ = syncEngine;
        // PC → phone pushes must run regardless of the open page too (M19): the phone's
        // Home widget wants media state, and shared files can arrive at any time.
        pcMedia.Start();
        share.Start();
        _ = home;

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(OnLinkStateChanged);
        _supervisor.ErrorRaised += message => _dispatcher.TryEnqueue(() => OnLinkMessage(message));
        // A status snapshot is the link proving it works: an older complaint is spent.
        _supervisor.StatusUpdated += _ => _dispatcher.TryEnqueue(OnHealthyStatus);
        _lostTimer = _dispatcher.CreateTimer();
        _lostTimer.Interval = LostGrace;
        _lostTimer.IsRepeating = false;
        _lostTimer.Tick += (_, _) => ShowPendingLost();

        // M12c-amend Part C: relay the non-wizard install-confirmation request to the UI thread —
        // it fires from PhoneSetupService's connect path (a background task), never the UI thread
        // (C2.5). MainWindow owns actually showing the ContentDialog (the house pattern lives in
        // page/window code-behind, matching SettingsPage's "Clear history" gate).
        phoneSetup.InstallConfirmationNeeded += device =>
            _dispatcher.TryEnqueue(() => InstallConfirmationRequested?.Invoke(device));

        _registry = registry;
        _updates = updates;
        // M17b B3: the update check runs AFTER the UI is up and never blocks it — fire-and-forget
        // with its own try/catch inside CheckAsync, so a dead backend cannot delay or break launch.
        // It belongs here rather than on a page for the usual reason (GUARDRAILS): a page view model
        // is created lazily and an update the user must take would never be offered.
        _ = CheckForUpdatesAsync();

        // M19 C3: a "Check now" pressed on the Settings page runs through the same service, so the
        // offer has to arrive here to be shown - the card lives on the shell, not on a page.
        updates.Checked += action => _dispatcher.TryEnqueue(() =>
        {
            _testOffer = null;
            UpdateAction = action;
            UpdateVersion = _updates.Latest?.Latest;
            UpdateNotes = _updates.Latest?.Notes;
            if (action == UpdateAction.None) _ = CheckTestFeedAsync(); // a test build: its own feed too
        });
        if (BuildFlavor.IsTest) _ = Task.Run(() => TestInstall.Prune(registry.RootPath));
    }

    // ---- M17b B4: what the user sees ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptionalUpdate))]
    [NotifyPropertyChangedFor(nameof(IsUpdateBlocking))]
    public partial UpdateAction UpdateAction { get; set; }

    [ObservableProperty]
    public partial string? UpdateNotes { get; set; }

    [ObservableProperty]
    public partial string? UpdateVersion { get; set; }

    /// <summary>A dismissible card. Bound to Visibility as a BOOL — never bind a string to
    /// Visibility (E_INVALIDARG, the M2b crash).</summary>
    public bool HasOptionalUpdate => UpdateAction == UpdateAction.Optional;

    /// <summary>
    /// A blocking overlay. It blocks the app's FUNCTION, not the process: the window still closes,
    /// the tray icon still works, and nothing traps the user (B4).
    /// </summary>
    public bool IsUpdateBlocking => UpdateAction == UpdateAction.Forced;

    /// <summary>Set when a download was refused — e.g. the checksum did not match. Plain language only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateProblem))]
    public partial string? UpdateProblem { get; set; }

    public bool HasUpdateProblem => !string.IsNullOrWhiteSpace(UpdateProblem);

    /// <summary>The newer test build on offer (test builds only), or null.</summary>
    private TestManifest? _testOffer;

    private static readonly HttpClient TestFeedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private async Task CheckForUpdatesAsync()
    {
        // Everything below the gate inside CheckAsync is skipped when checking is off, so with
        // the feature off this call costs one delegate read and returns None (M19 C3).
        var action = await _updates.CheckAsync().ConfigureAwait(false);
        if (action == UpdateAction.None)
        {
            // A test build also follows the test feed: a newer test build is offered the same way,
            // and a real release (above) still wins when there is one.
            await CheckTestFeedAsync().ConfigureAwait(false);
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            UpdateAction = action;
            UpdateVersion = _updates.Latest?.Latest;
            UpdateNotes = _updates.Latest?.Notes;
        });
    }

    /// <summary>Test builds only: a newer build on the test feed becomes the update card's offer.</summary>
    private async Task CheckTestFeedAsync()
    {
        if (!BuildFlavor.IsTest || !_registry.UpdatesEnabled)
        {
            return;
        }
        TestManifest? feed;
        try
        {
            feed = TestManifest.TryParse(await TestFeedHttp.GetStringAsync(TestChannel.FeedUrl).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return; // offline or no feed yet: nothing to offer
        }
        if (TestChannel.Offer(feed, BuildFlavor.TestBuild, _registry.SkippedUpdateVersion) is not { } offer)
        {
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            _testOffer = offer;
            UpdateAction = UpdateAction.Optional;
            UpdateVersion = TestChannel.Label(offer.Build, offer.Name);
            UpdateNotes = string.IsNullOrWhiteSpace(offer.Notes) ? "A newer test build of Linc." : offer.Notes;
        });
    }

    /// <summary>B4: records THIS version only, so the next release asks again.</summary>
    [RelayCommand]
    private void SkipUpdate()
    {
        _registry.SaveSkippedUpdateVersion(UpdateVersion);
        _testOffer = null;
        UpdateAction = UpdateAction.None;
    }

    /// <summary>
    /// M17b C: download, verify the manifest's SHA256, then hand the installer to Windows and exit.
    /// A hash mismatch refuses and says so — an unverified binary is never launched.
    /// </summary>
    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (_testOffer is { } test)
        {
            await ApplyTestBuildAsync(test).ConfigureAwait(false);
            return;
        }
        if (_updates.Latest is not { } manifest)
        {
            return;
        }

        UpdateProblem = null;
        var (ok, path, message) = await _updates
            .DownloadAndVerifyAsync(manifest, Path.GetTempPath()).ConfigureAwait(false);
        if (!ok || path is null)
        {
            _dispatcher.TryEnqueue(() => UpdateProblem = message);
            return;
        }

        LaunchInstaller(path);
    }

    /// <summary>A test build: download, check the hash, unzip beside the others, start it, exit.</summary>
    private async Task ApplyTestBuildAsync(TestManifest test)
    {
        _dispatcher.TryEnqueue(() => UpdateProblem = null);
        var asManifest = new UpdateManifest($"test-{test.Build}", null, test.Notes, test.Desktop, null);
        var (ok, path, message) = await _updates.DownloadAndVerifyAsync(asManifest, Path.GetTempPath()).ConfigureAwait(false);
        if (!ok || path is null)
        {
            _dispatcher.TryEnqueue(() => UpdateProblem = message);
            return;
        }
        var (exe, problem) = await Task.Run(() => TestInstall.Unpack(path, _registry.RootPath, test.Build)).ConfigureAwait(false);
        if (exe is null)
        {
            _dispatcher.TryEnqueue(() => UpdateProblem = problem);
            return;
        }
        var log = App.Services.GetRequiredService<ILogService>();
        log.Log(LogLevel.Info, $"Updating to {TestChannel.Label(test.Build, test.Name)} from {BuildFlavor.TestLabel}.");
        Task.Run(() => PcVoiceAccess.RestoreAllMics(log)).Wait(TimeSpan.FromSeconds(3));
        Task.Run(() => PcTabletMode.RestorePending(log)).Wait(TimeSpan.FromSeconds(3));
        Task.Run(() => App.Services.GetRequiredService<IMirrorService>().Dispose()).Wait(TimeSpan.FromSeconds(8));
        TestInstall.Start(exe);
        Environment.Exit(0);
    }

    /// <summary>
    /// Starts the verified installer and closes Linc so the file it is replacing is not locked.
    /// Separate from the verify step so nothing can reach it without a matching checksum.
    /// </summary>
    private void LaunchInstaller(string verifiedPath)
    {
        if (verifiedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // 1.0.0.11 (D-085): the Inno Setup installer, run silently. It closes Linc, upgrades in
            // place (keeping %LOCALAPPDATA%\Linc) and starts it again. Put the PC's microphone and
            // display scale back first, as Exit does.
            var log = App.Services.GetRequiredService<ILogService>();
            Task.Run(() => PcVoiceAccess.RestoreAllMics(log)).Wait(TimeSpan.FromSeconds(3));
            Task.Run(() => PcTabletMode.RestorePending(log)).Wait(TimeSpan.FromSeconds(3));
            Task.Run(() => App.Services.GetRequiredService<IMirrorService>().Dispose()).Wait(TimeSpan.FromSeconds(8));
            Process.Start(new ProcessStartInfo(verifiedPath,
                "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART")
            { UseShellExecute = true });
            Environment.Exit(0);
            return;
        }
        // A portable zip can't install itself: show it in Explorer to unzip over the old folder.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{verifiedPath}\"") { UseShellExecute = true });
        _dispatcher.TryEnqueue(() => UpdateProblem = "The update is downloaded. Close Linc, unzip it over this folder, and start it again.");
    }

    /// <summary>The last link message as the supervisor said it. Nothing shows this as a banner
    /// any more; <see cref="LinkNotice"/> is what the pages show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    // ---- The link notice (owner concerns 2026-10-19, items 7 and 9) ----
    //
    // The full-width red banner is gone: every link message (lost, can't reach the companion app,
    // the transport changed…) is one quiet line in the phone's own card on Home and under the
    // phone's name on the Device page, with the full text as a tooltip. Two rules keep it honest:
    // a lost connection is only said once the link has stayed down for LostGrace (most drops
    // heal within a couple of seconds), and anything said is cleared the moment the link is
    // Connected, or by the next status snapshot once it has been up for NoticeMinimum (the owner
    // saw "Linc can't reach the companion app…" while connected for 5 m 32 s).

    /// <summary>How long a drop must last before "Lost the connection" is shown.</summary>
    private static readonly TimeSpan LostGrace = TimeSpan.FromSeconds(5);

    /// <summary>A notice raised on a live link stays at least this long before a status clears it.</summary>
    private static readonly TimeSpan NoticeMinimum = TimeSpan.FromSeconds(8);

    private readonly DispatcherQueueTimer _lostTimer;
    private string? _pendingLost;
    private DateTimeOffset _noticeAtUtc;

    /// <summary>The full link message, or null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkNotice), nameof(LinkNoticeText), nameof(LinkNoticeGlyph))]
    public partial string? LinkNotice { get; set; }

    public bool HasLinkNotice => LinkNotice is not null;

    /// <summary>The notice as one short line; the full text is the tooltip.</summary>
    public string LinkNoticeText => Shorten(LinkNotice);

    /// <summary>Warning for a lost link, info for the rest.</summary>
    public string LinkNoticeGlyph => LinkNotice is { } m && IsLostMessage(m) ? "\uE7BA" : "\uE946";

    private static bool IsLostMessage(string message) =>
        message.StartsWith("Lost the connection", StringComparison.OrdinalIgnoreCase);

    private void OnLinkMessage(string message)
    {
        ErrorMessage = message;
        if (IsLostMessage(message))
        {
            // Said only if the link is still down LostGrace from now.
            _pendingLost = message;
            _lostTimer.Stop();
            _lostTimer.Start();
            return;
        }
        LinkNotice = message;
        _noticeAtUtc = DateTimeOffset.UtcNow;
    }

    private void ShowPendingLost()
    {
        if (_pendingLost is { } message && !IsConnected)
        {
            LinkNotice = message;
            _noticeAtUtc = DateTimeOffset.UtcNow;
        }
        _pendingLost = null;
    }

    private void OnHealthyStatus()
    {
        if (IsConnected && LinkNotice is not null && DateTimeOffset.UtcNow - _noticeAtUtc >= NoticeMinimum)
        {
            ClearLinkNotice();
        }
    }

    private void ClearLinkNotice()
    {
        _pendingLost = null;
        _lostTimer.Stop();
        LinkNotice = null;
        ErrorMessage = null;
    }

    private static string Shorten(string? message)
    {
        if (message is null) return "";
        if (IsLostMessage(message)) return "Connection lost — Linc reconnects by itself";
        if (message.Contains("reach the companion app", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("reach the Linc app", StringComparison.OrdinalIgnoreCase))
        {
            return "Can't reach the Linc app on the phone — open Linc there";
        }
        // Otherwise the first sentence, kept to one line's worth.
        var cut = message.IndexOf(". ", StringComparison.Ordinal);
        var line = cut > 0 ? message[..(cut + 1)] : message;
        return line.Length > 96 ? line[..95].TrimEnd() + "…" : line;
    }

    [ObservableProperty]
    public partial bool IsOnboarding { get; set; }

    /// <summary>Mirrors onto <see cref="IOnboardingState"/> so PhoneSetupService's install gate
    /// can read "is the wizard open" without depending on this (or any) ViewModel.</summary>
    partial void OnIsOnboardingChanged(bool value) => _onboardingState.IsActive = value;

    /// <summary>Fired (on the UI thread) when a non-wizard connect wants to ask before installing
    /// the companion on a phone. MainWindow shows the confirmation dialog.</summary>
    public event Action<AdvancedSharpAdbClient.Models.DeviceData>? InstallConfirmationRequested;

    public bool HasError => ErrorMessage is not null;
    public bool IsConnected => _supervisor.State == LinkState.Connected;

    /// <summary>Raised on the UI thread with whether a phone is connected, for the tray icon.</summary>
    public event Action<bool>? TrayStateChanged;

    public string TrayText => _supervisor.State == LinkState.Connected
        ? $"{BuildFlavor.AppName} — connected to {_supervisor.Device?.Model}"
        : $"{BuildFlavor.AppName} — {StateWord}";

    /// <summary>The tray menu's phone line: "Pixel 7 — connected" (a label, not a command).</summary>
    public string TrayPhoneText => _supervisor.State == LinkState.Connected
        ? $"{_supervisor.Device?.Model ?? "Phone"} — connected"
        : $"{_registry.PairedModel ?? "No phone"} — {StateWord}";

    public bool IsPaused => _supervisor.State == LinkState.Paused;

    public string PauseText => IsPaused ? "Resume connection" : "Pause connection";

    /// <summary>The tray's Pause/Resume: pausing stops reconnecting until resumed.</summary>
    public void TogglePause()
    {
        if (IsPaused)
        {
            _supervisor.ResumeAutomatic();
        }
        else
        {
            _supervisor.RequestDisconnect();
        }
    }

    private string StateWord => _supervisor.State switch
    {
        LinkState.Connecting => "connecting",
        LinkState.Searching => "searching",
        LinkState.Paused => "paused",
        _ => "not connected",
    };

    private void OnLinkStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(TrayText));
        OnPropertyChanged(nameof(TrayPhoneText));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseText));
        TrayStateChanged?.Invoke(IsConnected);
        if (IsConnected)
        {
            ClearLinkNotice(); // back within the grace: the drop is never mentioned
        }
    }
}
