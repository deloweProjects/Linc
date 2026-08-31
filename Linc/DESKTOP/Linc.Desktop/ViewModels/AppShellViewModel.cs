using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Cross-cutting shell state: tray text and the global error banner shown above
/// whichever page is active. Also responsible for eagerly starting the background
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
        IDeviceCacheService cache,
        OutboxService outbox,
        HomeViewModel home,
        IOnboardingState onboardingState,
        IPhoneSetupService phoneSetup,
        IDeviceRegistry registry,
        UpdateCheckService updates)
    {
        // Reverse mirror (M05): start answering the phone's pc.mirror.* / pc.displays.get
        // requests now, so the phone can view the PC without the Device page being open
        // (same reasoning as the other push services here — see below).
        pcMirror.Attach();
        // Quick controls (v17, M4b): same reasoning — the phone's Tools page must be able to
        // lock/sleep/volume/brightness this PC without the Device page being open.
        pcControl.Attach();
        // Hotspot link-up (v18, M13b): adb.announce is unsolicited and arrives as soon as the
        // phone's control connection comes up, so this has to be listening from launch — the
        // same reasoning as the two above, and more so, since nobody navigates to it at all.
        hotspotLink.Attach();

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
        _supervisor.ErrorRaised += message => _dispatcher.TryEnqueue(() => ErrorMessage = message);

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
            UpdateAction = action;
            UpdateVersion = _updates.Latest?.Latest;
            UpdateNotes = _updates.Latest?.Notes;
        });
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

    private async Task CheckForUpdatesAsync()
    {
        // Everything below the gate inside CheckAsync is skipped when checking is off, so with
        // the feature off this call costs one delegate read and returns None (M19 C3).
        var action = await _updates.CheckAsync().ConfigureAwait(false);
        if (action == UpdateAction.None)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            UpdateAction = action;
            UpdateVersion = _updates.Latest?.Latest;
            UpdateNotes = _updates.Latest?.Notes;
        });
    }

    /// <summary>B4: records THIS version only, so the next release asks again.</summary>
    [RelayCommand]
    private void SkipUpdate()
    {
        _registry.SaveSkippedUpdateVersion(UpdateVersion);
        UpdateAction = UpdateAction.None;
    }

    /// <summary>
    /// M17b C: download, verify the manifest's SHA256, then hand the installer to Windows and exit.
    /// A hash mismatch refuses and says so — an unverified binary is never launched.
    /// </summary>
    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
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

    /// <summary>
    /// Starts the verified installer and closes Linc so the file it is replacing is not locked.
    /// Separate from the verify step so nothing can reach it without a matching checksum.
    /// </summary>
    private static void LaunchInstaller(string verifiedPath)
    {
        Process.Start(new ProcessStartInfo(verifiedPath) { UseShellExecute = true });
        Environment.Exit(0);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

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

    public string TrayText => _supervisor.State == LinkState.Connected
        ? $"Linc — connected to {_supervisor.Device?.Model}"
        : $"Linc — {StateWord}";

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
        if (IsConnected)
        {
            ErrorMessage = null;
        }
    }
}
