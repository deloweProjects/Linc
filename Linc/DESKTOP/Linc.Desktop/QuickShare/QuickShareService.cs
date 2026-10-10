using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Linc.Desktop.Services;
using Makaretu.Dns;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using Zeroconf;

namespace Linc.Desktop.QuickShare;

public enum QsDirection { Incoming, Outgoing }

public enum QsState { WaitingForAnswer, Transferring, Done, Declined, Failed }

/// <summary>One transfer as the UI lists it. Replaced (not mutated) on every change.</summary>
public sealed record QsTransfer(
    string Id,
    QsDirection Direction,
    string PeerName,
    string Title,
    string Pin,
    QsState State,
    long Bytes,
    long TotalBytes,
    string? Message = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<QsReceivedText>? Texts = null)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)Bytes / TotalBytes, 0, 1) : 0;
}

/// <summary>An incoming offer waiting for the person at the PC to say yes or no.</summary>
public sealed class QsIncomingOffer
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Id { get; init; }
    public required string SenderName { get; init; }
    public required string Pin { get; init; }
    public required IReadOnlyList<string> Items { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>True when the sender's name matches a phone paired with Linc (a hint, not proof).</summary>
    public bool FromKnownPhone { get; init; }

    public void Accept() => _decision.TrySetResult(true);
    public void Decline() => _decision.TrySetResult(false);
    /// <summary>Completes when someone answers — toast, dialog or page — so the others can close.</summary>
    public Task<bool> Decided => _decision.Task;
}

/// <summary>A Quick Share receiver found on the network.</summary>
public sealed record QsNearbyDevice(string Id, string Name, QsWire.DeviceType Type, IPEndPoint Endpoint);

public interface IQuickShareService : IDisposable
{
    /// <summary>Whether this PC shows up in nearby Android phones' Quick Share sheet.</summary>
    bool ReceiveEnabled { get; }

    /// <summary>The name nearby phones see.</summary>
    string DeviceName { get; }

    /// <summary>Where received files land.</summary>
    string ReceivedFolder { get; }

    IReadOnlyList<QsTransfer> Transfers { get; }
    IReadOnlyList<QsNearbyDevice> Nearby { get; }
    bool IsScanning { get; }

    /// <summary>A sender wants to send something. The UI must Accept or Decline (times out as a decline).</summary>
    event Action<QsIncomingOffer>? IncomingOffer;

    event Action<QsTransfer>? TransferChanged;
    event Action? NearbyChanged;

    void Start();
    void SetReceiveEnabled(bool enabled);
    void SetDeviceName(string name);

    /// <summary>
    /// Look for nearby Quick Share receivers for <paramref name="duration"/>. Sends the Bluetooth
    /// beacon that makes nearby Android phones start advertising, then browses the network.
    /// </summary>
    Task ScanAsync(TimeSpan duration, CancellationToken ct);

