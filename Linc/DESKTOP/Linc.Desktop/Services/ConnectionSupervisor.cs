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
    void ExpectNextDevice(bool newPhoneOnly = false);

    /// <summary>The add-a-phone flow was closed: stop expecting, and resume normal reconnecting.</summary>
    void StopExpecting();

    void RequestDisconnect();
    void ResumeAutomatic();

    /// <summary>
    /// Something outside the link just saw the phone on the network (a Quick Share from it, for
    /// one). While searching, treat it like a resume: scan now and adopt whatever ADB already
    /// holds, instead of waiting out the idle delay. A no-op when connected or paused.
    /// </summary>
    void NudgeReconnect(string reason);
}

/// <summary>
/// The reconnection state machine and the ONE owner of which transport is live (D-081,
/// Vibe/agent/design/connection.md). Discovery adverts, the USB poll, the phone dialling in
/// and the hotspot standby only report; every decision is <see cref="LinkPolicy"/>'s, and every
/// trigger goes through <see cref="Nudge"/>. A 15 s health probe detects dead links; a better
/// transport takes over make-before-break; a cable pulled while Wi-Fi is around gets a grace
/// window to hand off before anything shows; per-endpoint backoff is cut short by real signals
/// and forgets endpoints that keep failing.
/// </summary>
public sealed class ConnectionSupervisor(
    IConnectionManager connection,
    IDiscoveryService discovery,
    IUsbWatcherService usbWatcher,
    IDeviceRegistry registry,
    ITlsTransportService tlsTransport,
    IBlePresenceService blePresence,
    IHotspotLinkService hotspotLink,
    ILogService log) : IConnectionSupervisor
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EndpointBackoff _backoff = new();
    private CancellationTokenSource? _healthCts;
    private CancellationTokenSource? _clockCts;

    /// <summary>
    /// Set while a better link that just died (the cable came out) is being replaced (D-081):
    /// State stays Connected until this passes, so a hand-off to Wi-Fi never shows as a drop.
    /// </summary>
    private DateTime? _handoffUntilUtc;
    private int _handoffGeneration;
    private bool _expectAny;

    /// <summary>
    /// The add-a-phone flow is adding a NEW phone (D-079): while set, phones this PC already knows
    /// are not adopted — over USB, Wi-Fi discovery or a direct dial-in — so a known phone that
    /// happens to be nearby can't win the race and be reported as the one just added.
    /// </summary>
    private bool _expectNewOnly;
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

    /// <summary>Serials the USB poll currently sees, so a cable going in is noticed once (D-081).</summary>
    private readonly HashSet<string> _onCable = new(StringComparer.OrdinalIgnoreCase);

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
        Nudge(LinkSignal.UserAsked, $"You picked {Describe(transport.Value)}");
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
        StartClockWatch();
        discovery.Start();
        usbWatcher.Start();
        blePresence.Start(); // hint only, and a no-op on a PC without Bluetooth (M04, D-034)
        if (registry.BackgroundConnectionEnabled)
        {
            tlsTransport.Start(); // standing presence: advertise + listen (D-014/D-022)
        }
        SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
        Nudge(LinkSignal.AppStart, "Linc started");
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
            _expectNewOnly = false;
            lock (_backoff)
            {
                _backoff.Clear(); // a different phone shouldn't inherit the old one's penalty box
            }
            _handoffUntilUtc = null;
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
        Nudge(LinkSignal.UserAsked, "Switched phones");
    }

    public Task ConnectManualAsync(string hostPort) => ConnectAsync(hostPort, manual: true);
    public Task ConnectUsbManualAsync(DeviceData device) => ConnectUsbAsync(device, manual: true);

    public void ExpectNextDevice(bool newPhoneOnly = false)
    {
        _expectAny = true;
        _expectNewOnly = newPhoneOnly;
        if (State is LinkState.NoDevice or LinkState.Paused)
        {
            SetState(LinkState.Searching);
        }
    }

    public void StopExpecting()
    {
        _expectAny = false;
        _expectNewOnly = false;
        ResumeAutomatic();
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
            _expectNewOnly = false;
            _handoffUntilUtc = null;
            SetState(LinkState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void NudgeReconnect(string reason) => Nudge(LinkSignal.UserAsked, reason);

    public void ResumeAutomatic()
    {
        if (State == LinkState.Paused)
        {
            SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
            Nudge(LinkSignal.UserAsked, "Reconnecting automatically again");
        }
    }

    /// <summary>
    /// The one path every "the phone may be reachable now" signal takes (D-081). A strong signal
    /// cuts every backoff wait short; a live link is probed when the network changed or the PC
    /// woke; while hunting (or handing off), discovery is woken and whatever the adb server
    /// already holds is adopted. Cheap and single-flighted, so callers never need to debounce.
    /// </summary>
    private void Nudge(LinkSignal signal, string detail)
    {
        if (!_started)
        {
            return;
        }
        if (LinkPolicy.IsStrong(signal))
        {
            lock (_backoff)
            {
                _backoff.CutWaits();
            }
        }
        if (signal == LinkSignal.Resume)
        {
            // Multicast membership doesn't reliably survive standby: say "I'm here" again so the
            // phone's mDNS browse finds this PC without waiting for its next refresh.
            tlsTransport.RefreshAdvert();
        }
        if (State == LinkState.Connected && !IsHandingOff)
        {
            if (LinkPolicy.ShouldProbeLiveLink(signal))
            {
                log.Log(LogLevel.Info, $"{detail}; checking the phone is still there.");
                _ = ProbeNowAsync();
            }
            return;
        }
        if (State is not (LinkState.Searching or LinkState.NoDevice or LinkState.Connected))
        {
            return; // paused or mid-connect
        }
        if (signal is not (LinkSignal.BleSighting or LinkSignal.ProbeFailing))
        {
            log.Log(LogLevel.Info, $"{detail}; looking for the phone now.");
        }
        discovery.ScanNow();
        _ = RecoverPresentWirelessDeviceAsync();
    }

    private bool IsHandingOff => _handoffUntilUtc is { } until && DateTime.UtcNow < until;


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
        _clockCts?.Cancel();
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
        // A phone this PC has connected before is recognised from its advert even when it is not
        // the active tab — but only adopted while idle, never over a live link.
        var returning = !matchesPairedPhone && DeviceAdmission.MayAdoptKnownPhone(Activity(State))
            ? DeviceAdmission.RecogniseKnown(service.InstanceName, KnownSerials(), contains: true)
            : null;
        if (!_expectAny && !matchesPairedPhone && returning is null)
        {
            return;
        }
        if (_expectNewOnly && (matchesPairedPhone ||
            DeviceAdmission.RecogniseKnown(service.InstanceName, KnownSerials(), contains: true) is not null))
        {
            return; // adding a new phone (D-079): a known one nearby must not win the race
        }
        if (returning is not null)
        {
            log.Log(LogLevel.Info, $"Recognised {ModelOf(returning)} on Wi-Fi — a phone this PC already knows; reconnecting.");
        }
        var address = $"{service.IpAddress}:{service.Port}";
        if (IsBackedOff(address))
        {
            return;
        }
        _ = ConnectAsync(address, manual: false, knownPhone: returning is not null);
    }

    /// <summary>
    /// The paired phone's Bluetooth beacon was heard, so it is physically here. That is not a
    /// way to connect — no data rides BLE (D-034) — it just means a Wi-Fi scan is worth doing
    /// right now instead of after the idle delay. Fires about once a second while in range,
    /// hence the cheap guard.
    /// </summary>
    private void OnBleSighting(string serial)
    {
        if (State is not (LinkState.Searching or LinkState.NoDevice) ||
            DeviceAdmission.RecogniseKnown(serial, KnownSerials()) is null)
        {
            return;
        }
        Nudge(LinkSignal.BleSighting, "The phone is nearby (Bluetooth)");
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
        lock (_onCable)
        {
            _onCable.Remove(serial);
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
        log.Log(LogLevel.Info,
            $"USB cable for {live.Model} is gone from ADB ({UsbWatcherService.MissesToDeclareGone} " +
            "consecutive 3 s polls, so 3-6 s after the pull); handing the link to Wi-Fi if the " +
            "phone is there, without waiting for the health probe.");
        OnLinkDropped(LinkSignal.CableOut);
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
        if (device.Serial is { } plugged)
        {
            bool fresh;
            lock (_onCable)
            {
                fresh = _onCable.Add(plugged);
            }
            if (fresh)
            {
                // The poll reports a plugged phone every 3 s; only its arrival is a signal.
                log.Log(LogLevel.Info, $"{device.Model ?? "A phone"} is on the USB cable.");
                Nudge(LinkSignal.CableIn, "The cable went in");
            }
        }
        var knownSerial = registry.PairedSerial;
        if (_expectNewOnly && DeviceAdmission.RecogniseKnown(device.Serial, KnownSerials()) is not null)
        {
            return; // adding a new phone (D-079): known phones wait
        }
        if (knownSerial is not null && device.Serial == knownSerial)
        {
            // The paired phone over USB: connect when idle, or switch to it from a lower-priority
            // link (wireless/TLS) — USB is the most reliable, lowest-latency transport (D-USB).
            // M13f §2: a transport that has not answered must lose its rank. The 3 s USB poll's
            // `device` state is adb's claim, and that claim outlives the cable (the adb server
            // keeps a stale entry), so while a live link is answering, USB must NOT preempt it —
            // that is the "still connected to USB long after the cable is gone" failure.
            if (State == LinkState.Connected && !IsHandingOff && Device is { } current)
            {
                var currentAnswered = LastRttMs is not null;
                // M15a Part C: the user's hand-picked transport outranks the automatic pick, but
                // only while it is answering — ChooseWithOverride reports a dropped override and
                // the caption below says so rather than going quiet.
                var choice = TransportRank.ChooseWithOverride(
                [
                    new TransportCandidate(LinkTransport.AdbUsb.ToString(), LinkPolicy.Rank(LinkTransport.AdbUsb),
                        Answered: currentAnswered && current.Transport == LinkTransport.AdbUsb),
                    new TransportCandidate(current.Transport.ToString(), LinkPolicy.Rank(current.Transport),
                        Answered: currentAnswered && current.Transport != LinkTransport.AdbUsb),
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
        // A phone this PC already knows, just not the active tab. Idle: connect it straight away —
        // asking "connect this phone?" about a phone the user has used here before is exactly
        // the "treated as new" complaint. Busy: fall through to the card, which names it.
        if (DeviceAdmission.RecogniseKnown(device.Serial, KnownSerials()) is { } returning &&
            DeviceAdmission.MayAdoptKnownPhone(Activity(State)))
        {
            // A phone whose tab the user closed stays closed (D-079): plugging it in doesn't
            // re-adopt it behind their back. The add-a-phone flow (expecting) or picking it does.
            if (registry.IsHidden(returning) && !_expectAny)
            {
                return;
            }
            if (!IsBackedOff(device.Serial ?? ""))
            {
                log.Log(LogLevel.Info, $"Recognised {ModelOf(returning)} over USB — a phone this PC already knows; connecting.");
                _ = ConnectUsbAsync(device, manual: false, knownPhone: true);
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

    private async Task ConnectAsync(string address, bool manual, bool knownPhone = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (manual ? State is LinkState.Connecting : !ShouldEngage(LinkTransport.AdbWireless))
            {
                return;
            }
            if (knownPhone && !DeviceAdmission.MayAdoptKnownPhone(EngageActivity()))
            {
                return; // something connected while this waited for the gate; never switch phones on our own
            }
            var handoff = BeginAttempt();
            try
            {
                var device = await connection.ConnectAsync(address, CancellationToken.None);
                if (!string.IsNullOrEmpty(device.SerialNo))
                {
                    registry.SavePairedDevice(device.SerialNo, device.Model);
                }
                registry.SaveLastHostPort(address.Trim());
                Succeeded(address);
                Adopt(device, handoff, address);
            }
            catch (LincException ex)
            {
                var firstFailure = Failed(address);
                ForgetIfStale(address);
                EndFailedAttempt(handoff);
                LastErrorMessage = ex.Message;
                if (manual || firstFailure)
                {
                    if (handoff is null)
                    {
                        ErrorRaised?.Invoke(ex.Message);
                    }
                    log.Log(LogLevel.Warn, $"Connect to {address} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Where an attempt starts from. Nothing live: show Connecting. A live link (a better transport
    /// is taking over) or one being handed off: show nothing — the old link keeps working until the
    /// new one is installed (make-before-break, D-081). Returns the transport being replaced, if any.
    /// </summary>
    private LinkTransport? BeginAttempt()
    {
        if (!LinkPolicy.AttemptIsVisible(Activity(State), IsHandingOff) && Device is { } live)
        {
            return live.Transport;
        }
        SetState(LinkState.Connecting);
        return null;
    }

    /// <summary>A new link is live: install it as a fresh connect or a silent handoff.</summary>
    private void Adopt(ConnectedDevice device, LinkTransport? replacing, string how)
    {
        var handedOff = replacing is not null;
        var wasHandingOff = IsHandingOff;
        Device = device;
        _expectAny = false;
        _expectNewOnly = false;
        _handoffUntilUtc = null;
        TransportChosenByHand = ManualTransport == device.Transport;
        if (!handedOff)
        {
            ConnectedAtUtc = DateTimeOffset.UtcNow;
            if (_hasConnectedOnce)
            {
                ReconnectCount++;
            }
            _hasConnectedOnce = true;
        }
        _firstProbeFailureUtc = null;
        StartHealthLoop();
        if (handedOff)
        {
            log.Log(LogLevel.Info, $"Moved the link to {Describe(device.Transport)} without dropping it" +
                (wasHandingOff ? " (after the cable came out)." : "."));
            StateChanged?.Invoke(); // still Connected; the transport (and its icon) changed
        }
        else
        {
            SetState(LinkState.Connected);
            log.Log(LogLevel.Info, $"Connected to {device.Model} ({how})");
        }
    }

    /// <summary>
    /// An attempt failed. Started from nothing: back to hunting. Started beside a live link: that
    /// link is untouched and nothing is shown — the whole point of make-before-break.
    /// </summary>
    private void EndFailedAttempt(LinkTransport? replacing)
    {
        if (replacing is null)
        {
            SetState(registry.PairedSerial is null ? LinkState.NoDevice : LinkState.Searching);
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
                lock (_backoff)
                {
                    _backoff.CutWaits(); // the phone is here: nothing should be waiting out a backoff
                }
                // Any known phone can now authenticate (TlsTransportService), so say which one
                // this is before adopting it: the adopt reads model/serial from the active record.
                var dialler = IdentifyTlsPhone(stream);
                if (_expectNewOnly)
                {
                    stream.Dispose(); // only known phones dial in directly; adding a new one (D-079)
                    return;
                }
                if (dialler is { } closed && closed.Serial != registry.PairedSerial &&
                    registry.IsHidden(closed.Serial) && !_expectAny)
                {
                    stream.Dispose(); // its tab was closed (D-079); the phone backs off and retries later
                    return;
                }
                if (dialler is { } phone && phone.Serial != registry.PairedSerial)
                {
                    log.Log(LogLevel.Info, $"Recognised {phone.Model} dialling in directly — a phone this PC already knows.");
                    registry.SavePairedDevice(phone.Serial, phone.Model);
                }
                var handoff = BeginAttempt();
                try
                {
                    var device = await connection.AdoptTlsConnectionAsync(stream, peer, CancellationToken.None);
                    Adopt(device, handoff, "direct, no ADB");
                }
                catch (Exception ex) when (ex is LincException or System.IO.IOException)
                {
                    stream.Dispose();
                    EndFailedAttempt(handoff);
                    LastErrorMessage = "A direct connection from the phone failed.";
                    // Unsolicited inbound failure: nothing else observes this, so log and raise it
                    // the same way the outbound connect paths do (A2.1 — was previously silent).
                    if (handoff is null)
                    {
                        ErrorRaised?.Invoke(LastErrorMessage);
                    }
                    log.Log(LogLevel.Warn, $"Direct connection from the phone failed: {ex.Message}");
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task ConnectUsbAsync(DeviceData device, bool manual, bool knownPhone = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (manual ? State is LinkState.Connecting : !ShouldEngage(LinkTransport.AdbUsb))
            {
                return;
            }
            if (knownPhone && !DeviceAdmission.MayAdoptKnownPhone(EngageActivity()))
            {
                return; // something connected while this waited for the gate; never switch phones on our own
            }
            // A different phone on the cable (the user confirmed the switch) is not a handoff:
            // the old phone's link goes first, as before.
            var samePhone = Device is { } live &&
                (string.Equals(live.SerialNo, device.Serial, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(live.Serial, device.Serial, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(registry.PairedSerial, device.Serial, StringComparison.OrdinalIgnoreCase));
            if (State == LinkState.Connected && !samePhone)
            {
                StopHealthLoop();
                connection.Disconnect();
                Device = null;
                SetState(LinkState.Searching);
            }
            var handoff = BeginAttempt();
            try
            {
                var connected = await connection.ConnectToUsbDeviceAsync(device, CancellationToken.None);
                if (!string.IsNullOrEmpty(connected.SerialNo))
                {
                    registry.SavePairedDevice(connected.SerialNo, connected.Model);
                }
                Succeeded(device.Serial);
                Adopt(connected, handoff, "USB");
            }
            catch (LincException ex)
            {
                var firstFailure = Failed(device.Serial);
                EndFailedAttempt(handoff);
                LastErrorMessage = ex.Message;
                if (manual || firstFailure)
                {
                    if (handoff is null)
                    {
                        ErrorRaised?.Invoke(ex.Message);
                    }
                    log.Log(LogLevel.Warn, $"USB connect failed: {ex.Message}");
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
                    if (failures == 0)
                    {
                        // Preemptive: the link may be dying, so start finding the next one NOW
                        // rather than after the 6 s it takes to confirm. A fresh mDNS scan is
                        // cheap and harmless if the link recovers; if it doesn't, the adverts and
                        // the standby are already in hand when OnLinkDropped goes looking.
                        discovery.ScanNow();
                    }
                    if (++failures >= maxConsecutiveFailures)
                    {
                        OnLinkDropped(LinkSignal.LinkDropped);
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

    /// <summary>
    /// The live link died. When it was the cable (<see cref="LinkSignal.CableOut"/>) and the phone
    /// may still be on Wi-Fi, hand off first: State stays Connected for
    /// <see cref="LinkPolicy.HandoffGrace"/> while wireless ADB is tried and the phone — whose
    /// control socket just died — dials in directly. Only if nothing lands is it a drop.
    /// </summary>
    private void OnLinkDropped(LinkSignal why)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (State != LinkState.Connected || IsHandingOff)
                {
                    return;
                }
                StopHealthLoop();
                if (LinkPolicy.OnLiveLinkLost(why, IsWifiPossible()) == LostLink.HandOff)
                {
                    var generation = ++_handoffGeneration;
                    _handoffUntilUtc = DateTime.UtcNow + LinkPolicy.HandoffGrace;
                    LastRttMs = null;
                    _ = FinishHandoffAsync(generation);
                }
                else
                {
                    DropLink();
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }
            Nudge(LinkSignal.CableOut, "The cable came out");
        });
    }

    /// <summary>Wi-Fi is worth waiting for when this phone has ever been reachable without a cable.</summary>
    private bool IsWifiPossible() =>
        registry.ConnectionPreference != ConnectionPreference.UsbOnly &&
        (registry.PhoneCertBase64 is not null || RecoveryCandidates().Count > 0);

    private async Task FinishHandoffAsync(int generation)
    {
        await Task.Delay(LinkPolicy.HandoffGrace);
        await _gate.WaitAsync();
        try
        {
            if (generation != _handoffGeneration || _handoffUntilUtc is null || State != LinkState.Connected)
            {
                return; // a link landed, or something else changed the state meanwhile
            }
            log.Log(LogLevel.Info, $"No Wi-Fi link took over within {LinkPolicy.HandoffGrace.TotalSeconds:0} s.");
            DropLink();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The visible drop, under the gate: back to Searching and tell the user.</summary>
    private void DropLink()
    {
        var dying = Device?.Transport;
        StopHealthLoop();
        connection.Disconnect();
        Device = null;
        _handoffUntilUtc = null;
        LastRttMs = null;
        SetState(LinkState.Searching);
        LastErrorMessage = "Lost the connection to your phone.";
        ErrorRaised?.Invoke("Lost the connection to your phone. Linc will reconnect automatically when it sees the phone again.");
        // M15a Part B: the one number nobody had — how long from the first unanswered probe to
        // the link actually being marked dead. Logged on every real drop, so the next
        // measurement needs no instrumentation run, just the log.
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
        // M15a Part B: a drop is at least as strong a signal as resume or a network change —
        // look for the phone straight away instead of waiting for an advert.
        _ = Task.Run(() => Nudge(LinkSignal.LinkDropped, "The link dropped"));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Nudge(LinkSignal.NetworkChanged, "The PC's network changed");

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Nudge(LinkSignal.Resume, "PC woke from sleep");
        }
    }

    /// <summary>
    /// A 5 s tick that notices when it was held up for much longer — the PC was asleep (D-081).
    /// PowerModeChanged never reaches a desktop app on a Modern Standby PC (it never fired once in
    /// the owner's logs), but a stalled timer is visible on every PC.
    /// </summary>
    private void StartClockWatch()
    {
        _clockCts = new CancellationTokenSource();
        var ct = _clockCts.Token;
        _ = Task.Run(async () =>
        {
            var last = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ClockTick, ct);
                }
                catch (OperationCanceledException)
                {
                    return; // the supervisor was disposed; the watch ends with it
                }
                var now = DateTime.UtcNow;
                if (LinkPolicy.LooksLikeResume(now - last, ClockTick))
                {
                    Nudge(LinkSignal.Resume, $"PC woke from sleep (asleep about {(now - last).TotalSeconds:0} s)");
                }
                last = now;
            }
        }, ct);
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
        var candidates = RecoveryCandidates();
        if (candidates.Count == 0 || !IsTransportAllowed(LinkTransport.AdbWireless) ||
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
                if ((State == LinkState.Connected && !IsHandingOff) || State == LinkState.Connecting)
                {
                    return;
                }
                if (State == LinkState.Searching || IsHandingOff)
                {
                    foreach (var address in candidates)
                    {
                        if (!IsBackedOff(address) &&
                            await connection.HasOnlineDeviceAsync(address, CancellationToken.None))
                        {
                            await ConnectAsync(address, manual: false);
                            if (State == LinkState.Connected && !IsHandingOff)
                            {
                                return;
                            }
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    /// <summary>
    /// Wireless endpoints worth trying, best first. The hotspot standby comes first: its health
    /// loop keeps it connected and identity-verified in the ADB server while another transport
    /// carries the link, so a cable pull can fail over to it at once instead of waiting for an
    /// mDNS advert (measured 2026-09-25: the standby was up while recovery only knew the old
    /// ephemeral mDNS port). The last host:port is the fallback it always was.
    /// </summary>
    private List<string> RecoveryCandidates()
    {
        var list = new List<string>(2);
        foreach (var address in new[] { hotspotLink.LinkedEndpoint, registry.LastHostPort })
        {
            if (!string.IsNullOrWhiteSpace(address) &&
                !list.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase) &&
                !IsStale(address.Trim()))
            {
                list.Add(address.Trim());
            }
        }
        return list;
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
            // OnLinkDropped logs and surfaces this — not silent, just not logged here directly.
            OnLinkDropped(LinkSignal.LinkDropped);
        }
    }

    private IEnumerable<string> KnownSerials() => registry.KnownDevices.Select(d => d.Serial);

    private string ModelOf(string serial) =>
        registry.KnownDevices.FirstOrDefault(d => d.Serial == serial)?.Model ?? "your phone";

    /// <summary>Which known phone's pinned certificate the inbound TLS stream presented, if any.</summary>
    private KnownDevice? IdentifyTlsPhone(System.IO.Stream stream)
    {
        if (stream is not System.Net.Security.SslStream { RemoteCertificate: { } cert })
        {
            return null;
        }
        var raw = cert.GetRawCertData();
        return registry.KnownDevices.FirstOrDefault(d =>
            DeviceAdmission.MatchesAny(raw, DeviceAdmission.PinnedCertificates([d.PhoneCertBase64])));
    }

    private bool IsBackedOff(string address)
    {
        lock (_backoff)
        {
            return _backoff.IsWaiting(address, DateTime.UtcNow);
        }
    }

    private int Failures(string address)
    {
        lock (_backoff)
        {
            return _backoff.Failures(address);
        }
    }

    private bool IsStale(string address)
    {
        lock (_backoff)
        {
            return _backoff.IsStale(address);
        }
    }

    /// <returns>True if this is the first failure in the current streak.</returns>
    private bool Failed(string address)
    {
        lock (_backoff)
        {
            return _backoff.Failed(address, DateTime.UtcNow);
        }
    }

    private void Succeeded(string address)
    {
        lock (_backoff)
        {
            _backoff.Succeeded(address);
        }
    }

    /// <summary>
    /// An address that failed <see cref="EndpointBackoff.ForgetAfter"/> times in a row is almost
    /// always an old one (a new DHCP lease, a new wireless-debugging port): stop offering it.
    /// </summary>
    private void ForgetIfStale(string address)
    {
        if (!IsStale(address))
        {
            return;
        }
        if (string.Equals(registry.LastHostPort?.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            registry.SaveLastHostPort(null);
        }
        log.Log(LogLevel.Info, $"Forgot {address}: it failed {EndpointBackoff.ForgetAfter} times in a row, so it is probably an old address.");
    }

    /// <summary>
    /// What the admission rules should treat the link as: while a dead cable is being handed off,
    /// the phone is effectively idle — anything that answers may take over.
    /// </summary>
    private LinkActivity EngageActivity() => IsHandingOff ? LinkActivity.Idle : Activity(State);

    /// <summary>Does the active phone's connection preference permit this transport?</summary>
    private bool IsTransportAllowed(LinkTransport transport) =>
        LinkPolicy.IsAllowed(transport, registry.ConnectionPreference);

    /// <summary>
    /// Should a connect (or a handoff to a better transport) begin now? <see cref="LinkPolicy"/>
    /// decides: idle, or strictly better than the live link, within the phone's preference and
    /// never away from a hand-picked transport.
    /// </summary>
    private bool ShouldEngage(LinkTransport transport) =>
        LinkPolicy.ShouldEngage(transport, EngageActivity(),
            IsHandingOff ? null : Device?.Transport, ManualTransport, registry.ConnectionPreference);

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
                    lock (_backoff)
                    {
                        _backoff.Clear();
                    }
                    LastRttMs = null;
                    SetState(LinkState.Searching);
                    log.Log(LogLevel.Info, "Connection method changed; switching to your preferred transport.");
                }
            }
            finally
            {
                _gate.Release();
            }
            Nudge(LinkSignal.PreferenceChanged, "Connection method changed");
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
