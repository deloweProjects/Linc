using System.Net.NetworkInformation;
using AdvancedSharpAdbClient.Models;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

public enum LinkState
{
    /// <summary>No phone has ever been paired; waiting for the user to pair.</summary>
    NoDevice,

    /// <summary>Watching discovery for the paired phone.</summary>
    Searching,

    Connecting,
    Connected,

    /// <summary>User asked to disconnect; automatic reconnection is suspended.</summary>
    Paused,
}

public interface IConnectionSupervisor : IDisposable
{
    LinkState State { get; }
    ConnectedDevice? Device { get; }

    /// <summary>How many times this process has reconnected after the first successful connect.</summary>
    int ReconnectCount { get; }

    /// <summary>When the current (or most recent) connection was established.</summary>
    DateTimeOffset? ConnectedAtUtc { get; }

    /// <summary>Last error message, kept until the next one arrives — survives a later successful reconnect.</summary>
    string? LastErrorMessage { get; }

    /// <summary>Round-trip time of the most recent health probe, or null before the first one.</summary>
    int? LastRttMs { get; }

    /// <summary>Raised after any state transition. May fire on background threads.</summary>
    event Action? StateChanged;

    /// <summary>Fresh device status from the health loop. May fire on background threads.</summary>
    event Action<DeviceStatus>? StatusUpdated;

    /// <summary>A user-facing message worth showing. May fire on background threads.</summary>
    event Action<string>? ErrorRaised;

    /// <summary>A USB-attached phone was seen that isn't the paired device — needs an explicit
    /// user confirmation before Linc will connect to it. May fire on background threads.</summary>
    event Action<DeviceData>? UsbDeviceNeedsConfirmation;

    /// <summary>
    /// The user chose Cancel on the confirmation card for this phone (M15c A1). Linc stops
    /// offering it until the cable is pulled and put back — the 3 s poll would otherwise re-raise
    /// the card every 3 s, which is the same nag the card exists to replace.
    /// </summary>
    void DeclineUsbDevice(string serial);

    /// <summary>
    /// The transport the user picked by hand, or null when Linc is choosing automatically
    /// (M15a Part C).
    /// </summary>
    LinkTransport? ManualTransport { get; }

    /// <summary>True when the live link is the one the user asked for rather than the ranked pick.</summary>
    bool TransportChosenByHand { get; }

    /// <summary>
    /// Pin the link to one transport, or pass null to go back to automatic. Honoured until it is
    /// cleared or the chosen transport stops answering — a drop falls back and says so, it never
    /// leaves the caption claiming a transport that is gone.
    /// </summary>
    void SetManualTransport(LinkTransport? transport);

    void Start();

    /// <summary>
    /// The active device changed (a tab switch — M03/D-037): drop the current link and start
    /// hunting for whichever phone the registry now points at. Safe to call when nothing is
    /// connected, and a no-op before <see cref="Start"/>.
    /// </summary>
    void RetargetActiveDevice();

    Task ConnectManualAsync(string hostPort);
    Task ConnectUsbManualAsync(DeviceData device);

    /// <summary>Trust the next connect advert regardless of serial — call right after pairing.</summary>
    void ExpectNextDevice();

    void RequestDisconnect();
    void ResumeAutomatic();
}

