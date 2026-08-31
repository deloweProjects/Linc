using System.IO;
using System.Text.Json;

namespace Linc.Desktop.Services;

/// <summary>
/// How the user wants a phone connected. <see cref="Auto"/> lets the supervisor pick the best
/// available transport (USB &gt; wireless ADB &gt; Direct TLS) and switch as better ones appear;
/// the others pin it to one transport. Per device (M03), default <see cref="Auto"/>.
/// </summary>
public enum ConnectionPreference { Auto, UsbOnly, WirelessOnly, DirectOnly }

public interface IDeviceRegistry
{
    /// <summary>
    /// The Linc store directory this registry was built against. Any other store under it — the
    /// per-device <c>cache\</c>, and anything added later — <b>must</b> take its root from here so
    /// a harness on a temp root cannot reach the owner's real data (D-057/D-058).
    /// </summary>
    string RootPath { get; }

    string? LastHostPort { get; }

    /// <summary>Hardware serial (ro.serialno) of the paired phone; matches mDNS instance names.</summary>
    string? PairedSerial { get; }

    string? PairedModel { get; }
    bool ClipboardSyncEnabled { get; }
    bool NotificationSyncEnabled { get; }
    bool AllowUsbConnections { get; }

    /// <summary>The paired phone's pinned TLS certificate, base64 DER (v9, D-022).</summary>
    string? PhoneCertBase64 { get; }

    /// <summary>Standing presence: advertise + listen for direct connections (default on).</summary>
    bool BackgroundConnectionEnabled { get; }

    /// <summary>
    /// Persistent notification history is ON — opting in to storing notification bodies on disk
    /// (D-045, M9b). <b>Default OFF</b>, on every fresh install, new device, and upgraded install
    /// with no stored preference (§2.1). Turning it on is the user consenting; turning it off
    /// stops new rows but does NOT clear existing ones (§2.5 — Clear is a separate action).
    /// App-wide, not per-device, so it belongs here not on <see cref="KnownDevice"/>.
    /// </summary>
    bool NotificationHistoryEnabled { get; }

    /// <summary>
    /// How long stored notification rows live: 7 / 30 / 90 days. Default <b>30</b> when the
    /// feature is first enabled (§2.3). Older rows are pruned at startup and whenever this is
    /// shortened. Stored here (app-wide) rather than per device, like
    /// <see cref="NotificationHistoryEnabled"/>.
    /// </summary>
    int NotificationHistoryRetentionDays { get; }

    /// <summary>The active phone's preferred transport (default <see cref="ConnectionPreference.Auto"/>).</summary>
    ConnectionPreference ConnectionPreference { get; }

    /// <summary>
    /// Opt-in "start Linc when I sign in" (M12e). <b>Default OFF</b>, on every fresh install and
    /// upgraded install with no stored key, same shape as <see cref="NotificationHistoryEnabled"/>.
    /// App-wide, not per-device. The actual HKCU Run key write lives in
    /// <see cref="StartupRegistration"/>, never here (D-036/D-057 harness decoupling).
    /// </summary>
    bool StartWithWindows { get; }

    /// <summary>
    /// M19 C3: "Keep Linc up to date". **Default ON.** Off means the update path cannot execute at
    /// all — no request, no UI, no log line — and the app only says where to update by hand. The
    /// manifest address is no longer a setting: it is <see cref="UpdateChannel.ManifestUrl"/>,
    /// baked into the build. App-wide, not per-device.
    /// </summary>
    bool UpdatesEnabled { get; }

    /// <summary>M17b B4: the version the user chose to skip, or null. Applies to that version only.</summary>
    string? SkippedUpdateVersion { get; }

    /// <summary>
    /// M18 B1: "Use my phone's colours". **Default ON** where the data exists. When off — or when
    /// the phone is disconnected, or has sent no wallpaper — the app uses M14's neutral ramp, which
    /// is the base state it already ships with rather than a fallback bolted on.
    /// </summary>
    bool UsePhoneColours { get; }

    void SaveConnectionPreference(ConnectionPreference preference);

