using System.Reflection;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public sealed record ConnectedDevice(
    string Model, string Serial, string SerialNo, CompanionHello Companion,
    LinkTransport Transport = LinkTransport.AdbWireless)
{
    /// <summary>
    /// The phone's own identity for everything stored per phone (D-078): its hardware serial, the
    /// same key the device registry uses — not <see cref="Serial"/>, which is whatever adb calls
    /// this connection (USB serial, IPv4/IPv6 host:port, mDNS name) and changes with the route.
    /// Keyed by route, one phone became four cached "phones", re-downloading every app icon each time.
    /// </summary>
    public string CacheKey => string.IsNullOrEmpty(SerialNo) ? Serial : SerialNo;
}

public interface IConnectionManager
{
    ConnectedDevice? Current { get; }

    /// <summary>ADB-level device handle for services that run their own commands (file sync etc.).</summary>
    DeviceData? RawDevice { get; }

    /// <summary>Unsolicited phone → desktop protocol messages. May fire on background threads.</summary>
    event Action<Protocol.Envelope>? CompanionMessageReceived;

    /// <summary>
    /// Fire-and-forget PC → phone push (v13: `pc.media.state`, `share.incoming`).
    /// Silently no-ops when disconnected or on a pre-v13 link.
    /// </summary>
    Task SendToPhoneAsync(Protocol.Envelope envelope, CancellationToken ct);

    Task SendClipboardAsync(string text, CancellationToken ct);
    Task SendNotificationDismissAsync(string key, CancellationToken ct);

    /// <summary>Fetches an app's icon (PNG bytes) over the v5 bulk channel; null when unavailable or on a pre-v5 link.</summary>
    Task<byte[]?> FetchAppIconAsync(string packageName, CancellationToken ct);

    /// <summary>Fetches any bulk resource by kind/id (largeIcon, albumArt…); null when unavailable.</summary>
    Task<byte[]?> FetchBulkAsync(string kind, string id, CancellationToken ct);

    /// <summary>
    /// Opens a channel the DESKTOP serves, by asking the phone to dial back (v14, M05's
    /// pc-video channel). Unlike <c>ChannelOpener</c> this never dials outward: over LAN the
    /// phone reaches the TLS listener directly, and over the cable it reaches that same
    /// listener through D-022's <c>adb reverse</c>. Returns the stream positioned after the
    /// channel header.
    /// </summary>
    Task<Stream> OpenPhoneDialledChannelAsync(int channel, CancellationToken ct);

    /// <summary>Fires a notification action's PendingIntent on the phone (v6).</summary>
    Task SendNotificationActionAsync(string key, int index, CancellationToken ct);

    /// <summary>Fills a notification action's RemoteInput and fires it (v6 inline reply).</summary>
    Task SendNotificationReplyAsync(string key, int index, string text, CancellationToken ct);

    /// <summary>Sends a media transport control (play/pause/next/prev/seek) to the phone (v6).</summary>
    Task SendMediaControlAsync(string action, long? positionMs, CancellationToken ct);

    /// <summary>Toggles the phone's locate ring (v7).</summary>
    Task LocateDeviceAsync(CancellationToken ct);

    /// <summary>Turns the phone's Do Not Disturb on or off (v7; needs the phone-side grant).</summary>
    Task SetDndAsync(bool enabled, CancellationToken ct);

    /// <summary>Sets the phone's ringer mode: normal / vibrate / silent (v8; same grant as DND).</summary>
    Task SetSoundModeAsync(string mode, CancellationToken ct);

    /// <summary>
    /// True when the ADB server already lists <paramref name="serial"/> as an online device.
    /// After a resume the paired phone often stays a healthy wireless device here while its
    /// mDNS advert goes quiet — this is how the supervisor spots that phone when discovery can't.
    /// </summary>
    Task<bool> HasOnlineDeviceAsync(string serial, CancellationToken ct);

    Task<ConnectedDevice> ConnectAsync(string hostPort, CancellationToken ct);

    /// <summary>Connect to a device ADB already knows about (USB) — skips `adb connect` entirely.</summary>
    Task<ConnectedDevice> ConnectToUsbDeviceAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Adopts an authenticated inbound Direct-TLS control connection (v9). No ADB involved.
    /// <paramref name="peer"/> is that connection's remote endpoint (v18), or null when unknown.
    /// </summary>
    /// <param name="pcDialled">v32: the PC dialled the phone's LincLink listener for this link, so
    /// channels are dialled to the phone too, instead of asking the phone to dial back through a
    /// firewall that may block it.</param>
    Task<ConnectedDevice> AdoptTlsConnectionAsync(
        System.IO.Stream stream, System.Net.IPEndPoint? peer, CancellationToken ct, bool pcDialled = false);