    Task SendAsync(QsNearbyDevice target, IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Sends a piece of text or a link; the phone offers Open for a link, Copy for text.</summary>
    Task SendTextAsync(QsNearbyDevice target, string text, CancellationToken ct);

    /// <summary>
    /// Skip the prompt for a Quick Share from the phone Linc is connected to - only when the
    /// sender is at the SAME network address as that live Linc link and carries its name (see
    /// <see cref="QsTrust"/>). Off by default.
    /// </summary>
    bool AutoAcceptOwnPhone { get; }
    void SetAutoAcceptOwnPhone(bool enabled);

    /// <summary>Files handed over from outside the page (Explorer's "Send with Quick Share").</summary>
    IReadOnlyList<string> QueuedFiles { get; }
    event Action? QueuedFilesChanged;
    void QueueFiles(IEnumerable<string> paths);
    void ClearQueuedFiles();

    /// <summary>Toast button presses (Accept/Decline) land here.</summary>
    void HandleNotificationArguments(IDictionary<string, string> arguments);
}

/// <summary>
/// Makes this PC a Quick Share device, like Google's Quick Share for Windows: receive from any
/// Android phone nearby (no Linc needed on it), and send to them. Wi-Fi LAN medium only; both
/// ends must be on the same network. Everything after the handshake is end-to-end encrypted
/// (UKEY2 + AES-CBC/HMAC), and every incoming transfer needs an explicit Accept.
/// </summary>
public sealed class QuickShareService(
    IDeviceRegistry registry,
    IConnectionSupervisor supervisor,
    IConnectionManager connection,
    ILogService log) : IQuickShareService
{
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadvertiseCooldown = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, QsTransfer> _transfers = new();
    private readonly ConcurrentDictionary<string, QsIncomingOffer> _pendingOffers = new();
    private readonly ConcurrentDictionary<string, QsNearbyDevice> _nearby = new();
    private readonly string _endpointId = QsWire.NewEndpointId();
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listenCts;
    private ServiceDiscovery? _advertiser;
    private DateTime _lastAdvertiseUtc = DateTime.MinValue;
    private bool _started;
    private int _scanning;
    private Settings _settings = new(true, null);
    private readonly List<string> _queued = [];

    public bool AutoAcceptOwnPhone => _settings.AutoAcceptOwnPhone;

    public void SetAutoAcceptOwnPhone(bool enabled)
    {
        _settings = _settings with { AutoAcceptOwnPhone = enabled };
        SaveSettings();
        log.Log(LogLevel.Info, enabled
            ? "Quick Share: files from your connected phone will be accepted without asking."
            : "Quick Share: every transfer asks first again.");
    }

    public IReadOnlyList<string> QueuedFiles
    {
        get { lock (_queued) { return _queued.ToList(); } }
    }

    public event Action? QueuedFilesChanged;

    public void QueueFiles(IEnumerable<string> paths)
    {
        lock (_queued)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path) && !_queued.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _queued.Add(path);
                }
            }
        }
        QueuedFilesChanged?.Invoke();
    }

    public void ClearQueuedFiles()
    {
        lock (_queued) { _queued.Clear(); }
        QueuedFilesChanged?.Invoke();
    }

    public bool ReceiveEnabled => _settings.ReceiveEnabled;
    public string DeviceName => string.IsNullOrWhiteSpace(_settings.DeviceName) ? DefaultName() : _settings.DeviceName!;

    public string ReceivedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Quick Share");

    public IReadOnlyList<QsTransfer> Transfers => _transfers.Values.OrderByDescending(t => t.Id).ToList();
    public IReadOnlyList<QsNearbyDevice> Nearby => _nearby.Values.OrderBy(d => d.Name).ToList();
    public bool IsScanning => Volatile.Read(ref _scanning) != 0;

    public event Action<QsIncomingOffer>? IncomingOffer;
    public event Action<QsTransfer>? TransferChanged;
    public event Action? NearbyChanged;

    private string SettingsPath => Path.Combine(registry.RootPath, "quickshare.json");

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _settings = LoadSettings();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        if (ReceiveEnabled)
        {
            StartReceiving();
        }
    }

    public void SetReceiveEnabled(bool enabled)
    {
        if (enabled == ReceiveEnabled)
        {
            return;
        }
        _settings = _settings with { ReceiveEnabled = enabled };
        SaveSettings();
        if (enabled)
        {
            StartReceiving();
        }
        else
        {
            StopReceiving();
            log.Log(LogLevel.Info, "Quick Share: this PC is no longer visible to nearby phones.");
        }
    }

    public void SetDeviceName(string name)
    {
        var trimmed = name.Trim();
        _settings = _settings with { DeviceName = trimmed.Length == 0 || trimmed == DefaultName() ? null : trimmed };
        SaveSettings();
        if (ReceiveEnabled)
        {
            Advertise(force: true);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        StopReceiving();
    }

    // =========================================================================================
    // Receiving
    // =========================================================================================

    private void StartReceiving()
    {
        lock (_lock)
        {
            if (_listener is not null)
            {
                return;
            }
            try
            {
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                _listener = null;
                log.Log(LogLevel.Warn, $"Quick Share couldn't start listening, so phones won't see this PC: {ex.Message}");
                return;
            }
            _listenCts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _listenCts.Token);
        }
        Advertise(force: true);
    }

    private void StopReceiving()
    {
        lock (_lock)
        {
            _listenCts?.Cancel();
            _listenCts?.Dispose();
            _listenCts = null;
            _listener?.Stop();
            _listener = null;
            _advertiser?.Dispose();
            _advertiser = null;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (ReceiveEnabled)
        {
            Advertise(force: false);
        }
    }

    /// <summary>
    /// Publishes <c>_FC9F5ED42C8A._tcp</c> with the endpoint-info TXT record. Republished when the
    /// network changes, so the phone is told the addresses that exist now.
    /// </summary>
    private void Advertise(bool force)
    {
        lock (_lock)
        {
            if (_listener is null)
            {
                return;
            }
            var now = DateTime.UtcNow;
            if (!force && now - _lastAdvertiseUtc < ReadvertiseCooldown)
            {
                return;
            }
            _lastAdvertiseUtc = now;
            var port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            var addresses = LanAddresses();
            _advertiser?.Dispose();
            _advertiser = new ServiceDiscovery();
            var profile = new ServiceProfile(QsWire.ServiceInstanceName(_endpointId), QsWire.ServiceType, port, addresses);
            profile.AddProperty("n", QsWire.Base64Url(QsWire.EndpointInfo(DeviceName, QsWire.DeviceType.Laptop)));
            _advertiser.Advertise(profile);
            log.Log(LogLevel.Info,
                $"Quick Share: visible to nearby phones as \"{DeviceName}\" " +
                $"({(addresses.Count > 0 ? string.Join(", ", addresses) : "no LAN address")}, port {port}).");
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // listener stopped
            }
            _ = HandleIncomingAsync(client, ct);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        var id = NewTransferId();
        var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        QsConnection? connection = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                (connection, var offer) = await QsConnection.AcceptAsync(client, handshake.Token);
                var sender = connection.RemoteName ?? "A nearby device";
                var items = offer.Describe().ToList();
                var title = items.Count == 1 ? items[0] : $"{items.Count} items";
                Update(new QsTransfer(id, QsDirection.Incoming, sender, title, connection.Pin,
                    QsState.WaitingForAnswer, 0, offer.TotalBytes));
                log.Log(LogLevel.Info, $"Quick Share: {sender} ({peer}) wants to send {title} — PIN {connection.Pin}.");

                var pending = new QsIncomingOffer
                {
                    Id = id,
                    SenderName = sender,
                    Pin = connection.Pin,
                    Items = items,
                    TotalBytes = offer.TotalBytes,
                    FromKnownPhone = registry.KnownDevices.Any(d =>
                        string.Equals(d.Model, sender, StringComparison.OrdinalIgnoreCase)),
                };

                // A known phone just proved it is on this network. If Linc is not linked to it,
                // that is as good a "look now" signal as a resume or a network change.
                if (pending.FromKnownPhone && supervisor.State != LinkState.Connected)
                {
                    supervisor.NudgeReconnect($"{sender} just sent a Quick Share from {peer}");
                }

                var linked = supervisor.State == LinkState.Connected ? supervisor.Device : null;
                var ownPhone = QsTrust.IsOwnConnectedPhone(sender, peer, linked?.Model, LinkedPhoneAddress());
                _pendingOffers[id] = pending;
                if (AutoAcceptOwnPhone && ownPhone)
                {
                    log.Log(LogLevel.Info, $"Quick Share: auto-accepted - {sender} is at {peer}, the same address as your live Linc link.");
                    pending.Accept();
                }
                else
                {
                    IncomingOffer?.Invoke(pending);
                    ShowOfferToast(pending);
                }

                var answer = await Task.WhenAny(pending.Decided, Task.Delay(OfferTimeout, ct));
                _pendingOffers.TryRemove(id, out _);
                RemoveToast(id);
                if (answer != pending.Decided || !pending.Decided.Result)
                {
                    await connection.RejectAsync(ct);
                    Update(Get(id) with { State = QsState.Declined, Message = answer == pending.Decided ? "Declined" : "No answer — declined" });
                    return;
                }

                Update(Get(id) with { State = QsState.Transferring });
                var progress = new Progress<long>(bytes => Update(Get(id) with { Bytes = bytes }));
                var (files, texts) = await connection.ReceiveAsync(offer, ReceivedFolder, progress, ct);
                Update(Get(id) with
                {
                    State = QsState.Done,
                    Bytes = offer.TotalBytes,
                    Files = files,
                    Texts = texts,
                    Message = files.Count > 0 ? $"Saved to {ReceivedFolder}" : null,
                });
                log.Log(LogLevel.Info, $"Quick Share: received {title} from {sender}.");
                ShowDoneToast(sender, files, texts);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException
                                       or OperationCanceledException or SocketException or Google.Protobuf.InvalidProtocolBufferException)
        {
            if (_transfers.ContainsKey(id))
            {
                Update(Get(id) with { State = QsState.Failed, Message = Plain(ex) });
            }
            log.Log(LogLevel.Warn, $"Quick Share from {peer} stopped: {ex.Message}");
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            else
            {
                client.Dispose();
            }
        }
    }

    // =========================================================================================
    // Sending
    // =========================================================================================

    public async Task ScanAsync(TimeSpan duration, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            return;
        }
        NearbyChanged?.Invoke();
        var publisher = StartWakeBeacon();
        try
        {
            var until = DateTime.UtcNow + duration;
            var own = LanAddresses().Select(a => a.ToString()).ToHashSet();
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                IReadOnlyList<IZeroconfHost> hosts;
                try
                {
                    hosts = await ZeroconfResolver.ResolveAsync(QsWire.ServiceType + ".local.",
                        scanTime: TimeSpan.FromSeconds(2), cancellationToken: ct);
                }
                catch (Exception ex) when (ex is SocketException or InvalidOperationException)
                {
                    log.Log(LogLevel.Warn, $"Quick Share: couldn't browse the network: {ex.Message}");
                    break;
                }
                foreach (var host in hosts)
                {
                    if (string.IsNullOrEmpty(host.IPAddress) || own.Contains(host.IPAddress) ||
                        !IPAddress.TryParse(host.IPAddress, out var ip))
                    {
                        continue;
                    }
                    foreach (var service in host.Services.Values)
                    {
                        var props = new Dictionary<string, string>();
                        foreach (var pair in service.Properties?.SelectMany(p => p) ?? [])
                        {
                            props.TryAdd(pair.Key, pair.Value); // first TXT record wins on a duplicate key
                        }
                        var (name, type) = props.TryGetValue("n", out var n)
                            ? SafeParse(n)
                            : (null, QsWire.DeviceType.Unknown);
                        var device = new QsNearbyDevice($"{ip}:{service.Port}", name ?? "Hidden device", type,
                            new IPEndPoint(ip, service.Port));
                        if (_nearby.TryAdd(device.Id, device))
                        {
                            NearbyChanged?.Invoke();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed or the user stopped the scan.
        }
        finally
        {
            StopWakeBeacon(publisher);
            Interlocked.Exchange(ref _scanning, 0);
            NearbyChanged?.Invoke();
        }
    }

    public Task SendAsync(QsNearbyDevice target, IReadOnlyList<string> paths, CancellationToken ct) =>
        SendCoreAsync(target, paths, null, ct);

    public Task SendTextAsync(QsNearbyDevice target, string text, CancellationToken ct) =>
        SendCoreAsync(target, [], text, ct);

    private async Task SendCoreAsync(QsNearbyDevice target, IReadOnlyList<string> paths, string? text, CancellationToken ct)
    {
        var id = NewTransferId();
        var total = paths.Sum(p => new FileInfo(p).Length) + (text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(text));
        var title = text is not null
            ? (text.Length > 60 ? text[..60] + "…" : text)
            : paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
        Update(new QsTransfer(id, QsDirection.Outgoing, target.Name, title, "", QsState.WaitingForAnswer, 0, total,
            "Connecting…"));
        try
        {
            await using var connection = await QsConnection.ConnectAsync(target.Endpoint, DeviceName, ct);
            Update(Get(id) with { Pin = connection.Pin, Message = $"Waiting for {target.Name} to accept — PIN {connection.Pin}" });
            log.Log(LogLevel.Info, $"Quick Share: offering {title} to {target.Name} — PIN {connection.Pin}.");
            var progress = new Progress<long>(bytes => Update(Get(id) with { State = QsState.Transferring, Bytes = bytes, Message = null }));
            var accepted = text is not null
                ? await connection.SendTextAsync(text, ct)
                : await connection.SendFilesAsync(paths, progress, ct);
            Update(Get(id) with
            {
                State = accepted ? QsState.Done : QsState.Declined,
                Bytes = accepted ? total : 0,
                Message = accepted ? "Sent" : $"{target.Name} declined",
            });
            log.Log(LogLevel.Info, accepted ? $"Quick Share: sent {title} to {target.Name}." : $"Quick Share: {target.Name} declined {title}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException
                                       or OperationCanceledException or SocketException or Google.Protobuf.InvalidProtocolBufferException)
        {
            Update(Get(id) with { State = QsState.Failed, Message = Plain(ex) });
            log.Log(LogLevel.Warn, $"Quick Share to {target.Name} failed: {ex.Message}");
            // A device that can't be reached is stale; drop it so the list stays honest.
            if (ex is SocketException && _nearby.TryRemove(target.Id, out _))
            {
                NearbyChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Android phones only advertise their Quick Share service after hearing this BLE beacon
    /// (PROTOCOL.md: service 0xFE2C, fixed prefix). Windows, unlike macOS, lets an app publish
    /// service data, so a PC can wake phones the way Google's own Windows app does. Best effort:
    /// a PC with no Bluetooth still finds phones already advertising.
    /// </summary>
    private BluetoothLEAdvertisementPublisher? StartWakeBeacon()
    {
        try
        {
            var writer = new DataWriter();
            writer.WriteBytes([0x2C, 0xFE]); // UUID 0xFE2C, little-endian
            writer.WriteBytes(QsWire.WakeServiceData());
            var advertisement = new BluetoothLEAdvertisement();
            advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(0x16, writer.DetachBuffer()));
            var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
            publisher.Start();
            return publisher;
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Info, $"Quick Share: no Bluetooth wake-up ({ex.Message}); only phones already visible will show.");
            return null;
        }
    }

    private static void StopWakeBeacon(BluetoothLEAdvertisementPublisher? publisher)
    {
        try
        {
            publisher?.Stop();
        }
        catch (Exception)
        {
            // The adapter went away mid-scan; there is nothing left to stop.
        }
    }

    // =========================================================================================
    // Notifications
    // =========================================================================================

    public void HandleNotificationArguments(IDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("qs", out var action) || !arguments.TryGetValue("id", out var id) ||
            !_pendingOffers.TryGetValue(id, out var offer))
        {
            return;
        }
        if (action == "accept")
        {
            offer.Accept();
        }
        else if (action == "decline")
        {
            offer.Decline();
        }
    }

    private void ShowOfferToast(QsIncomingOffer offer)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("qs", "open").AddArgument("id", offer.Id)
                .AddText($"{offer.SenderName} wants to share with you")
                .AddText(offer.Items.Count == 1 ? offer.Items[0] : $"{offer.Items.Count} items")
                .AddText($"PIN {offer.Pin} — check it matches the phone")
                .AddButton(new AppNotificationButton("Accept").AddArgument("qs", "accept").AddArgument("id", offer.Id))
                .AddButton(new AppNotificationButton("Decline").AddArgument("qs", "decline").AddArgument("id", offer.Id))
                .SetTag(offer.Id)
                .SetScenario(AppNotificationScenario.Reminder)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Quick Share: couldn't show the accept notification ({ex.Message}); use the Share page.");
        }
    }

    private static void RemoveToast(string id)
    {
        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(id);
        }
        catch (Exception)
        {
            // No toast to remove (never shown, or notifications are off).
        }
    }

    private void ShowDoneToast(string sender, List<string> files, List<QsReceivedText> texts)
    {
        try
        {
            var text = files.Count switch
            {
                0 when texts.Count > 0 => texts[0].Text,
                1 => Path.GetFileName(files[0]),
                _ => $"{files.Count} files",
            };
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText($"Received from {sender}")
                .AddText(text)
                .BuildNotification());
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Info, $"Quick Share: couldn't show the received notification ({ex.Message}).");
        }
    }

    // =========================================================================================
    // Helpers
    // =========================================================================================

    private void Update(QsTransfer transfer)
    {
        _transfers[transfer.Id] = transfer;
        TransferChanged?.Invoke(transfer);
    }

    private QsTransfer Get(string id) => _transfers[id];

    private static long _transferCounter;

    /// <summary>Sortable, so newest-first is a string sort.</summary>
    private static string NewTransferId() =>
        $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref _transferCounter):D4}";

    private static (string? Name, QsWire.DeviceType Type) SafeParse(string base64)
    {
        try
        {
            return QsWire.ParseEndpointInfo(QsWire.FromBase64Url(base64));
        }
        catch (FormatException)
        {
            return (null, QsWire.DeviceType.Unknown);
        }
    }

    private static string Plain(Exception ex) => ex switch
    {
        OperationCanceledException => "Cancelled",
        SocketException => "Couldn't reach the device. Check you're both on the same Wi-Fi network.",
        System.Security.Cryptography.CryptographicException => "The secure connection failed; try again.",
        _ => ex.Message.StartsWith("Quick Share: ", StringComparison.Ordinal) ? ex.Message["Quick Share: ".Length..] : ex.Message,
    };

    private static string DefaultName()
    {
        var name = Environment.MachineName;
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant() : "Windows PC";
    }

    /// <summary>
    /// Real LAN IPv4 addresses, excluding loopback, link-local and virtual adapters — the same
    /// judgement the Direct-TLS advert makes, so a VPN never out-ranks Wi-Fi here either.
    /// </summary>
    private static List<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            var description = nic.Description.ToLowerInvariant();
            if (description.Contains("virtual") || description.Contains("hyper-v") || description.Contains("vpn") ||
                description.Contains("radmin") || description.Contains("vmware") || description.Contains("virtualbox"))
            {
                continue;
            }
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    list.Add(ip);
                }
            }
        }
        return list;
    }

    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(true, null);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.Log(LogLevel.Warn, $"Quick Share settings unreadable, using defaults: {ex.Message}");
        }
        return new Settings(true, null);
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings));
        }
        catch (IOException ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't save Quick Share settings: {ex.Message}");
        }
    }

    /// <summary>The live Linc link's phone address, where one exists (Wi-Fi links only).</summary>
    private string? LinkedPhoneAddress()
    {
        if (supervisor.State != LinkState.Connected || supervisor.Device is not { } device)
        {
            return null;
        }
        return device.Transport switch
        {
            LinkTransport.DirectTls => connection.PeerAddress,
            LinkTransport.AdbWireless => device.Serial.Contains(':') ? device.Serial[..device.Serial.LastIndexOf(':')] : null,
            _ => null, // a USB link has no network address to compare against
        };
    }

    private sealed record Settings(bool ReceiveEnabled, string? DeviceName, bool AutoAcceptOwnPhone = false);
}