    /// <summary>Persists <see cref="StartWithWindows"/>. Does not touch the registry — the caller (the Settings view model) also calls <see cref="StartupRegistration"/>.</summary>
    void SaveStartWithWindows(bool enabled);

    /// <summary>Persists <see cref="UpdatesEnabled"/> (M19 C3).</summary>
    void SaveUpdatesEnabled(bool enabled);

    /// <summary>Persists <see cref="SkippedUpdateVersion"/> (M17b B4). Records that ONE version.</summary>
    void SaveSkippedUpdateVersion(string? version);

    /// <summary>Persists <see cref="UsePhoneColours"/> (M18 B1).</summary>
    void SaveUsePhoneColours(bool enabled);

    /// <summary>Raised when <see cref="UsePhoneColours"/> changes, so the theme can re-derive at once.</summary>
    event Action? UsePhoneColoursChanged;

    /// <summary>Raised when the active phone's <see cref="ConnectionPreference"/> changes.</summary>
    event Action? ConnectionPreferenceChanged;

    /// <summary>Sync-page lane toggles (v11), keyed folders/photos/messages/calls.</summary>
    bool SyncLane(string lane);
    void SaveSyncLane(string lane, bool enabled);

    /// <summary>Raised when any sync lane toggles, so other surfaces (Home) can refresh.</summary>
    event Action? SyncLanesChanged;

    /// <summary>Folders-lane pair (M18c, D-027): the PC folder synced against <see cref="FolderSyncPhonePath"/>.</summary>
    string? FolderSyncPcPath { get; }

    /// <summary>Folders-lane phone path (M18c). Defaults to <c>/sdcard/Download</c>.</summary>
    string FolderSyncPhonePath { get; }

    void SaveFolderSyncPaths(string? pcPath, string phonePath);

    void SaveLastHostPort(string hostPort);
    void SavePairedDevice(string serial, string model);
    void SaveClipboardSyncEnabled(bool enabled);
    void SaveNotificationSyncEnabled(bool enabled);
    void SaveAllowUsbConnections(bool enabled);
    void SavePhoneCert(string certBase64);
    void SaveBackgroundConnectionEnabled(bool enabled);

    /// <summary>
    /// Persists <see cref="NotificationHistoryEnabled"/>. Turning the feature OFF does not delete
    /// stored rows (§2.5); the caller (the Settings page) is responsible for the "Clear history"
    /// action via <see cref="LincStore.DeleteAllNotificationsAsync"/>.
    /// </summary>
    void SaveNotificationHistoryEnabled(bool enabled);

    /// <summary>
    /// Persists <see cref="NotificationHistoryRetentionDays"/>. The SHORTEN-to-prune behavior
    /// (§2.3 — the user shortening the window expects old data gone now) is wired at the call
    /// site (the Settings page), which invokes <see cref="LincStore.PruneNotificationsAsync"/>
    /// with the new shorter window if it is smaller than the old one. This method just stores.
    /// </summary>
    void SaveNotificationHistoryRetentionDays(int days);

    /// <summary>Per-device Desktop Mode settings (M08a).</summary>
    DesktopModeSettings DesktopMode { get; }
    event Action? DesktopModeChanged;
    void SaveDesktopMode(DesktopModeSettings settings);

    /// <summary>Per-device screen-mirror settings (M5b).</summary>
    MirrorSettings Mirror { get; }
    event Action? MirrorChanged;
    void SaveMirror(MirrorSettings settings);

    /// <summary>Per-device Home sections layout (M6a).</summary>
    HomeLayout Home { get; }
    event Action? HomeChanged;
    void SaveHome(HomeLayout layout);

    /// <summary>Forgets the paired phone and last-known address (Settings page "Forget device").</summary>
    void Clear();

    // ---- Multi-device (M03, D-037) ----

    /// <summary>
    /// Every phone this PC has paired with, newest first. Exactly one of them is active at a
    /// time — <see cref="PairedSerial"/> — and every per-device property above resolves against
    /// that one, so callers never learn there is more than one device (D-037).
    /// </summary>
    IReadOnlyList<KnownDevice> KnownDevices { get; }