    /// <summary>
    /// v32: the transport under the live session died; the session is kept for a resume (the
    /// supervisor lingers and races for another route). Never raised below v32.
    /// </summary>
    event Action? TransportLost;

    /// <summary>v32: the phone said where it can be dialled (its addresses and LincLink port).</summary>
    event Action<IReadOnlyList<string>, int>? PhoneWhere;

    /// <summary>True when the live link is a v32 session a new transport can resume.</summary>
    bool HasResumableSession { get; }

    /// <summary>
    /// The address of the far end of the CURRENT control connection, or null when the link is
    /// not one whose peer address means anything (v18). Populated for Direct TLS only: on an
    /// ADB link the socket is a loopback forward, so its peer is 127.0.0.1 and naming that as
    /// "the phone" would be worse than having nothing — hence PROTOCOL.md v18's `addrs`
    /// fallback and the same-subnet filter that guards it.
    /// </summary>
    string? PeerAddress { get; }

    /// <summary>True when the link runs over ADB — mirror/screenshot need this.</summary>
    bool HasAdb { get; }

    /// <summary>Opens the files channel (2) and returns its stream (v10 — Direct TLS file transfer).</summary>
    Task<System.IO.Stream> OpenFileChannelAsync(CancellationToken ct);

    /// <summary>Recent photos from the phone (v10).</summary>
    Task<IReadOnlyList<PhotoItem>> GetRecentPhotosAsync(int limit, CancellationToken ct);

    /// <summary>The phone's launchable-app inventory (v16, D-058). Gate on
    /// <see cref="AppsPayload.IsSupported"/> — a v&#8804;15 phone never answers.</summary>
    Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken ct);

    /// <summary>Opens a URL on the phone (v10 continue-on-phone).</summary>
    Task ContinueUrlAsync(string url, CancellationToken ct);

    /// <summary>Enable/disable Sync-page lanes on the phone (v11).</summary>
    Task SyncConfigAsync(System.Text.Json.Nodes.JsonObject config, CancellationToken ct);

    /// <summary>Recent SMS from the phone (v11, Messages lane).</summary>
    Task<IReadOnlyList<SmsMessage>> SmsListAsync(int limit, CancellationToken ct);

    /// <summary>Send an SMS via the phone (v11).</summary>
    Task SmsSendAsync(string address, string body, CancellationToken ct);

    /// <summary>Recent calls from the phone (v12).</summary>
    Task<IReadOnlyList<CallEntry>> CallLogAsync(int limit, CancellationToken ct);

    /// <summary>Place a call from the phone (v12).</summary>
    Task CallDialAsync(string number, CancellationToken ct);

    /// <summary>Decline/end the current call (v12).</summary>
    Task CallDeclineAsync(CancellationToken ct);

    /// <summary>v15 (D-055): set the phone's screen rotation.</summary>
    Task SetDisplayRotationAsync(string mode, CancellationToken ct);

    /// <summary>v15 (D-055): set the phone's brightness.</summary>
    Task SetDisplayBrightnessAsync(bool auto, int? level, CancellationToken ct);

    Task<DeviceStatus> GetStatusAsync(CancellationToken ct);
    void Disconnect();
}