/// <summary>
/// The reconnection state machine (docs/ROADMAP.md M4). Discovery adverts drive
/// connection attempts; a 15 s health probe detects dead links (including after PC
/// sleep or phone reboot); network changes trigger an immediate probe; per-address
/// exponential backoff prevents hammering a phone that keeps refusing.
/// </summary>
public sealed class ConnectionSupervisor(
    IConnectionManager connection,
    IDiscoveryService discovery,
    IUsbWatcherService usbWatcher,
    IDeviceRegistry registry,
    ITlsTransportService tlsTransport,
    IBlePresenceService blePresence,
    ILogService log) : IConnectionSupervisor
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);
    private static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (int Failures, DateTime NextAttemptUtc)> _backoff = [];
    private CancellationTokenSource? _healthCts;
    private bool _expectAny;
    private bool _started;
    private int _recovering;

    /// <summary>
    /// Serials already reported as unusable, with the sentence used. The USB poll re-reports every
    /// 3 s while a phone sits at the trust prompt; the user needs telling once, not twenty times a
    /// minute. Cleared the moment the phone becomes usable, so a second visit speaks again.
    /// </summary>
    private readonly Dictionary<string, string> _unusableReported = [];

    /// <summary>
    /// Serials the user cancelled the confirmation card for (M15c A1). Cleared when the watcher
    /// reports the serial gone — i.e. when the cable comes out — so replugging asks again.
    /// </summary>
    private readonly HashSet<string> _declinedUsb = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the health probe last failed, so <see cref="OnLinkDropped"/> can report how long
    /// detection actually took rather than leaving it to be guessed (M15a Part B).
    /// </summary>
    private DateTime? _firstProbeFailureUtc;

    public LinkState State { get; private set; } = LinkState.NoDevice;
    public ConnectedDevice? Device { get; private set; }
    public int ReconnectCount { get; private set; }
    public DateTimeOffset? ConnectedAtUtc { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public int? LastRttMs { get; private set; }
    private bool _hasConnectedOnce;

    public event Action? StateChanged;
    public event Action<DeviceStatus>? StatusUpdated;
    public event Action<string>? ErrorRaised;
    public event Action<DeviceData>? UsbDeviceNeedsConfirmation;

    public LinkTransport? ManualTransport { get; private set; }
    public bool TransportChosenByHand { get; private set; }

    public void SetManualTransport(LinkTransport? transport)
    {
        if (ManualTransport == transport)
        {
            return;
        }
        ManualTransport = transport;
        TransportChosenByHand = transport is not null && Device?.Transport == transport;
        log.Log(LogLevel.Info, transport is { } t
            ? $"Connection pinned to {Describe(t)} by hand."
            : "Connection back to choosing the best transport automatically.");
        StateChanged?.Invoke();
        if (transport is null)
        {
            return;
        }
        // Asking for a transport should act, not merely record a preference for the next event:
        // nudge the paths that can bring it up now. USB is covered by the 3 s poll.
        discovery.ScanNow();
        _ = RecoverPresentWirelessDeviceAsync();
    }

    /// <summary>Plain language for a transport — user-facing, so never the enum name.</summary>
    private static string Describe(LinkTransport transport) => transport switch
    {
        LinkTransport.AdbUsb => "the USB cable",
        LinkTransport.AdbWireless => "wireless debugging",
        LinkTransport.DirectTls => "a direct Wi-Fi link",
        _ => "this connection",
    };

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        discovery.ConnectServiceSeen += OnConnectServiceSeen;
        usbWatcher.UsbDeviceSeen += OnUsbDeviceSeen;
        usbWatcher.UsbDeviceUnusable += OnUsbDeviceUnusable;
        usbWatcher.UsbDeviceGone += OnUsbDeviceGone;
        tlsTransport.ControlArrived += OnTlsControlArrived;
        registry.ActiveDeviceChanged += RetargetActiveDevice;
        registry.ConnectionPreferenceChanged += OnConnectionPreferenceChanged;
        blePresence.DeviceSighted += OnBleSighting;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        discovery.Start();
        usbWatcher.Start();
        blePresence.Start(); // hint only, and a no-op on a PC without Bluetooth (M04, D-034)
        if (registry.BackgroundConnectionEnabled)
        {
            tlsTransport.Start(); // standing presence: advertise + listen (D-014/D-022)
        }
        SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
    }

    public void RetargetActiveDevice()
    {
        if (!_started)
        {
            return;
        }
        _gate.Wait();
        try
        {
            StopHealthLoop();
            connection.Disconnect();
            Device = null;
            _expectAny = false;
            _backoff.Clear(); // a different phone shouldn't inherit the old one's penalty box
            LastRttMs = null;
            ConnectedAtUtc = null;
            SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
            log.Log(LogLevel.Info, registry.PairedModel is { } model
                ? $"Switched to {model}; looking for it now."
                : "No phone selected.");
        }
        finally
        {
            _gate.Release();
        }
        // SetState only fires when the state actually changed; a switch from Connected to
        // Searching always does, but Searching→Searching (switching between two absent phones)
        // doesn't — and the UI still has to repaint for the new device.
        StateChanged?.Invoke();
    }

    public Task ConnectManualAsync(string hostPort) => ConnectAsync(hostPort, manual: true);
    public Task ConnectUsbManualAsync(DeviceData device) => ConnectUsbAsync(device, manual: true);

    public void ExpectNextDevice()
    {
        _expectAny = true;
        if (State is LinkState.NoDevice or LinkState.Paused)
        {
            SetState(LinkState.Searching);
        }
    }

    public void RequestDisconnect()
    {
        _gate.Wait();
        try
        {
            StopHealthLoop();
            connection.Disconnect();
            Device = null;
            _expectAny = false;
            SetState(LinkState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ResumeAutomatic()
    {
        if (State == LinkState.Paused)
        {
            SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
        }
    }

    public void Dispose()
    {
        discovery.ConnectServiceSeen -= OnConnectServiceSeen;
        usbWatcher.UsbDeviceSeen -= OnUsbDeviceSeen;
        usbWatcher.UsbDeviceUnusable -= OnUsbDeviceUnusable;
        usbWatcher.UsbDeviceGone -= OnUsbDeviceGone;
        tlsTransport.ControlArrived -= OnTlsControlArrived;
        registry.ActiveDeviceChanged -= RetargetActiveDevice;
        registry.ConnectionPreferenceChanged -= OnConnectionPreferenceChanged;
        blePresence.DeviceSighted -= OnBleSighting;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopHealthLoop();
        blePresence.Dispose();
        usbWatcher.Dispose();
        connection.Disconnect();
    }

    private void OnConnectServiceSeen(DiscoveredService service)
    {
        if (!ShouldEngage(LinkTransport.AdbWireless))
        {
            return;
        }
        var knownSerial = registry.PairedSerial;
        var matchesPairedPhone = knownSerial is not null &&
            service.InstanceName.Contains(knownSerial, StringComparison.OrdinalIgnoreCase);
        if (!_expectAny && !matchesPairedPhone)
        {
            return;
        }
        var address = $"{service.IpAddress}:{service.Port}";
        if (IsBackedOff(address))
        {
            return;
        }
        _ = ConnectAsync(address, manual: false);
    }

    /// <summary>
    /// The paired phone's Bluetooth beacon was heard, so it is physically here. That is not a
    /// way to connect — no data rides BLE (D-034) — it just means a Wi-Fi scan is worth doing
    /// right now instead of after the idle delay. Fires about once a second while in range,
    /// hence the cheap guard.
    /// </summary>
    private void OnBleSighting(string serial)
    {
        if (State is not (LinkState.Searching or LinkState.NoDevice) || serial != registry.PairedSerial)
        {
            return;
        }
        discovery.ScanNow();
    }

    /// <summary>
    /// ADB can see a phone over USB but cannot use it — almost always the trust prompt (M15a A3).
    /// Say so once per phone, in plain language; the watcher's own 3 s poll is the recovery, so
    /// there is nothing to wait on here and no fixed sleep anywhere in the path.
    /// </summary>
    private void OnUsbDeviceUnusable(DeviceData device, string message)
    {
        var serial = device.Serial ?? "";
        lock (_unusableReported)
        {
            if (_unusableReported.TryGetValue(serial, out var already) && already == message)
            {
                return;
            }
            _unusableReported[serial] = message;
        }
        LastErrorMessage = message;
        ErrorRaised?.Invoke(message);
        log.Log(LogLevel.Warn, message);
    }

    public void DeclineUsbDevice(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return;
        }
        lock (_declinedUsb)
        {
            _declinedUsb.Add(serial.Trim());
        }
    }

    /// <summary>
    /// The USB poll can no longer see a serial it used to (M15c B2). Measured on the Pixel 7:
    /// pulling the cable removes the row from `adb devices` in ~329 ms, with no stale entry and no
    /// `offline` reading left behind — so absence is a positive death signal roughly 15 s cheaper
    /// than waiting for three health probes to time out, and HealthInterval stays where it is.
    ///
    /// It is only that for a cable link. A wireless or direct-TLS link is not in `adb devices` as
    /// a USB row at all, so its absence there means nothing and must never drop it — hence the
    /// transport AND serial test below.
    /// </summary>
    private void OnUsbDeviceGone(string serial)
    {
        lock (_declinedUsb)
        {
            _declinedUsb.Remove(serial); // unplugged: a replug is a fresh offer, not a nag
        }
        lock (_unusableReported)
        {
            _unusableReported.Remove(serial);
        }
        if (State != LinkState.Connected || Device is not { } live)
        {
            return;
        }
        if (live.Transport != LinkTransport.AdbUsb)
        {
            return;
        }
        var matches = string.Equals(live.Serial, serial, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(live.SerialNo, serial, StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            return;
        }
        // M15c B4: logged the way M15a logs probe-based detection, so the next cable pull is
        // measurable from the log alone. Best case one poll interval (~3 s) plus the confirming
        // poll; worst case ~6 s, against the health loop's 6-21 s.
        log.Log(LogLevel.Warn,
            $"USB cable for {live.Model} is gone from ADB ({UsbWatcherService.MissesToDeclareGone} " +
            "consecutive 3 s polls, so 3-6 s after the pull); dropping the link now instead of " +
            "waiting for the health probe.");
        OnLinkDropped();
    }

    private void OnUsbDeviceSeen(DeviceData device)
    {
        // The phone became usable, so anything said about it while it wasn't is spent: forget it,
        // and a later visit to the same state speaks again instead of staying silent.
        lock (_unusableReported)
        {
            if (_unusableReported.Remove(device.Serial ?? "") && LastErrorMessage is not null)
            {
                log.Log(LogLevel.Info, $"{device.Model ?? "The phone"} has approved this PC.");
            }
        }
        if (!registry.AllowUsbConnections)
        {
            return;
        }
        var knownSerial = registry.PairedSerial;
        if (knownSerial is not null && device.Serial == knownSerial)
        {
            // The paired phone over USB: connect when idle, or switch to it from a lower-priority
            // link (wireless/TLS) — USB is the most reliable, lowest-latency transport (D-USB).
            // M13f §2: a transport that has not answered must lose its rank. The 3 s USB poll's
            // `device` state is adb's claim, and that claim outlives the cable (the adb server
            // keeps a stale entry), so while a live link is answering, USB must NOT preempt it —
            // that is the "still connected to USB long after the cable is gone" failure.
            if (State == LinkState.Connected && Device is { } current)
            {
                var currentAnswered = LastRttMs is not null;
                // M15a Part C: the user's hand-picked transport outranks the automatic pick, but
                // only while it is answering — ChooseWithOverride reports a dropped override and
                // the caption below says so rather than going quiet.
                var choice = TransportRank.ChooseWithOverride(
                [
                    new TransportCandidate(LinkTransport.AdbUsb.ToString(), TransportPriority(LinkTransport.AdbUsb),
                        Answered: currentAnswered && current.Transport == LinkTransport.AdbUsb),
                    new TransportCandidate(LinkTransport.AdbWireless.ToString(), TransportPriority(LinkTransport.AdbWireless),
                        Answered: currentAnswered && current.Transport == LinkTransport.AdbWireless),
                ], ManualTransport?.ToString());
                if (choice.OverrideDropped)
                {
                    ClearManualTransport("the transport you picked stopped answering");
                }
                TransportChosenByHand = choice.ByHand;
                if (choice.Name != LinkTransport.AdbUsb.ToString())
                {
                    return; // the current link has answered and USB has not — a stale USB entry loses its rank
                }
            }
            if (ShouldEngage(LinkTransport.AdbUsb) && !IsBackedOff(device.Serial))
            {
                _ = ConnectUsbAsync(device, manual: false);
            }
            return;
        }
        // An unknown phone. D-037 stands — one active link — but M15a A5 is that the limit has to
        // be VISIBLE. It used to return here in silence whenever anything was connected, which is
        // exactly what "plugging in a second phone does nothing at all" looked like from outside.
        var decision = DeviceAdmission.DecideDeviceSwitch(
            Device?.Serial, device.Serial ?? "", Activity(State));
        switch (decision.Action)
        {
            case DeviceSwitchAction.AlreadyConnected:
                return;
            case DeviceSwitchAction.Refuse:
            case DeviceSwitchAction.SwitchAfterDisconnect:
                // Never connect over a live link on our own initiative; tell the user what the
                // one-phone limit means here and let them choose. The confirmation card is the
                // "activate it" step A5 asks for, and ConnectUsbManualAsync tears the old link
                // down before bringing this one up.
                if (IsDeclined(device.Serial))
                {
                    return; // the user already said no to this phone; ask again when it is replugged
                }
                ReportOnce(device.Serial ?? "", decision.Message);
                UsbDeviceNeedsConfirmation?.Invoke(device);
                return;
        }
        if (_expectAny)
        {
            _ = ConnectUsbAsync(device, manual: false);
            return;
        }
        if (!IsDeclined(device.Serial))
        {
            UsbDeviceNeedsConfirmation?.Invoke(device);
        }
    }

    private bool IsDeclined(string? serial)
    {
        lock (_declinedUsb)
        {
            return serial is not null && _declinedUsb.Contains(serial);
        }
    }

    /// <summary>Say something about a phone at most once per distinct message (see the field).</summary>
    private void ReportOnce(string serial, string message)
    {
        lock (_unusableReported)
        {
            if (_unusableReported.TryGetValue(serial, out var already) && already == message)
            {
                return;
            }
            _unusableReported[serial] = message;
        }
        LastErrorMessage = message;
        ErrorRaised?.Invoke(message);
        log.Log(LogLevel.Info, message);
    }

    /// <summary>
    /// The one place <see cref="LinkState"/> becomes the pure <see cref="LinkActivity"/> the
    /// admission rules take — see the comment on that enum for why they are separate.
    /// </summary>
    private static LinkActivity Activity(LinkState state) => state switch
    {
        LinkState.Connected => LinkActivity.Connected,
        LinkState.Connecting => LinkActivity.Connecting,
        LinkState.Paused => LinkActivity.Paused,
        _ => LinkActivity.Idle,
    };

    /// <summary>
    /// Drop a hand-picked transport that can no longer be honoured, and say why. An override that
    /// disappears without a word is the failure Part C exists to prevent.
    /// </summary>
    private void ClearManualTransport(string reason)
    {
        if (ManualTransport is not { } was)
        {
            return;
        }
        ManualTransport = null;
        TransportChosenByHand = false;
        var message = $"Linc is choosing the connection automatically again — {reason}.";
        LastErrorMessage = message;
        ErrorRaised?.Invoke(message);
        log.Log(LogLevel.Warn, $"Manual transport {was} cleared: {reason}.");
        StateChanged?.Invoke();
    }

    private async Task ConnectAsync(string address, bool manual)
    {
        await _gate.WaitAsync();
        try
        {
            if (manual ? State is LinkState.Connecting : !ShouldEngage(LinkTransport.AdbWireless))
            {
                return;
            }
            if (State == LinkState.Connected)
            {
                StopHealthLoop(); // switching: drop the current lower-priority link first
            }
            SetState(LinkState.Connecting);
            try
            {
                Device = await connection.ConnectAsync(address, CancellationToken.None);
                if (!string.IsNullOrEmpty(Device.SerialNo))
                {
                    registry.SavePairedDevice(Device.SerialNo, Device.Model);
                }
                registry.SaveLastHostPort(address.Trim());
                _expectAny = false;
                _backoff.Remove(address);
                ConnectedAtUtc = DateTimeOffset.UtcNow;
                if (_hasConnectedOnce)
                {
                    ReconnectCount++;
                }
                _hasConnectedOnce = true;
                TransportChosenByHand = ManualTransport == Device.Transport;
                SetState(LinkState.Connected);
                StartHealthLoop();
                log.Log(LogLevel.Info, $"Connected to {Device.Model} ({address})");
            }
            catch (LincException ex)
            {
                var firstFailure = RegisterFailure(address);
                SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
                LastErrorMessage = ex.Message;
                if (manual || firstFailure)
                {
                    ErrorRaised?.Invoke(ex.Message);
                    log.Log(LogLevel.Error, $"Connect to {address} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The phone dialed in over Direct TLS (v9) — adopt it if nothing else is connected.
    /// <paramref name="peer"/> is that connection's remote endpoint (v18): on a hotspot link it
    /// is the phone's address on the link, and it is carried down to ConnectionManager because
    /// the socket is not reachable from a <c>Stream</c>.
    /// </summary>
    private void OnTlsControlArrived(System.IO.Stream stream, System.Net.IPEndPoint? peer)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                // TLS is the lowest-priority transport, so ShouldEngage adopts it only when idle
                // (never preempts ADB) and only when the preference permits Direct connections.
                if (!ShouldEngage(LinkTransport.DirectTls))
                {
                    stream.Dispose(); // first-wins (D-022); the phone will redial if needed
                    return;
                }
                SetState(LinkState.Connecting);
                try
                {
                    Device = await connection.AdoptTlsConnectionAsync(stream, peer, CancellationToken.None);
                    ConnectedAtUtc = DateTimeOffset.UtcNow;
                    if (_hasConnectedOnce)
                    {
                        ReconnectCount++;
                    }
                    _hasConnectedOnce = true;
                    TransportChosenByHand = ManualTransport == Device.Transport;
                    SetState(LinkState.Connected);
                    StartHealthLoop();
                    log.Log(LogLevel.Info, $"Connected to {Device.Model} (direct, no ADB)");
                }
                catch (Exception ex) when (ex is LincException or System.IO.IOException)
                {
                    stream.Dispose();
                    SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
                    LastErrorMessage = "A direct connection from the phone failed.";
                    // Unsolicited inbound failure: nothing else observes this, so log and raise it
                    // the same way the outbound connect paths do (A2.1 — was previously silent).
                    ErrorRaised?.Invoke(LastErrorMessage);
                    log.Log(LogLevel.Warn, $"Direct connection from the phone failed: {ex.Message}");
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task ConnectUsbAsync(DeviceData device, bool manual)
    {
        await _gate.WaitAsync();
        try
        {
            if (manual ? State is LinkState.Connecting : !ShouldEngage(LinkTransport.AdbUsb))
            {
                return;
            }
            if (State == LinkState.Connected)
            {
                StopHealthLoop(); // switching: drop the current lower-priority link first
            }
            SetState(LinkState.Connecting);
            try
            {
                Device = await connection.ConnectToUsbDeviceAsync(device, CancellationToken.None);
                if (!string.IsNullOrEmpty(Device.SerialNo))
                {
                    registry.SavePairedDevice(Device.SerialNo, Device.Model);
                }
                _expectAny = false;
                _backoff.Remove(device.Serial);
                ConnectedAtUtc = DateTimeOffset.UtcNow;
                if (_hasConnectedOnce)
                {
                    ReconnectCount++;
                }
                _hasConnectedOnce = true;
                TransportChosenByHand = ManualTransport == Device.Transport;
                SetState(LinkState.Connected);
                StartHealthLoop();
                log.Log(LogLevel.Info, $"Connected to {Device.Model} (USB)");
            }
            catch (LincException ex)
            {
                var firstFailure = RegisterFailure(device.Serial);
                SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
                LastErrorMessage = ex.Message;
                if (manual || firstFailure)
                {
                    ErrorRaised?.Invoke(ex.Message);
                    log.Log(LogLevel.Error, $"USB connect failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartHealthLoop()
    {
        StopHealthLoop();
        _healthCts = new CancellationTokenSource();
        _ = HealthLoopAsync(_healthCts.Token);
    }

    private void StopHealthLoop()
    {
        _healthCts?.Cancel();
        _healthCts?.Dispose();
        _healthCts = null;
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        // A single slow status probe is usually transient congestion, not a dead link — most
        // often the reverse mirror's video stream (channel 5) briefly saturating the shared ADB
        // forward tunnel so the control request/response can't get through in time. Dropping on
        // the first failure caused a reconnect every few minutes during mirroring, and each
        // reconnect made the phone re-issue pc.mirror.start, thrashing the hardware encoder. So
        // only give up after several *consecutive* failures, re-probing quickly to confirm a
        // real drop rather than waiting out the full interval.
        const int maxConsecutiveFailures = 3;
        var failures = 0;
        try
        {
            // Immediate first probe populates the dashboard right after connecting.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var status = await connection.GetStatusAsync(ct);
                    LastRttMs = (int)stopwatch.ElapsedMilliseconds;
                    failures = 0;
                    _firstProbeFailureUtc = null;
                    StatusUpdated?.Invoke(status);
                }
                catch (LincException)
                {
                    // Expected and routine below the threshold (see the reasoning above this loop);
                    // OnLinkDropped() logs and surfaces it once the streak actually confirms a drop.
                    // M15a Part B: stamp the first failure so the drop can report how long
                    // detection actually took, instead of the interval being inferred from source.
                    _firstProbeFailureUtc ??= DateTime.UtcNow;
                    if (++failures >= maxConsecutiveFailures)
                    {
                        OnLinkDropped();
                        return;
                    }
                    log.Log(LogLevel.Info,
                        $"Health probe {failures}/{maxConsecutiveFailures} failed " +
                        $"({(int)(DateTime.UtcNow - _firstProbeFailureUtc.Value).TotalMilliseconds} ms into the streak).");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct); // re-probe soon to confirm
                    continue;
                }
                await Task.Delay(HealthInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Loop stopped deliberately.
        }
    }

    private void OnLinkDropped()
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (State != LinkState.Connected)
                {
                    return;
                }
                var dying = Device?.Transport;
                StopHealthLoop();
                connection.Disconnect();
                Device = null;
                SetState(LinkState.Searching);
                LastErrorMessage = "Lost the connection to your phone.";
                ErrorRaised?.Invoke("Lost the connection to your phone. Linc will reconnect automatically when it sees the phone again.");
                // M15a Part B: the one number nobody had — how long from the first unanswered
                // probe to the link actually being marked dead. Logged on every real drop, so the
                // next measurement needs no instrumentation run, just the log.
                var confirmMs = _firstProbeFailureUtc is { } since
                    ? (int)(DateTime.UtcNow - since).TotalMilliseconds
                    : -1;
                _firstProbeFailureUtc = null;
                log.Log(LogLevel.Warn,
                    $"Connection dropped; will reconnect automatically. Detection took {confirmMs} ms " +
                    $"from the first failed probe (health poll {HealthInterval.TotalSeconds:0} s, " +
                    "3 failures to confirm, 3 s apart).");
                // A genuine link death is the one thing an override must not survive silently.
                if (dying is { } dead && ManualTransport == dead)
                {
                    ClearManualTransport($"{Describe(dead)} stopped answering");
                }
                // M15a Part B, the stage the measurement named. Detection is a bounded 6-21 s, but
                // what came after it was unbounded: a dropped link left the supervisor in Searching
                // with no trigger of its own, waiting for an mDNS advert that a pulled USB cable
                // never causes. Resume, network change and preference change all already recover by
                // nudging discovery and adopting whatever ADB already holds — a drop is at least as
                // strong a signal as any of them, and both calls are bounded and single-flighted.
                discovery.ScanNow();
                _ = RecoverPresentWirelessDeviceAsync();
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // The old link may be dead even though the socket hasn't noticed yet;
        // probe now instead of waiting out the health interval.
        if (State == LinkState.Connected)
        {
            _ = ProbeNowAsync();
        }
        // A network coming back while we're hunting is a strong "the phone may be reachable now"
        // signal — adopt the phone ADB already holds instead of waiting for an mDNS advert.
        else if (State == LinkState.Searching)
        {
            _ = RecoverPresentWirelessDeviceAsync();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }
        log.Log(LogLevel.Info, "PC woke from sleep; re-checking the phone connection.");
        _ = RecoverAfterResumeAsync();
    }

    /// <summary>
    /// After a resume the control socket is usually dead, but the 15 s health probe may not have
    /// noticed yet — and, worse, the phone frequently stops re-emitting its wireless-debugging
    /// mDNS advert even though the ADB server still holds it as a healthy wireless device. That
    /// combination stranded the supervisor in Searching for minutes (only BLE sightings logged),
    /// because discovery is the sole wireless reconnect trigger. So: verify the link now, then
    /// reconnect straight to the phone ADB already knows about rather than waiting for an advert
    /// that isn't coming.
    /// </summary>
    private async Task RecoverAfterResumeAsync()
    {
        if (State == LinkState.Connected)
        {
            await ProbeNowAsync(); // a dead link drops to Searching through the usual path
        }
        discovery.ScanNow(); // nudge mDNS too, in case the advert does return
        await RecoverPresentWirelessDeviceAsync();
    }

    /// <summary>
    /// Reconnect to the paired phone when the ADB server already lists it online as a wireless
    /// device — the reconnect path that no discovery advert or USB poll covers. Matches on the
    /// last host:port we connected through (that is exactly the wireless device's ADB serial).
    /// Bounded so a genuinely absent phone falls back to normal hunting, and single-flighted so
    /// overlapping triggers (resume + network change) don't stack.
    /// </summary>
    private async Task RecoverPresentWirelessDeviceAsync()
    {
        var address = registry.LastHostPort;
        if (string.IsNullOrEmpty(address) || !IsTransportAllowed(LinkTransport.AdbWireless) ||
            Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
        {
            return;
        }
        try
        {
            // A just-detected drop may still be settling into Searching (OnLinkDropped runs on a
            // background task), so give it a few seconds before giving up.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (State is LinkState.Connected or LinkState.Connecting)
                {
                    return;
                }
                if (State == LinkState.Searching && !IsBackedOff(address) &&
                    await connection.HasOnlineDeviceAsync(address, CancellationToken.None))
                {
                    await ConnectAsync(address, manual: false);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private async Task ProbeNowAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var status = await connection.GetStatusAsync(cts.Token);
            StatusUpdated?.Invoke(status);
        }
        catch (Exception ex) when (ex is LincException or OperationCanceledException)
        {
            // OnLinkDropped() logs and surfaces this — not silent, just not logged here directly.
            OnLinkDropped();
        }
    }

    private bool IsBackedOff(string address) =>
        _backoff.TryGetValue(address, out var entry) && DateTime.UtcNow < entry.NextAttemptUtc;

    /// <returns>True if this is the first failure in the current streak.</returns>
    private bool RegisterFailure(string address)
    {
        var failures = _backoff.TryGetValue(address, out var entry) ? entry.Failures + 1 : 1;
        var delay = BackoffSeconds[Math.Min(failures, BackoffSeconds.Length) - 1];
        _backoff[address] = (failures, DateTime.UtcNow.AddSeconds(delay));
        return failures == 1;
    }

    /// <summary>Higher wins: USB is preferred over wireless ADB, which is preferred over Direct TLS.</summary>
    private static int TransportPriority(LinkTransport transport) => transport switch
    {
        LinkTransport.AdbUsb => 3,
        LinkTransport.AdbWireless => 2,
        LinkTransport.DirectTls => 1,
        _ => 0,
    };

    /// <summary>Does the active phone's connection preference permit this transport?</summary>
    private bool IsTransportAllowed(LinkTransport transport) => registry.ConnectionPreference switch
    {
        ConnectionPreference.UsbOnly => transport == LinkTransport.AdbUsb,
        ConnectionPreference.WirelessOnly => transport == LinkTransport.AdbWireless,
        ConnectionPreference.DirectOnly => transport == LinkTransport.DirectTls,
        _ => true, // Auto
    };

    /// <summary>
    /// Should a connect (or a switch) to <paramref name="transport"/> begin now? True when idle,
    /// or when it strictly outranks the current link (auto-switch to a better transport). Always
    /// false when a connect is already in flight, when paused, or when the user's per-device
    /// preference forbids this transport.
    /// </summary>
    private bool ShouldEngage(LinkTransport transport)
    {
        if (!IsTransportAllowed(transport))
        {
            return false;
        }
        // M15a Part C: a hand-picked transport is honoured until it is cleared or dies, so nothing
        // else may take the link off it — including a higher-ranked one. That is the whole point
        // of "use wireless now" rather than waiting for ranking to agree.
        if (ManualTransport is { } pinned && transport != pinned)
        {
            return false;
        }
        return State switch
        {
            LinkState.Searching or LinkState.NoDevice => true,
            LinkState.Connected => Device is { } d && TransportPriority(transport) > TransportPriority(d.Transport),
            _ => false, // Connecting (in flight) or Paused
        };
    }

    /// <summary>
    /// The user changed the connection method under Devices. Drop the current link only if the new
    /// preference forbids its transport, then re-hunt so an allowed, available transport connects
    /// promptly (USB is covered by the 3 s poll; wireless/TLS get a discovery nudge).
    /// </summary>
    private void OnConnectionPreferenceChanged()
    {
        if (!_started)
        {
            return;
        }
        // Raised on the UI thread (the dropdown); hop off it so a slow in-flight connect holding
        // the gate can't freeze the UI.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (State == LinkState.Connected && Device is { } d && !IsTransportAllowed(d.Transport))
                {
                    StopHealthLoop();
                    connection.Disconnect();
                    Device = null;
                    _backoff.Clear();
                    LastRttMs = null;
                    SetState(LinkState.Searching);
                    log.Log(LogLevel.Info, "Connection method changed; switching to your preferred transport.");
                }
            }
            finally
            {
                _gate.Release();
            }
            discovery.ScanNow();
            await RecoverPresentWirelessDeviceAsync();
        });
    }

    private void SetState(LinkState newState)
    {
        if (State == newState)
        {
            return;
        }
        State = newState;
        StateChanged?.Invoke();
    }
}