    /// <summary>Raised when a device is added to or removed from <see cref="KnownDevices"/>.</summary>
    event Action? KnownDevicesChanged;

    /// <summary>Raised after <see cref="SetActiveDevice"/> repoints every per-device property.</summary>
    event Action? ActiveDeviceChanged;

    /// <summary>
    /// Makes <paramref name="serial"/> the active device: address, certificate, sync lanes and
    /// folder pair all switch to that phone's record. No-op if it is already active or unknown.
    /// </summary>
    void SetActiveDevice(string serial);

    /// <summary>Hides a device's tab without unpairing it (closing a tab ≠ forgetting — D-033).</summary>
    void SetDeviceHidden(string serial, bool hidden);

    /// <summary>Removes one device from the list (closing its tab is not this — that's explicit forget).</summary>
    void ForgetKnownDevice(string serial);
}

/// <summary>
/// A phone this PC knows about, whether or not it is the active one (M03). Everything that used
/// to be a single global setting lives here per device, so switching tabs can never show one
/// phone's address, certificate or sync lanes against another.
/// </summary>
public sealed record KnownDevice(
    string Serial,
    string Model,
    DateTimeOffset FirstPairedUtc,
    string? LastHostPort = null,
    string? PhoneCertBase64 = null,
    Dictionary<string, bool>? SyncLanes = null,
    string? FolderSyncPcPath = null,
    string? FolderSyncPhonePath = null,
    bool Hidden = false,
    ConnectionPreference Preference = ConnectionPreference.Auto,
    DesktopModeSettings? DesktopMode = null,
    MirrorSettings? Mirror = null,
    HomeLayout? Home = null);

/// <summary>Persists known-device data under %LOCALAPPDATA%\Linc.</summary>
public sealed class DeviceRegistry : IDeviceRegistry
{
    /// <summary>
    /// The settings file. The five per-device fields (LastHostPort, PhoneCertBase64, SyncLanes,
    /// FolderSyncPcPath, FolderSyncPhonePath) are <b>legacy</b> — they are still read so a
    /// pre-M03 file migrates into the active device's record, and written back as null.
    /// </summary>
    private sealed record Settings(
        string? LastHostPort,
        string? PairedSerial,
        string? PairedModel,
        bool? ClipboardSyncEnabled,
        bool? NotificationSyncEnabled,
        bool? AllowUsbConnections,
        string? PhoneCertBase64 = null,
        bool? BackgroundConnectionEnabled = null,
        Dictionary<string, bool>? SyncLanes = null,
        string? FolderSyncPcPath = null,
        string? FolderSyncPhonePath = null,
        List<KnownDevice>? KnownDevices = null,
        // M9b (D-045): notification history is opt-in. Appended to the end of the record so an
        // older settings.json still deserializes — System.Text.Json ignores unmapped members and
        // tolerates missing trailing ones. Their `??` defaults below carry the §2.1/§2.3 rules.
        bool? NotificationHistoryEnabled = null,
        int? NotificationHistoryRetentionDays = null,
        // M12e: opt-in autostart. Appended to the end for the same reason as the two M9b fields
        // above it — an older settings.json still deserializes.
        bool? StartWithWindows = null,
        // M17b B2: appended at the end for the same migration reason as every field above it —
        // an older settings.json still deserializes and both read back as null, i.e. OFF.
        // M19 C1 RETIRED UpdateManifestUrl: the address is a build-time constant now. The slot is
        // kept and always written null so an M17b-era settings.json still round-trips positionally
        // — removing it would shift every field after it and silently mis-read older files.
        string? UpdateManifestUrl = null,
        string? SkippedUpdateVersion = null,
        // M18 B1: appended at the end like every field above it. Note the default here is ON, so a
        // missing key (an upgraded install) keeps the dynamic colour the app already had.
        bool? UsePhoneColours = null,
        // M19 C3: appended at the end for the same migration reason as every field above it. The
        // default is ON, so an upgraded install starts on the update channel rather than silently
        // opted out of it.
        bool? UpdatesEnabled = null);