/// <summary>
/// Connects to a paired phone over ADB — wireless (adb connect) or USB (already
/// visible to the ADB server) — then does device health checks, port-forward to the
/// companion socket, and protocol handshake. Every failure mode maps to a
/// plain-language message (docs/CONTRIBUTING.md §Errors speak human).
/// </summary>
public sealed class ConnectionManager(
    IAdbServerHost adbServerHost,
    ILogService log,
    IDeviceRegistry registry,
    ITlsTransportService tls,
    IPhoneSetupService setup,
    LinkSecretStore secrets) : IConnectionManager
{
    private const string LincPackageName = "app.linc.android";

    private readonly AdbClient _adb = new();
    private CompanionClient? _companion;

    public bool HasAdb => RawDevice is not null;

    public ConnectedDevice? Current { get; private set; }
    public DeviceData? RawDevice { get; private set; }

    public string? PeerAddress { get; private set; }

    /// <summary>Last reason a v13 push was suppressed, so it is logged once rather than per push.</summary>
    private string? _suppressedPushReason;

    public event Action<Protocol.Envelope>? CompanionMessageReceived;

    public event Action? TransportLost;
    public event Action<IReadOnlyList<string>, int>? PhoneWhere;

    public bool HasResumableSession => _companion is { HasLinkSession: true };

    /// <summary>A new protocol client, offering v32 only while the new connection engine is on.</summary>
    private CompanionClient NewClient() => new()
    {
        PcId = tls.PcId,
        MaxVersion = registry.LincLinkEnabled ? ProtocolConstants.Version : LinkProtocol.Version - 1,
    };

    /// <summary>
    /// The session a new transport should offer to resume (v32), if there is one and it belongs to
    /// <paramref name="phoneId"/> — the phone this transport reaches. Another phone never hears of it.
    /// </summary>
    private CompanionClient? Resumable(string? phoneId) =>
        registry.LincLinkEnabled && phoneId is not null && _companion is { HasLinkSession: true } live && live.PhoneLinkId == phoneId
            ? live
            : null;

    /// <summary>The LincLink id of the phone at the other end of a TLS stream: the hash of the certificate it presented.</summary>
    private static string? PhoneIdOf(System.IO.Stream stream) =>
        stream is System.Net.Security.SslStream { RemoteCertificate: { } cert }
            ? LinkSecretStore.PhoneIdOf(Convert.ToBase64String(cert.GetRawCertData()))
            : null;

    public async Task<bool> HasOnlineDeviceAsync(string serial, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return false;
        }
        try
        {
            await adbServerHost.EnsureRunningAsync(ct);
            var devices = await _adb.GetDevicesAsync(ct);
            return devices.Any(d => d.Serial == serial && d.State == DeviceState.Online);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // ADB server hiccup: treat as "not present" and let normal hunting continue
        }
    }

    public async Task<ConnectedDevice> ConnectAsync(string hostPort, CancellationToken ct)
    {
        // No Disconnect() first (D-081): the live link, if any, keeps working until the new one
        // has finished its handshake — Install swaps them. A failed attempt changes nothing.
        await adbServerHost.EnsureRunningAsync(ct);

        var (host, port) = ParseHostPort(hostPort);
        string result;
        try
        {
            result = await _adb.ConnectAsync(host, port, ct);
        }
        catch (Exception ex) when (ex is not LincException and not OperationCanceledException)
        {
            // Expected and routine: wrap the raw adb-client exception into the plain-language
            // message and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            throw new LincException(CannotReachPhone, ex);
        }
        if (result.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            throw new LincException(CannotReachPhone, new IOException(result));
        }

        var serial = $"{host}:{port}";
        var devices = await _adb.GetDevicesAsync(ct);
        var device = devices.FirstOrDefault(d => d.Serial == serial)
            ?? throw new LincException(CannotReachPhone);
        EnsureOnline(device);

        return await FinishConnectAsync(device, serial, ct);
    }

    public async Task<ConnectedDevice> ConnectToUsbDeviceAsync(DeviceData device, CancellationToken ct)
    {
        await adbServerHost.EnsureRunningAsync(ct);
        EnsureOnline(device);
        return await FinishConnectAsync(device, device.Serial, ct);
    }

    public async Task<ConnectedDevice> AdoptTlsConnectionAsync(
        System.IO.Stream stream, System.Net.IPEndPoint? peer, CancellationToken ct, bool pcDialled = false)
    {
        var companion = NewClient();
        var phoneId = PhoneIdOf(stream);
        try
        {
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            var hello = await companion.ConnectAsync(stream, appVersion, ct, resume: Resumable(phoneId));
            // v32: a resumed hello carries on the live session on this new transport.
            var live = companion.ResumedInto ?? companion;
            var phoneCert = registry.PhoneCertBase64;
            if (pcDialled && peer is not null && phoneCert is not null)
            {
                // The PC reached the phone, so the PC dials its channels too (v32): the phone's
                // dial-back would have to come in through this PC's firewall.
                var phone = new System.Net.IPEndPoint(peer.Address, LinkProtocol.PhonePort);
                live.ChannelOpener = (channel, token, openCt) => tls.DialPhoneChannelAsync(phone, phoneCert, channel, token, openCt);
            }
            else
            {
                // Typed channels over TLS: the phone dials back on request (v9 channel.open).
                live.ChannelOpener = async (channel, token, openCt) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(openCt);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var arrival = tls.AwaitChannelAsync(channel, token, timeout.Token);
                    await live.RequestChannelOpenAsync(channel, timeout.Token);
                    return await arrival;
                };
            }
            if (!hello.Resumed)
            {
                live.MessageReceived += envelope => CompanionMessageReceived?.Invoke(envelope);
                if (hello.V >= 6)
                {
                    foreach (var topic in new[] { Topic.Status, Topic.Notifications, Topic.Clipboard, Topic.Media })
                    {
                        await live.SubscribeAsync(topic, ct);
                    }
                }
            }
            // No ADB on this link: mirror/files/screenshot unavailable (M17).
            var current = Install(live, new ConnectedDevice(
                Model: registry.PairedModel ?? "Android phone",
                Serial: registry.PairedSerial ?? "direct",
                SerialNo: registry.PairedSerial ?? "",
                Companion: hello,
                Transport: LinkTransport.DirectTls), rawDevice: null, peer?.Address.ToString(), phoneId, pcDialled);
            log.Log(LogLevel.Info, hello.Resumed
                ? $"The session moved to a direct Wi-Fi link ({(pcDialled ? "this PC dialled the phone" : "the phone dialled in")}); nothing was lost."
                : pcDialled ? "Connected to the phone directly — this PC dialled it, no ADB involved"
                : "Connected to the phone directly — no ADB involved");
            return current;
        }
        catch
        {
            // Expected and routine: clean up the half-open companion before propagating the
            // failure — the caller (ConnectionSupervisor) logs and surfaces it.
            companion.Dispose();
            throw;
        }
    }

    /// <summary>Everything after "we have an Online DeviceData" — shared by the wireless and USB paths.</summary>
    private async Task<ConnectedDevice> FinishConnectAsync(DeviceData device, string serial, CancellationToken ct)
    {
        var model = await GetPropAsync(device, "ro.product.model", "Android phone", ct);
        // Hardware serial: it appears inside the phone's mDNS instance name
        // (adb-<serialno>-XXXXXX), which is what lets discovery auto-match this phone.
        // Over USB, the ADB serial is typically this same value directly.
        var serialNo = await GetPropAsync(device, "ro.serialno", "", ct);

        // Make THIS phone the active record before anything below writes per-device state. The
        // TLS exchange saves the phone's certificate to the active device, and the supervisor
        // used to switch the active device only after the whole connect returned — so connecting
        // a second phone pinned its certificate onto the first phone's record, and the first
        // phone then looked "reinstalled" (logged 9 times before this fix). SavePairedDevice
        // dedupes on the serial, so a returning phone updates its record instead of adding one.
        if (!string.IsNullOrEmpty(serialNo))
        {
            registry.SavePairedDevice(serialNo, model);
        }

        // Install the companion and grant its permissions if this phone has never had it
        // (M01, D-035). No-ops in a few milliseconds once the package is present.
        await setup.EnsureReadyAsync(device, ct);

        int localPort;
        try
        {
            localPort = await _adb.CreateForwardAsync(
                device, "tcp:0", $"localabstract:{ProtocolConstants.SocketName}", allowRebind: true, ct);
        }
        catch (Exception ex) when (ex is not LincException and not OperationCanceledException)
        {
            // Expected and routine: wrap the raw adb-client exception into the plain-language
            // message and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            throw new LincException(
                "Linc connected to the phone but couldn't open its data channel. " +
                "Turn Wireless debugging off and on again on the phone, then retry.", ex);
        }

        // ADB authenticated this phone, and the record made active above holds its pinned certificate.
        var (companion, hello) = await ReachCompanionAsync(device, localPort, Resumable(LinkSecretStore.PhoneIdOf(registry.PhoneCertBase64)), ct);
        // v32: a resumed hello carries on the live session on this new transport.
        var shell = companion;
        companion = companion.ResumedInto ?? companion;
        try
        {
            if (!hello.Resumed)
            {
                companion.MessageReceived += envelope => CompanionMessageReceived?.Invoke(envelope);
            }
            var selfTest = false;
            var transport = AdbSerial.IsNetwork(serial) ? LinkTransport.AdbWireless : LinkTransport.AdbUsb;
            if (hello.V >= 9)
            {
                // TLS pairing (D-022): trust-on-first-use over the authenticated ADB link.
                //
                // Exchanged on EVERY ADB connect, not only when nothing is pinned. The phone's
                // certificate lives in the Android KeyStore, which is wiped when the app is
                // uninstalled — so a reinstall silently invalidates whatever this PC has pinned,
                // and Direct TLS then fails forever with no error anywhere (the desktop drops
                // unknown certificates silently by design). M01 made reinstalls routine, since
                // the desktop now installs the companion itself. PROTOCOL.md already specifies
                // re-sending as the way to replace a pin, and the ADB link is authenticated, so
                // re-pinning here costs one round trip and keeps both sides honest.
                try
                {
                    var phoneCert = await companion.ExchangeTlsAsync(
                        tls.CertificateBase64, tls.TlsPort, tls.ReversePort, ct);
                    if (phoneCert is not null && !string.IsNullOrEmpty(serialNo))
                    {
                        // A phone paired by QR code first now has a hardware serial: one record.
                        registry.MergeQrPairedPhone(serialNo, phoneCert);
                    }
                    if (phoneCert is not null && phoneCert != registry.PhoneCertBase64)
                    {
                        var replacing = registry.PhoneCertBase64 is not null;
                        registry.SavePhoneCert(phoneCert);
                        log.Log(LogLevel.Info, replacing
                            ? "The phone has a new identity (it was reinstalled); re-paired for direct connections."
                            : "Paired with the phone for direct connections");
                    }
                }
                catch (LincException)
                {
                    // TLS pairing is best-effort; ADB keeps working regardless.
                }
                // adb reverse: lets the phone dial this PC through adb itself (D-022) — over the
                // cable, and over Wireless debugging too, where it rides adb's own connection.
                // Without it a Wi-Fi link left the phone dialling this PC's LAN address, which
                // Windows Firewall blocks on most networks: every channel back (PC Remote's
                // video, the terminal, files) failed with "couldn't open that channel back".
                if (transport is LinkTransport.AdbUsb or LinkTransport.AdbWireless)
                {
                    try
                    {
                        await _adb.CreateReverseForwardAsync(
                            device, $"tcp:{tls.ReversePort}", $"tcp:{tls.TlsPort}", allowRebind: true, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A bonus path (the LAN address is still tried); say so for the logs.
                        log.Log(LogLevel.Info, $"Couldn't set up the phone's way back through adb ({ex.Message}); it will try this PC's network address");
                    }
                }
            }
            if (hello.V >= 6 && !hello.Resumed)
            {
                // v6 pub/sub (D-019): the phone withholds events until subscribed. Subscribe
                // before returning so the notification backlog and media state aren't missed.
                // A resumed session (v32) is still subscribed.
                foreach (var topic in new[] { Topic.Status, Topic.Notifications, Topic.Clipboard, Topic.Media })
                {
                    await companion.SubscribeAsync(topic, ct);
                }
            }
            if (hello.V >= 5 && !hello.Resumed)
            {
                // M12 proof: exercise the bulk channel once, concurrently with the control
                // connection, so the pipeline is verifiable on hardware (Logs page). Best-effort.
                selfTest = true;
            }
            var current = Install(companion, new ConnectedDevice(model, serial, serialNo, hello, transport), device, peer: null,
                phoneId: LinkSecretStore.PhoneIdOf(registry.PhoneCertBase64)); // after the exchange above re-pinned it
            if (selfTest)
            {
                _ = RunBulkSelfTestAsync();
            }
            return current;
        }
        catch
        {
            // Expected and routine: clean up the half-open companion before propagating the
            // failure — the caller (ConnectionSupervisor) logs and surfaces it. A resumed session
            // isn't this attempt's to dispose: only the new transport's shell is.
            shell.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The phone's Linc app answering on the forwarded socket. Not answering (just installed, a
    /// reboot, a crash, a force-stop) is repaired here: start it over ADB, then, with the new
    /// engine on, keep probing every half second for up to 15 s instead of one fixed 2 s wait and
    /// one more try (the old path, kept exactly when the engine is off). Each step's time is
    /// logged: "USB fails the first time" has no proven cause yet, and these lines will show it.
    /// </summary>
    private async Task<(CompanionClient Client, CompanionHello Hello)> ReachCompanionAsync(
        DeviceData device, int localPort, CompanionClient? resume, CancellationToken ct)
    {
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var engine = registry.LincLinkEnabled;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var started = false;
        var attempts = 0;
        Exception? first = null;
        while (true)
        {
            attempts++;
            var companion = NewClient();
            string why;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (engine)
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(3)); // a loopback forward answers in milliseconds
                }
                var hello = await companion.ConnectAsync(localPort, appVersion, attempt.Token, resume);
                if (started)
                {
                    log.Log(LogLevel.Info, engine
                        ? $"Linc on the phone answered {clock.ElapsedMilliseconds} ms after Linc started it over ADB (attempt {attempts})."
                        : "The companion app wasn't running; Linc started it on the phone.");
                }
                return (companion, hello);
            }
            catch (PhoneBusyException)
            {
                companion.Dispose();
                throw; // it's answering, just with another PC: nothing to repair
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is LincException or OperationCanceledException)
            {
                companion.Dispose();
                first ??= ex;
                why = ex is OperationCanceledException ? "no answer within 3 s" : "nothing listening yet";
                if (!engine && started)
                {
                    throw; // the old path: one start, one more try
                }
            }
            if (!started)
            {
                // The companion isn't answering. We hold an ADB link, so start it ourselves rather
                // than asking the user to unlock the phone and tap a button (M01, D-035).
                if (engine)
                {
                    log.Log(LogLevel.Info, $"Linc on the phone isn't answering ({why}, {clock.ElapsedMilliseconds} ms in); starting it over ADB.");
                }
                var ok = await TryStartCompanionAsync(device, ct, settle: !engine);
                started = true;
                if (!ok && !engine)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first!); // the old path, unchanged
                }
                continue;
            }
            if (clock.Elapsed > LinkBrain.AppProbeBudget)
            {
                log.Log(LogLevel.Warn, $"Linc on the phone still isn't answering after {clock.Elapsed.TotalSeconds:0} s ({attempts} tries, last: {why}); trying again shortly.");
                throw new CompanionNotAnsweringException();
            }
            await Task.Delay(LinkBrain.AppProbeEvery, ct);
        }
    }

    /// <summary>Fetches the phone's own Linc icon over the bulk channel and logs the result. Never throws.</summary>
    private async Task RunBulkSelfTestAsync()
    {
        try
        {
            var icon = await FetchAppIconAsync(LincPackageName, CancellationToken.None);
            log.Log(icon is { Length: > 0 } ? LogLevel.Info : LogLevel.Warn, icon is { Length: > 0 }
                ? $"Pipeline: bulk channel OK — fetched the phone's Linc icon ({icon.Length} bytes) over a second concurrent connection."
                : "Pipeline: bulk channel returned no data for the self-test icon.");
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Pipeline: bulk channel self-test couldn't run ({ex.Message}).");
        }
    }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.GetStatusAsync(ct);
    }

    public Task SendClipboardAsync(string text, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.SetClipboardAsync(text, ct);
    }

    public Task SendNotificationDismissAsync(string key, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.DismissNotificationAsync(key, ct);
    }

    public Task<byte[]?> FetchAppIconAsync(string packageName, CancellationToken ct) =>
        FetchBulkAsync("appIcon", packageName, ct);

    public Task<System.IO.Stream> OpenFileChannelAsync(CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.OpenChannelAsync(2, ct);
    }

    public Task<IReadOnlyList<PhotoItem>> GetRecentPhotosAsync(int limit, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.GetRecentPhotosAsync(limit, ct);
    }

    public Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.GetAppsAsync(ct);
    }

    public Task ContinueUrlAsync(string url, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.ContinueUrlAsync(url, ct);
    }

    public async Task SendToPhoneAsync(Protocol.Envelope envelope, CancellationToken ct)
    {
        var companion = _companion;
        if (companion is null || Current is not { Companion.V: >= 13 })
        {
            // Say so once per reason. A silent no-op here looks exactly like a broken feature
            // from the phone's side — "the widget never updates" with nothing in any log.
            var reason = companion is null
                ? "no phone connected"
                : $"the phone negotiated v{Current?.Companion.V} (needs 13)";
            if (_suppressedPushReason != reason)
            {
                _suppressedPushReason = reason;
                log.Log(LogLevel.Warn, $"Not sending '{envelope.Type}' to the phone: {reason}.");
            }
            return;
        }
        _suppressedPushReason = null;
        try
        {
            await companion.SendAsync(envelope, ct);
        }
        catch (LincException)
        {
            // The link is dying; the supervisor handles reconnect.
        }
    }

    public Task SyncConfigAsync(System.Text.Json.Nodes.JsonObject config, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.SyncConfigAsync(config, ct);
    }

    public Task<IReadOnlyList<SmsMessage>> SmsListAsync(int limit, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.SmsListAsync(limit, ct);
    }

    public Task SmsSendAsync(string address, string body, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.SmsSendAsync(address, body, ct);
    }

    public Task<IReadOnlyList<CallEntry>> CallLogAsync(int limit, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.CallLogAsync(limit, ct);
    }

    public Task CallDialAsync(string number, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.CallDialAsync(number, ct);
    }

    public Task CallDeclineAsync(CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.CallDeclineAsync(ct);
    }

    /// <summary>
    /// v15 (D-055): set the phone's screen rotation. Throws <see cref="LincException"/>
    /// with the phone's plain-language refusal when WRITE_SETTINGS is not granted
    /// (caller surfaces that text — never re-words it).
    /// </summary>
    public Task SetDisplayRotationAsync(string mode, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.SetDisplayRotationAsync(mode, ct);
    }

    /// <summary>
    /// v15 (D-055): set the phone's brightness. [auto]=true ignores [level]; otherwise
    /// [level] is the 0–100 percentage the phone will scale to its own panel range.
    /// </summary>
    public Task SetDisplayBrightnessAsync(bool auto, int? level, CancellationToken ct)
    {
        var companion = _companion ?? throw new LincException("Not connected to a phone right now.");
        return companion.SetDisplayBrightnessAsync(auto, level, ct);
    }

    public Task<byte[]?> FetchBulkAsync(string kind, string id, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.FetchBulkAsync(kind, id, ct);
    }

    public async Task<Stream> OpenPhoneDialledChannelAsync(int channel, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        var token = companion.SessionToken
            ?? throw new LincException("Not connected to a phone right now.");

        if (PcDialled)
        {
            // v32: this PC reached the phone's listener, so it dials the channel there too.
            return await companion.OpenChannelAsync(channel, ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        // Arm the waiter BEFORE asking, or a phone that dials back quickly arrives before
        // anything is registered to receive it.
        var arrival = tls.AwaitChannelAsync(channel, token, timeout.Token);
        await companion.RequestChannelOpenAsync(channel, timeout.Token);
        return await arrival;
    }

    public Task SendNotificationActionAsync(string key, int index, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.FireNotificationActionAsync(key, index, ct);
    }

    public Task SendNotificationReplyAsync(string key, int index, string text, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.SendNotificationReplyAsync(key, index, text, ct);
    }

    public Task SendMediaControlAsync(string action, long? positionMs, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.SendMediaControlAsync(action, positionMs, ct);
    }

    public Task LocateDeviceAsync(CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.LocateDeviceAsync(ct);
    }

    public Task SetDndAsync(bool enabled, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.SetDndAsync(enabled, ct);
    }

    public Task SetSoundModeAsync(string mode, CancellationToken ct)
    {
        var companion = _companion
            ?? throw new LincException("Not connected to a phone right now.");
        return companion.SetSoundModeAsync(mode, ct);
    }

    /// <summary>
    /// Make the freshly handshaken <paramref name="companion"/> the live link and only then close
    /// the old one (D-081): a handoff from Wi-Fi to USB, or the reverse, never leaves a moment
    /// with nothing connected. The phone keeps both control connections and talks to the newest.
    /// </summary>
    private ConnectedDevice Install(CompanionClient companion, ConnectedDevice current, DeviceData? rawDevice, string? peer, string? phoneId, bool pcDialled = false)
    {
        var old = _companion;
        var fresh = !ReferenceEquals(old, companion);
        if (fresh)
        {
            // v32: the session's own events, only while it is the live one.
            companion.TransportLost += () =>
            {
                if (ReferenceEquals(_companion, companion))
                {
                    RawDevice = null; // the transport that died is the one the device described
                    PeerAddress = null;
                    TransportLost?.Invoke();
                }
            };
            companion.PhoneWhere += (addresses, port) =>
            {
                if (ReferenceEquals(_companion, companion))
                {
                    PhoneWhere?.Invoke(addresses, port);
                }
            };
        }
        _companion = companion;
        Current = current;
        RawDevice = rawDevice;
        PeerAddress = peer;
        PcDialled = pcDialled;
        if (old is not null && fresh)
        {
            old.Dispose();
        }
        // v32: the transport may have died, or the phone said where it listens, before the
        // handlers above existed. Neither may be lost: report both now, off this call.
        var deadAlready = companion.MarkAdopted();
        if (fresh && companion.LastWhere is { } where)
        {
            PhoneWhere?.Invoke(where.Addresses, where.Port);
        }
        if (deadAlready && fresh) // (a resumed session reported its own loss: it was adopted already)
        {
            _ = Task.Run(() =>
            {
                if (ReferenceEquals(_companion, companion))
                {
                    RawDevice = null;
                    PeerAddress = null;
                    TransportLost?.Invoke();
                }
            });
        }
        _ = ShareLinkSecretAsync(companion, phoneId);
        return current;
    }

    /// <summary>True when the live link is one the PC dialled to the phone's LincLink listener (v32).</summary>
    public bool PcDialled { get; private set; }

    /// <summary>
    /// v32: make sure the phone holds this pair's link secret (it keys the UDP beacon). Sent once,
    /// and again only when the phone reports a different generation. Best-effort: without it the
    /// phone is still found by every other route.
    /// </summary>
    private async Task ShareLinkSecretAsync(CompanionClient companion, string? phoneId)
    {
        // The secret is chosen by the phone this PC knows is at the other end (its pinned
        // certificate), never by the id the phone states; a mismatch gets nothing.
        if (!registry.LincLinkEnabled || !companion.HasLinkSession || phoneId is null || companion.PhoneLinkId != phoneId)
        {
            return;
        }
        try
        {
            var (secret, gen) = secrets.Ensure(phoneId);
            if (companion.PhoneSecretGen == gen)
            {
                return;
            }
            await companion.ShareLinkSecretAsync(Convert.ToBase64String(secret), gen, CancellationToken.None);
            log.Log(LogLevel.Info, "Gave the phone this PC's link key, so the two can find each other on any Wi-Fi.");
        }
        catch (LincException)
        {
            // Next connect tries again.
        }
    }

    public void Disconnect()
    {
        _companion?.Dispose();
        _companion = null;
        Current = null;
        RawDevice = null;
        PcDialled = false;
        // v18: a peer address outlives nothing. Keeping it past the connection it described is
        // exactly the stale-address failure `gen` exists to prevent, one layer lower down.
        PeerAddress = null;
    }

    private static void EnsureOnline(DeviceData device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                return;
            case DeviceState.Unauthorized:
                throw new LincException(
                    "The phone hasn't approved this PC yet. This usually means the two aren't " +
                    "paired — pair them once from the phone's Wireless debugging screen, or tap " +
                    "\"Allow\" on the phone if it's asking about USB debugging.");
            case DeviceState.Offline:
                throw new LincException(
                    "The phone shows as offline. Turn Wireless debugging off and on again " +
                    "on the phone (or unplug and replug the USB cable), then retry.");
            default:
                throw new LincException(
                    "The phone isn't ready to talk to this PC. Check that Wireless debugging " +
                    "is still on and both devices are on the same Wi-Fi network, or that the USB " +
                    "cable is connected.");
        }
    }

    /// <summary>
    /// Starts the phone's companion service over ADB (M01, D-035). Returns true when the start
    /// was accepted, so the caller can retry the handshake.
    ///
    /// The service must be exported for this to work — `am` refuses a non-exported component
    /// with "Requires permission not exported from uid ...", which is exactly why every install
    /// or reboot used to need a manual tap on the phone.
    /// </summary>
    private async Task<bool> TryStartCompanionAsync(DeviceData device, CancellationToken ct, bool settle = true)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(
                $"am start-foreground-service -n {ProtocolConstants.CompanionPackage}/{ProtocolConstants.CompanionService}",
                device, receiver, ct);
            var output = receiver.ToString();
            if (output.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                log.Log(LogLevel.Warn, "Couldn't start the companion app on the phone automatically.");
                return false;
            }
            // The service needs a moment to bind its socket before the handshake can land. The new
            // engine probes for it instead (ReachCompanionAsync), so it waits only as long as needed.
            if (settle)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"Couldn't start the companion app on the phone: {ex.Message}");
            return false;
        }
    }

    private async Task<string> GetPropAsync(DeviceData device, string prop, string fallback, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync($"getprop {prop}", device, receiver, ct);
            var value = receiver.ToString().Trim();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback; // informational only — never fail the connect for this
        }
    }

    private static (string Host, int Port) ParseHostPort(string hostPort)
    {
        var parts = hostPort.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var port) && port is > 0 and < 65536)
        {
            return (parts[0], port);
        }
        throw new LincException(
            "That address doesn't look right. Enter it as shown on the phone's Wireless " +
            "debugging screen — for example 192.168.1.23:37099.");
    }

    private const string CannotReachPhone =
        "Linc couldn't reach the phone at that address. Check that Wireless debugging is on, " +
        "the address matches the phone's Wireless debugging screen (it changes sometimes), " +
        "and both devices are on the same Wi-Fi network.";
}
