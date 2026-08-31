using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public sealed class AdbDeviceVm(DeviceData device)
{
    public string Serial => device.Serial;
    public string State => device.State.ToString();
    public string Model => string.IsNullOrEmpty(device.Model) ? "—" : device.Model;
}

public sealed class ForwardVm(string local, string remote)
{
    public string Local => local;
    public string Remote => remote;
}

/// <summary>One row in the Settings › Devices list (M2a): a phone this PC has paired with.</summary>
public sealed class KnownDeviceVm
{
    public KnownDeviceVm(KnownDevice device, bool isActive, Action<KnownDeviceVm> setActive, Action<KnownDeviceVm> forget)
    {
        Serial = device.Serial;
        Model = device.Model;
        IsActive = isActive;
        StateText = isActive ? "Active" : device.Hidden ? "Tab closed" : "Paired";
        SetActiveCommand = new RelayCommand(() => setActive(this));
        ForgetCommand = new RelayCommand(() => forget(this));
    }

    public string Serial { get; }
    public string Model { get; }
    public bool IsActive { get; }
    public bool IsNotActive => !IsActive;
    public string StateText { get; }
    public RelayCommand SetActiveCommand { get; }
    public RelayCommand ForgetCommand { get; }
}

/// <summary>
/// Power-user surface: ADB status/device list/forwards, a real shell-command box,
/// and connection/sync controls. The shell box deliberately reverses D-001's
/// default "no raw ADB" posture for this one page, per explicit user request —
/// see docs/DECISIONS.md D-009. Everything else here is safe, bounded actions
/// (restart server, forget device) rather than arbitrary commands.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAdbServerHost _adbServerHost;
    private readonly IConnectionManager _connection;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly IClipboardSyncService _clipboardSync;
    private readonly INotificationSyncService _notifications;
    private readonly ILogService _log;
    private readonly ITlsTransportService _tls;
    private readonly LincStore _store;
    private readonly StartupRegistration _startup;
    private readonly UpdateCheckService _updates;
    private readonly AdbClient _adb = new();
    private readonly DispatcherQueue _dispatcher;

    private readonly IDeviceCacheService _cache;

    public ObservableCollection<AdbDeviceVm> Devices { get; } = [];
    public ObservableCollection<ForwardVm> Forwards { get; } = [];

    /// <summary>Every phone paired with this PC, for the Devices management section (M2a).</summary>
    public ObservableCollection<KnownDeviceVm> KnownDevices { get; } = [];

    public SettingsViewModel(
        IAdbServerHost adbServerHost,
        IConnectionManager connection,
        IConnectionSupervisor supervisor,
        IDeviceRegistry registry,
        IClipboardSyncService clipboardSync,
        INotificationSyncService notifications,
        ILogService log,
        ITlsTransportService tls,
        IDeviceCacheService cache,
        LincStore store,
        StartupRegistration startup,
        UpdateCheckService updates)
    {
        _updates = updates;
        _cache = cache;
        _tls = tls;
        _adbServerHost = adbServerHost;
        _connection = connection;
        _supervisor = supervisor;
        _registry = registry;
        _clipboardSync = clipboardSync;
        _notifications = notifications;
        _log = log;
        _store = store;
        _startup = startup;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        AdbPathText = adbServerHost.AdbPath ?? "Not found";
        ShellOutput = "";
        ShellCommand = "";
        // M12 (B2.3): labelled so a beta tester can unambiguously report their version.
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        AppVersionText = $"Linc {appVersion}";

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(OneAtATimeText));
            OnPropertyChanged(nameof(ShowOneAtATimeNote));
        });

        // Keep the Devices list in step with the tab strip: pairing, closing a tab and switching
        // all flow through these two registry events (M2a).
        _registry.KnownDevicesChanged += () => _dispatcher.TryEnqueue(RebuildKnownDevices);
        _registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(RebuildKnownDevices);
        RebuildKnownDevices();

        _ = RefreshDevicesAsync();
    }

    [ObservableProperty]
    public partial string AdbPathText { get; set; }

    [ObservableProperty]
    public partial string AppVersionText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial string ShellCommand { get; set; }

    [ObservableProperty]
    public partial string ShellOutput { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasStatusMessage => StatusMessage is not null;
    public bool IsPaused => _supervisor.State == LinkState.Paused;
    public bool HasConnectedDevice => _connection.RawDevice is not null;

    public bool ClipboardSyncEnabled
    {
        get => _clipboardSync.Enabled;
        set { _clipboardSync.Enabled = value; OnPropertyChanged(); }
    }

    public bool NotificationSyncEnabled
    {
        get => _notifications.Enabled;
        set { _notifications.Enabled = value; OnPropertyChanged(); }
    }

    public bool AllowUsbConnections
    {
        get => _registry.AllowUsbConnections;
        set { _registry.SaveAllowUsbConnections(value); OnPropertyChanged(); }
    }

    /// <summary>Standing presence (v9, D-014): advertise + accept direct phone connections.</summary>
    public bool BackgroundConnectionEnabled
    {
        get => _registry.BackgroundConnectionEnabled;
        set
        {
            _registry.SaveBackgroundConnectionEnabled(value);
            if (value)
            {
                _tls.Start();
            }
            else
            {
                _tls.Stop();
            }
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// "Start Linc when I sign in" (M12e). The setter is the only place that calls both
    /// <see cref="IDeviceRegistry.SaveStartWithWindows"/> (the persisted preference) and
    /// <see cref="StartupRegistration"/> (the actual HKCU Run key) — setting one without the
    /// other is the M5c-2 dead-UI defect this task calls out.
    /// </summary>
    public bool StartWithWindows
    {
        get => _registry.StartWithWindows;
        set
        {
            _registry.SaveStartWithWindows(value);
            if (value)
            {
                _startup.Enable(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!);
            }
            else
            {
                _startup.Disable();
            }
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// M18 B1: "Use my phone's colours". Default ON. Off returns the app to M14's neutral ramp —
    /// which is the palette it ships with, so this is a return to the base state, not a special case.
    /// </summary>
    public bool UsePhoneColours
    {
        get => _registry.UsePhoneColours;
        set
        {
            _registry.SaveUsePhoneColours(value);
            OnPropertyChanged();
        }
    }

    // ---- M19 C3: auto-update ----
    //
    // There is no address to configure any more: the manifest lives in Linc's own repository at
    // Releases/update.json and its URL is a build-time constant (UpdateChannel.ManifestUrl). What
    // is left is ONE switch, default ON. Off means the update code path cannot execute at all —
    // no request, no UI, no log line — and the card then says where to update by hand instead.
    // Nothing about the user is ever sent: the check is a plain GET of a static file (M17b B5).

    /// <summary>"Keep Linc up to date". Default ON; off leaves the whole feature inert.</summary>
    public bool UpdatesEnabled
    {
        get => _registry.UpdatesEnabled;
        set
        {
            if (_registry.UpdatesEnabled == value)
            {
                return;
            }
            _registry.SaveUpdatesEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateStatus));
        }
    }

    /// <summary>Plain-language state for the card. Never shows a URL, a status code or a hash.</summary>
    public string UpdateStatus => UpdatesEnabled
        ? "Linc will check for updates when it starts, at most once every 6 hours."
        : $"Automatic updates are off. Linc won't check for anything. You can still update by hand from {UpdateChannel.ReleasesUrl}.";

    /// <summary>Result of the last "Check now" press. Plain language; empty until one happens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckResult))]
    public partial string UpdateCheckResult { get; set; } = string.Empty;

    /// <summary>Bound to Visibility as a BOOL — never bind a string to Visibility (the M2b crash).</summary>
    public bool HasUpdateCheckResult => !string.IsNullOrWhiteSpace(UpdateCheckResult);

    /// <summary>
    /// M19 C3: "Check now". Only useful while the switch is on — with it off this says so and
    /// makes no request at all, because UpdateCheckService refuses before touching the network.
    /// The offer itself appears on the shell's update card, which the service's Checked event
    /// drives; this line only reports whether the check found anything.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        if (!UpdatesEnabled)
        {
            UpdateCheckResult = "Turn on “Keep Linc up to date” first.";
            return;
        }

        UpdateCheckResult = "Checking…";
        var action = await _updates.CheckNowAsync().ConfigureAwait(true);
        UpdateCheckResult = action == UpdateAction.None
            ? "Linc is up to date."
            : "An update is available — see the banner at the top of the window.";
    }

    // ---- M9b (D-045): notification history card ----
    //
    // The privacy posture from the task (§2) is enforced here too:
    //   - Default OFF, and so is the registry: turning it on is the user consenting (§2.1).
    //   - Turning it OFF does NOT clear stored rows (§2.5) — only ClearHistoryCommand does, and
    //     the card's helper text says so.
    //   - Turning it ON never backfills; rows start from the moment it is enabled (§2.5).
    //   - Shortening the retention window prunes immediately (§2.3) — the user shortening it
    //     expects the old data gone now, not in a day.

    /// <summary>Persistent notification history is ON (D-045).</summary>
    public bool NotificationHistoryEnabled
    {
        get => _registry.NotificationHistoryEnabled;
        set
        {
            _registry.SaveNotificationHistoryEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHistoryRetentionEnabled));
        }
    }

    /// <summary>
    /// The retention picker is disabled while the feature is off (§2.6). Bound to the
    /// picker's <c>IsEnabled</c> so the whole group greys out when the toggle is off.
    /// </summary>
    public bool IsHistoryRetentionEnabled => NotificationHistoryEnabled;

    /// <summary>Three radio-button helpers for the 7/30/90 picker (§2.3). One of them is true.</summary>
    public bool IsHistory7Days
    {
        get => _registry.NotificationHistoryRetentionDays == 7;
        set { if (value) SetRetention(7); }
    }
    public bool IsHistory30Days
    {
        get => _registry.NotificationHistoryRetentionDays == 30;
        set { if (value) SetRetention(30); }
    }
    public bool IsHistory90Days
    {
        get => _registry.NotificationHistoryRetentionDays == 90;
        set { if (value) SetRetention(90); }
    }

    private void SetRetention(int days)
    {
        var oldDays = _registry.NotificationHistoryRetentionDays;
        _registry.SaveNotificationHistoryRetentionDays(days);
        // §2.3: shortening the window prunes immediately. Widening does nothing (old rows now
        // live inside the new window). Fire-and-forget — same non-blocking stance as the startup
        // prune, and the store does its own degrade-on-failure.
        if (days < oldDays)
        {
            _ = _store.PruneNotificationsAsync(days, serial: null);
            StatusMessage = $"History kept for {days} days. Older entries were removed.";
        }
        else
        {
            // M9c §0.3: widening (e.g. 30 → 90) must NOT claim a prune — nothing was removed.
            // State only the new window. Shortening keeps the original message above.
            StatusMessage = $"History kept for {days} days.";
        }
    }

    /// <summary>
    /// The "Clear history" action (§2.4): deletes every stored notification, for all devices,
    /// and reports how many rows it removed in plain language. Works whether the feature is on
    /// or off — turning it off does NOT delete stored rows, this does. The confirmation gate is
    /// in the Settings page code-behind (matching the Files page delete dialog).
    /// </summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var removed = await _store.DeleteAllNotificationsAsync();
        StatusMessage = removed > 0
            ? $"Cleared stored notification history ({removed} item(s))."
            : "Notification history is empty — nothing to clear.";
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await _adb.GetDevicesAsync(CancellationToken.None);
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new AdbDeviceVm(device));
            }

            Forwards.Clear();
            if (_connection.RawDevice is { } current)
            {
                var forwards = await _adb.ListForwardAsync(current, CancellationToken.None);
                foreach (var forward in forwards)
                {
                    Forwards.Add(new ForwardVm(forward.Local, forward.Remote));
                }
            }
            OnPropertyChanged(nameof(HasConnectedDevice));
        }
        catch (Exception)
        {
            // Best-effort diagnostic view; leave the lists as they were.
        }
    }

    [RelayCommand]
    private async Task RestartAdbServerAsync()
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await _adbServerHost.RestartAsync(CancellationToken.None);
            StatusMessage = "ADB server restarted.";
        }
        catch (LincException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshDevicesAsync();
        }
    }

    [RelayCommand]
    private void ForgetDevice()
    {
        if (_registry.PairedSerial is { } paired)
        {
            _cache.Clear(paired); // don't leave a remembered page for a phone we just forgot
        }
        _registry.Clear();
        _supervisor.RequestDisconnect();
        StatusMessage = "Forgot the paired phone. Pair again from the Device page.";
    }

    /// <summary>Whether the Devices management section has anything to show (M2a).</summary>
    public bool HasKnownDevices => KnownDevices.Count > 0;

    /// <summary>
    /// M15c A2: the standing one-phone line, from the same pure static every other surface uses.
    /// Empty when nothing is live, so the section says nothing rather than something untrue.
    /// </summary>
    public string OneAtATimeText => DeviceAdmission.OneAtATimeNotice(
        _supervisor.State == LinkState.Connected ? _supervisor.Device?.Model ?? _supervisor.Device?.Serial : null) ?? "";

    public bool ShowOneAtATimeNote => OneAtATimeText.Length > 0;

    private void RebuildKnownDevices()
    {
        KnownDevices.Clear();
        foreach (var device in _registry.KnownDevices)
        {
            KnownDevices.Add(new KnownDeviceVm(
                device, device.Serial == _registry.PairedSerial, ActivateDevice, ForgetOneDevice));
        }
        OnPropertyChanged(nameof(HasKnownDevices));
    }

    /// <summary>Make a phone active from the list; un-hides its tab if it had been closed.</summary>
    private void ActivateDevice(KnownDeviceVm vm)
    {
        _registry.SetDeviceHidden(vm.Serial, hidden: false); // a "Set active" also re-opens the tab
        _registry.SetActiveDevice(vm.Serial);
        StatusMessage = $"Switched to {vm.Model}.";
    }

    /// <summary>真forget: unpair the phone AND delete its cached copy/profile (M2a).</summary>
    private void ForgetOneDevice(KnownDeviceVm vm)
    {
        _cache.Clear(vm.Serial);
        // Forgetting the active phone raises ActiveDeviceChanged inside the registry, which the
        // supervisor turns into a drop + hunt for whatever gets promoted (or a device-less state).
        _registry.ForgetKnownDevice(vm.Serial);
        StatusMessage = $"Forgot {vm.Model} and cleared its saved copy.";
    }

    /// <summary>Drops the offline copy without unpairing (M02, D-032).</summary>
    [RelayCommand]
    private void ClearDeviceData()
    {
        if (_registry.PairedSerial is { } serial)
        {
            _cache.Clear(serial);
            StatusMessage = "Cleared the saved copy of this phone. It fills in again once the phone connects.";
        }
        else
        {
            StatusMessage = "No phone is paired, so there's nothing saved to clear.";
        }
    }

    [RelayCommand]
    private void ResumeAutomatic() => _supervisor.ResumeAutomatic();

    [RelayCommand]
    private void ClearLogs() => _log.Clear();

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_log.LogFolderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.LogFolderPath}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RunShellCommandAsync()
    {
        var command = ShellCommand.Trim();
        if (command.Length == 0 || _connection.RawDevice is not { } device)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(command, device, receiver, CancellationToken.None);
            ShellOutput = receiver.ToString();
            _log.Log(LogLevel.Info, $"Ran shell command: {command}");
        }
        catch (Exception ex)
        {
            ShellOutput = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