    /// <summary>
    /// The owner's real store, <c>%LOCALAPPDATA%\Linc</c> — what production always uses. Exposed
    /// so a harness can assert the default resolves here by comparing the <b>string</b>, never by
    /// writing to it (D-057).
    /// </summary>
    public static string DefaultRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc");

    /// <summary>
    /// The store directory this instance was built against — <see cref="DefaultRootPath"/> unless a
    /// root was passed in. **Every other store under <c>%LOCALAPPDATA%\Linc</c> must take its root
    /// from here, not from <see cref="Environment.GetFolderPath"/> directly** (D-057/D-058): that is
    /// what stops a harness pointed at a temp root from writing into the owner's real
    /// <c>cache\</c> as well as their real <c>settings.json</c>. <see cref="AppCatalog"/> is the
    /// first such store; the rule holds for every future one.
    /// </summary>
    public string RootPath { get; }

    /// <summary>The settings.json this instance reads and writes. Under <see cref="DefaultRootPath"/> unless a root was passed in.</summary>
    public string SettingsPath { get; }

    public string? PairedSerial { get; private set; }
    public string? PairedModel { get; private set; }
    public bool ClipboardSyncEnabled { get; private set; } = true;
    public bool NotificationSyncEnabled { get; private set; } = true;
    public bool AllowUsbConnections { get; private set; } = true;
    public bool BackgroundConnectionEnabled { get; private set; } = true;

    // M9b (D-045 §2.1): history is OFF by default. The `= false` here is the in-code default;
    // the constructor's `?? false` re-asserts it on a missing/null key for an upgraded install.
    // If you find yourself changing either of these to `true` you have misread the task.
    public bool NotificationHistoryEnabled { get; private set; } = false;
    // §2.3: 30-day retention when the feature is first enabled. The Settings UI offers 7/30/90.
    public int NotificationHistoryRetentionDays { get; private set; } = 30;

    // M12e: autostart is OFF by default, same reasoning as NotificationHistoryEnabled above —
    // the `= false` here is the in-code default; the constructor's `?? false` re-asserts it on a
    // missing/null key for an upgraded install.
    public bool StartWithWindows { get; private set; } = false;

    // M19 C3: ON by default — the constructor's `?? true` below re-asserts it for an upgraded
    // install with no key, so nobody is silently left off the update channel.
    public bool UpdatesEnabled { get; private set; } = true;

    public string? SkippedUpdateVersion { get; private set; }

    // M18 B1: ON by default — `?? true` below re-asserts it for an upgraded install with no key.
    public bool UsePhoneColours { get; private set; } = true;

    /// <summary>Raised when <see cref="UsePhoneColours"/> changes, so the theme can re-derive.</summary>
    public event Action? UsePhoneColoursChanged;

    // ---- Per-device settings: these all read through the active device's record (M03, D-037).
    // The property names and shapes are exactly what they were when they were global, so every
    // caller — connection, TLS, sync engine, view models — is untouched by multi-device.

    /// <summary>The active device's record, or null before anything is paired.</summary>
    private KnownDevice? Active => _knownDevices.FirstOrDefault(d => d.Serial == PairedSerial);

    public string? LastHostPort => Active?.LastHostPort;
    public string? PhoneCertBase64 => Active?.PhoneCertBase64;
    public string? FolderSyncPcPath => Active?.FolderSyncPcPath;

    public string FolderSyncPhonePath =>
        string.IsNullOrWhiteSpace(Active?.FolderSyncPhonePath) ? "/sdcard/Download" : Active.FolderSyncPhonePath;

    public ConnectionPreference ConnectionPreference => Active?.Preference ?? ConnectionPreference.Auto;
    public event Action? ConnectionPreferenceChanged;

    public DesktopModeSettings DesktopMode => Active?.DesktopMode ?? new DesktopModeSettings();
    public event Action? DesktopModeChanged;

    public void SaveDesktopMode(DesktopModeSettings settings)
    {
        UpdateActive(d => d with { DesktopMode = settings });
        DesktopModeChanged?.Invoke();
    }

    public MirrorSettings Mirror => Active?.Mirror ?? MirrorSettings.BalancedDefaults;
    public event Action? MirrorChanged;

    public void SaveMirror(MirrorSettings settings)
    {
        UpdateActive(d => d with { Mirror = settings });
        MirrorChanged?.Invoke();
    }

    public HomeLayout Home => Active?.Home ?? HomeLayout.Default;
    public event Action? HomeChanged;

    public void SaveHome(HomeLayout layout)
    {
        UpdateActive(d => d with { Home = layout });
        HomeChanged?.Invoke();
    }

    public void SaveConnectionPreference(ConnectionPreference preference)
    {
        if (ConnectionPreference == preference)
        {
            return;
        }
        UpdateActive(d => d with { Preference = preference });
        ConnectionPreferenceChanged?.Invoke();
    }

    /// <param name="rootPath">
    /// Directory to keep <c>settings.json</c> in. Defaults to <see cref="DefaultRootPath"/>, so
    /// production call sites pass nothing and are unaffected. Verification harnesses pass a fresh
    /// temp directory, which is what makes it <i>structurally impossible</i> for one to damage the
    /// owner's real pairing and its TLS certificate — even if the run is killed (D-057).
    /// </param>
    public DeviceRegistry(string? rootPath = null)
    {
        RootPath = rootPath ?? DefaultRootPath;
        SettingsPath = Path.Combine(RootPath, "settings.json");
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                PairedSerial = settings?.PairedSerial;
                PairedModel = settings?.PairedModel;
                ClipboardSyncEnabled = settings?.ClipboardSyncEnabled ?? true;
                NotificationSyncEnabled = settings?.NotificationSyncEnabled ?? true;
                AllowUsbConnections = settings?.AllowUsbConnections ?? true;
                BackgroundConnectionEnabled = settings?.BackgroundConnectionEnabled ?? true;
                // M9b (D-045): a missing/null key means OFF — the only acceptable default for a
                // privacy-sensitive feature that puts notification bodies on disk (§2.1). An
                // upgraded install with no stored preference is indistinguishable from a fresh
                // install here, and must be just as quiet.
                NotificationHistoryEnabled = settings?.NotificationHistoryEnabled ?? false;
                // §2.3: 30-day window when the feature is first enabled. Not user-visible until the
                // toggle is on; the UI disables the picker while the toggle is off.
                NotificationHistoryRetentionDays = settings?.NotificationHistoryRetentionDays ?? 30;
                // M12e: a missing/null key means OFF, same reasoning as NotificationHistoryEnabled
                // above — an upgraded install with no stored preference must be as quiet as a
                // fresh one.
                StartWithWindows = settings?.StartWithWindows ?? false;
                // M19 C3: a missing/null key means ON. The retired UpdateManifestUrl slot is
                // deliberately not read — the address is UpdateChannel.ManifestUrl now.
                UpdatesEnabled = settings?.UpdatesEnabled ?? true;
                SkippedUpdateVersion = Blank(settings?.SkippedUpdateVersion);
                UsePhoneColours = settings?.UsePhoneColours ?? true;
                _knownDevices = settings?.KnownDevices ?? [];
                // Settings written before M03 have no list; adopt the active device so an
                // existing user's tab strip isn't empty on first run after upgrading.
                if (_knownDevices.Count == 0 && settings?.PairedSerial is { } existing)
                {
                    _knownDevices.Add(new KnownDevice(
                        existing, settings.PairedModel ?? "Android phone", DateTimeOffset.UtcNow));
                }
                MigrateLegacyPerDeviceSettings(settings);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt or unreadable settings just mean a fresh start.
        }
    }

    /// <summary>
    /// Folds a pre-M03 settings file's global per-device values into the active device's record.
    /// Runs once: after the first Persist() the legacy fields are written back as null.
    /// </summary>
    private void MigrateLegacyPerDeviceSettings(Settings? settings)
    {
        if (settings is null || PairedSerial is null)
        {
            return;
        }
        var hasLegacy = settings.LastHostPort is not null || settings.PhoneCertBase64 is not null
            || settings.SyncLanes is { Count: > 0 } || settings.FolderSyncPcPath is not null
            || settings.FolderSyncPhonePath is not null;
        if (!hasLegacy)
        {
            return;
        }
        UpdateActive(d => d with
        {
            LastHostPort = d.LastHostPort ?? settings.LastHostPort,
            PhoneCertBase64 = d.PhoneCertBase64 ?? settings.PhoneCertBase64,
            SyncLanes = d.SyncLanes ?? settings.SyncLanes,
            FolderSyncPcPath = d.FolderSyncPcPath ?? settings.FolderSyncPcPath,
            FolderSyncPhonePath = d.FolderSyncPhonePath ?? settings.FolderSyncPhonePath,
        }, persist: false);
    }

    public void SaveLastHostPort(string hostPort) =>
        UpdateActive(d => d with { LastHostPort = hostPort });

    public IReadOnlyList<KnownDevice> KnownDevices => _knownDevices;
    public event Action? KnownDevicesChanged;
    public event Action? ActiveDeviceChanged;

    private List<KnownDevice> _knownDevices = [];

    /// <summary>Rewrites the active device's record in place. Records are immutable, so this is
    /// the single funnel every per-device setter goes through.</summary>
    private void UpdateActive(Func<KnownDevice, KnownDevice> change, bool persist = true)
    {
        var index = _knownDevices.FindIndex(d => d.Serial == PairedSerial);
        if (index < 0)
        {
            return; // nothing paired yet; the value lands when SavePairedDevice creates the record
        }
        _knownDevices[index] = change(_knownDevices[index]);
        if (persist)
        {
            Persist();
        }
    }

    public void SavePairedDevice(string serial, string model)
    {
        var wasActive = PairedSerial == serial;
        PairedSerial = serial;
        PairedModel = model;

        // Remember every phone that has ever paired, so the tab strip has something to list
        // and M02's per-serial cache has an owner even when that phone isn't the active one.
        var index = _knownDevices.FindIndex(d => d.Serial == serial);
        if (index < 0)
        {
            _knownDevices.Insert(0, new KnownDevice(serial, model, DateTimeOffset.UtcNow));
            KnownDevicesChanged?.Invoke();
        }
        else if (_knownDevices[index].Hidden)
        {
            // Reconnecting to a phone whose tab was closed brings the tab back.
            _knownDevices[index] = _knownDevices[index] with { Hidden = false };
            KnownDevicesChanged?.Invoke();
        }
        Persist();
        if (!wasActive)
        {
            // Deliberately NOT ActiveDeviceChanged: that event means "the user picked a different
            // tab" and makes the supervisor drop its link. Here the link is the thing that just
            // succeeded — raising it would tear down the connection we are in the middle of
            // making, from inside the supervisor's own gate. The tab strip just needs a repaint.
            KnownDevicesChanged?.Invoke();
        }
    }

    public void SetActiveDevice(string serial)
    {
        if (serial == PairedSerial || _knownDevices.FirstOrDefault(d => d.Serial == serial) is not { } device)
        {
            return;
        }
        PairedSerial = device.Serial;
        PairedModel = device.Model;
        Persist();
        ActiveDeviceChanged?.Invoke();
        SyncLanesChanged?.Invoke(); // the new device's lanes are (probably) different ones
    }

    public void SetDeviceHidden(string serial, bool hidden)
    {
        var index = _knownDevices.FindIndex(d => d.Serial == serial);
        if (index < 0 || _knownDevices[index].Hidden == hidden)
        {
            return;
        }
        _knownDevices[index] = _knownDevices[index] with { Hidden = hidden };
        Persist();
        KnownDevicesChanged?.Invoke();
    }

    public void ForgetKnownDevice(string serial)
    {
        if (_knownDevices.RemoveAll(d => d.Serial == serial) > 0)
        {
            if (PairedSerial == serial)
            {
                // The active phone was forgotten: fall back to another known one, or nothing.
                var next = _knownDevices.FirstOrDefault(d => !d.Hidden) ?? _knownDevices.FirstOrDefault();
                PairedSerial = next?.Serial;
                PairedModel = next?.Model;
                ActiveDeviceChanged?.Invoke();
            }
            Persist();
            KnownDevicesChanged?.Invoke();
        }
    }

    public void SaveClipboardSyncEnabled(bool enabled)
    {
        ClipboardSyncEnabled = enabled;
        Persist();
    }

    public void SaveNotificationSyncEnabled(bool enabled)
    {
        NotificationSyncEnabled = enabled;
        Persist();
    }

    public void SaveAllowUsbConnections(bool enabled)
    {
        AllowUsbConnections = enabled;
        Persist();
    }

    public void SavePhoneCert(string certBase64) =>
        UpdateActive(d => d with { PhoneCertBase64 = certBase64 });

    public void SaveBackgroundConnectionEnabled(bool enabled)
    {
        BackgroundConnectionEnabled = enabled;
        Persist();
    }

    public void SaveNotificationHistoryEnabled(bool enabled)
    {
        NotificationHistoryEnabled = enabled;
        Persist();
    }

    public void SaveNotificationHistoryRetentionDays(int days)
    {
        // Clamp to the three supported windows (§2.3). Anything else is a caller bug or a stray
        // JSON value from a hand-edited file; we keep the well-defined choices.
        NotificationHistoryRetentionDays = days == 7 || days == 90 ? days : 30;
        Persist();
    }

    public void SaveStartWithWindows(bool enabled)
    {
        StartWithWindows = enabled;
        Persist();
    }

    public void SaveUpdatesEnabled(bool enabled)
    {
        UpdatesEnabled = enabled;
        Persist();
    }

    public void SaveSkippedUpdateVersion(string? version)
    {
        SkippedUpdateVersion = Blank(version);
        Persist();
    }

    public void SaveUsePhoneColours(bool enabled)
    {
        if (UsePhoneColours == enabled)
        {
            return;
        }
        UsePhoneColours = enabled;
        Persist();
        UsePhoneColoursChanged?.Invoke();
    }

    /// <summary>Whitespace-only becomes null, so an empty settings box leaves the feature inert.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool SyncLane(string lane) =>
        Active?.SyncLanes is { } lanes && lanes.TryGetValue(lane, out var on) && on;

    public event Action? SyncLanesChanged;

    public void SaveSyncLane(string lane, bool enabled)
    {
        UpdateActive(d =>
        {
            // Copy rather than mutate: the dictionary is part of an immutable record and is
            // shared with whatever was last serialized.
            var lanes = d.SyncLanes is null ? [] : new Dictionary<string, bool>(d.SyncLanes);
            lanes[lane] = enabled;
            return d with { SyncLanes = lanes };
        });
        SyncLanesChanged?.Invoke();
    }

    public void SaveFolderSyncPaths(string? pcPath, string phonePath) =>
        UpdateActive(d => d with
        {
            FolderSyncPcPath = pcPath,
            FolderSyncPhonePath = string.IsNullOrWhiteSpace(phonePath) ? "/sdcard/Download" : phonePath.Trim(),
        });

    public void Clear()
    {
        // Forgetting the active phone takes its address and certificate with it, and promotes
        // another known device if the user has more than one.
        if (PairedSerial is { } serial)
        {
            ForgetKnownDevice(serial);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // The five legacy per-device fields are written as null — they now live per device.
            // The two M9b history fields at the end round-trip through the positional record.
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(
                null, PairedSerial, PairedModel, ClipboardSyncEnabled, NotificationSyncEnabled,
                AllowUsbConnections, null, BackgroundConnectionEnabled, null,
                null, null, _knownDevices,
                NotificationHistoryEnabled, NotificationHistoryRetentionDays, StartWithWindows,
                null, SkippedUpdateVersion, UsePhoneColours, UpdatesEnabled)));
        }
        catch (IOException)
        {
            // Non-fatal: worst case the user pairs or types the address again next launch.
        }
    }
}
