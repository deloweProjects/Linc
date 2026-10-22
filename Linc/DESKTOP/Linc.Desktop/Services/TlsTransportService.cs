using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Makaretu.Dns;

namespace Linc.Desktop.Services;

public interface ITlsTransportService : IDisposable
{
    /// <summary>This PC's certificate, base64 DER — sent in `tls.exchange`.</summary>
    string CertificateBase64 { get; }

    /// <summary>
    /// This PC's stable id for the phone (D-088): the first 16 hex digits of the SHA-256 of its
    /// certificate. Sent in `hello` and the mDNS advert so a phone that knows several PCs can tell
    /// them apart; the phone computes the same value from the certificate it pinned.
    /// </summary>
    string PcId { get; }

    int TlsPort { get; }
    int ReversePort { get; }

    /// <summary>
    /// An authenticated phone dialed in and is waiting for our `hello`. Fires on a background
    /// thread. The second argument is the connection's remote endpoint — on a hotspot link that
    /// IS the phone's address on that link, answered by the OS when it established the socket
    /// (PROTOCOL.md v18). It is null when the platform did not report one.
    /// <para>
    /// It has to be handed over here because it is unreachable anywhere else: the consumer gets
    /// an <c>SslStream</c>, and <c>AuthenticatedStream.InnerStream</c> is protected, so there is
    /// no public path from the stream back down to the socket (measured in M13a §2).
    /// </para>
    /// </summary>
    event Action<Stream, IPEndPoint?>? ControlArrived;

    /// <summary>Start/stop listening + advertising, per the Background connection setting.</summary>
    void Start();
    void Stop();

    /// <summary>Republish the <c>_linc._tcp</c> advert now — after the PC wakes (D-081).</summary>
    void RefreshAdvert();

    /// <summary>
    /// Registers a waiter for a phone dial-back carrying `{channel, sessionToken}`,
    /// to be armed BEFORE sending `channel.open` (avoids the race). The returned task
    /// completes when the matching connection arrives.
    /// </summary>
    Task<Stream> AwaitChannelAsync(int channel, string sessionToken, CancellationToken ct);

    /// <summary>This PC's certificate (DER), for the pairing proof and the QR code's hash.</summary>
    byte[] CertificateDer { get; }

    /// <summary>
    /// v32: dial the phone's LincLink listener with mutual TLS — this PC's certificate, the
    /// phone's pinned. Outbound, so Windows Firewall never stands in the way.
    /// </summary>
    Task<SslStream> DialPhoneAsync(IPEndPoint phone, string phoneCertBase64, CancellationToken ct);

    /// <summary>v32: dial a phone that just scanned this PC's QR code (any certificate, only while the window is open).</summary>
    Task<(SslStream Ssl, byte[] PhoneCert)> DialPhoneForPairingAsync(IPEndPoint phone, CancellationToken ct);

    /// <summary>v32: dial a typed channel to the phone (a PC-dialled link): the header is already written.</summary>
    Task<Stream> DialPhoneChannelAsync(IPEndPoint phone, string phoneCertBase64, int channel, string sessionToken, CancellationToken ct);

    /// <summary>
    /// v32: open the QR pairing window (5 minutes, one use) and return what the code carries. The
    /// listener starts if it wasn't running. A second call replaces the code.
    /// </summary>
    PairingInvite OpenPairing();

    void ClosePairing();

    /// <summary>The pairing window's token while it is open (the UDP pairing probe is keyed by it), else null.</summary>
    byte[]? PairingToken { get; }

    /// <summary>
    /// v32: finish a pairing on a connection whose phone presented an unknown certificate while the
    /// window is open: read its <c>link.pair</c>, check the proof, pin it. True when paired.
    /// </summary>
    /// <param name="phoneListener">Where the phone's LincLink listener is, when known, so the PC can dial it next.</param>
    Task<bool> CompletePairingAsync(Stream stream, byte[] phoneCertDer, IPEndPoint? phoneListener, CancellationToken ct);

    /// <summary>A phone was just paired by QR code: its model, and its listener when known. The supervisor adopts it next.</summary>
    event Action<string, IPEndPoint?>? PhonePaired;
}

/// <summary>
/// The Direct TLS transport (docs/PROTOCOL.md v9, D-022): a mutual-TLS listener the
/// phone dials into, advertised as `_linc._tcp` on mDNS. Certificates are pinned
/// exact-bytes in both directions; connections presenting anything else are dropped
/// silently. The identical protocol/session/channel code runs over these streams.
/// </summary>
public sealed class TlsTransportService(IDeviceRegistry registry, ILogService log) : ITlsTransportService
{
    public int TlsPort => 46001;
    public int ReversePort => 46011;

    private readonly Lazy<X509Certificate2> _identity = new(() => LoadOrCreateIdentity(Path.Combine(registry.RootPath, "tls-identity.pfx")));
    /// <summary>
    /// Dial-backs being waited for, first come first served per (channel, session). A single slot
    /// per key used to be overwritten when the PC opened the same channel several times at once
    /// (app icons over the bulk channel): one dial-back matched, the rest arrived to an empty slot,
    /// were taken for new control connections and dropped, and their fetches hung for 8 s each.
    /// </summary>
    private readonly ConcurrentDictionary<(int Channel, string Token), ConcurrentQueue<TaskCompletionSource<Stream>>> _channelWaiters = new();
    private int _waiting;

    private TcpListener? _listener;
    private ServiceDiscovery? _advertiser;
    private CancellationTokenSource? _cts;

    /// <summary>Link changes arrive in bursts, so republish at most this often.</summary>
    private static readonly TimeSpan AdvertiseCooldown = TimeSpan.FromSeconds(2);
    private DateTime _lastAdvertiseUtc = DateTime.MinValue;

    public string CertificateBase64 => Convert.ToBase64String(_identity.Value.Export(X509ContentType.Cert));

    public string PcId => Convert.ToHexString(SHA256.HashData(_identity.Value.Export(X509ContentType.Cert)))[..16].ToLowerInvariant();

    public event Action<Stream, IPEndPoint?>? ControlArrived;
    public event Action<string, IPEndPoint?>? PhonePaired;

    private readonly PairingWindow _pairing = new();

    public byte[] CertificateDer => _identity.Value.Export(X509ContentType.Cert);

    public byte[]? PairingToken
    {
        get
        {
            lock (_pairing)
            {
                return _pairing.IsOpen(DateTime.UtcNow) ? _pairing.CurrentToken : null;
            }
        }
    }

    public PairingInvite OpenPairing()
    {
        Start(); // the phone may dial in to pair; harmless when already listening
        byte[] token;
        lock (_pairing)
        {
            token = _pairing.Open(DateTime.UtcNow);
        }
        log.Log(LogLevel.Info, "Showing a pairing code for 5 minutes (QR, no cable needed).");
        return new PairingInvite(LinkPairing.CertHash(CertificateDer), LanAddresses().Select(a => a.ToString()).ToList(),
            TlsPort, token, Environment.MachineName);
    }

    public void ClosePairing()
    {
        lock (_pairing)
        {
            _pairing.Close();
        }
    }

    private bool PairingOpen()
    {
        lock (_pairing)
        {
            return _pairing.IsOpen(DateTime.UtcNow);
        }
    }

    public async Task<bool> CompletePairingAsync(Stream stream, byte[] phoneCertDer, IPEndPoint? phoneListener, CancellationToken ct)
    {
        // The phone speaks first on a pairing connection: one link.pair, then this side answers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var json = await Framing.ReadAsync(stream, timeout.Token);
        if (json is null)
        {
            return false;
        }
        Envelope request;
        try
        {
            request = Envelope.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        if (request.Type != MessageType.LinkPair || (string?)request.Payload["proof"] is not { } proofText)
        {
            return false;
        }
        byte[] proof;
        try
        {
            proof = Convert.FromBase64String(proofText);
        }
        catch (FormatException)
        {
            return false;
        }
        bool ok;
        lock (_pairing)
        {
            ok = _pairing.TryConsume(proof, phoneCertDer, CertificateDer, DateTime.UtcNow);
        }
        var reply = Envelope.Create(MessageType.LinkPair, ok
            ? new JsonObject { ["ok"] = true, ["name"] = Environment.MachineName, ["pcId"] = PcId }
            : new JsonObject { ["ok"] = false }, replyTo: request.Id);
        await Framing.WriteAsync(stream, reply.ToJson(), timeout.Token);
        if (!ok)
        {
            log.Log(LogLevel.Warn, "A phone tried to pair with an old or wrong code; ignored.");
            return false;
        }
        var model = (string?)request.Payload["model"] is { Length: > 0 } m ? m : "Android phone";
        var certBase64 = Convert.ToBase64String(phoneCertDer);
        registry.SaveQrPairedPhone(LinkSecretStore.PhoneIdOf(certBase64)!, model, certBase64);
        log.Log(LogLevel.Info, $"Paired with {model} by QR code (no cable).");
        PhonePaired?.Invoke(model, phoneListener);
        return true;
    }

    public async Task<SslStream> DialPhoneAsync(IPEndPoint phone, string phoneCertBase64, CancellationToken ct) =>
        (await DialPhoneCoreAsync(phone, Convert.FromBase64String(phoneCertBase64), limitSendBuffer: false, ct)).Ssl;

    public Task<(SslStream Ssl, byte[] PhoneCert)> DialPhoneForPairingAsync(IPEndPoint phone, CancellationToken ct) =>
        DialPhoneCoreAsync(phone, [], limitSendBuffer: false, ct);

    public async Task<Stream> DialPhoneChannelAsync(IPEndPoint phone, string phoneCertBase64, int channel, string sessionToken, CancellationToken ct)
    {
        var (ssl, _) = await DialPhoneCoreAsync(phone, Convert.FromBase64String(phoneCertBase64),
            limitSendBuffer: channel is VideoChannel or AudioChannel, ct);
        try
        {
            var header = new JsonObject { ["channel"] = channel, ["sessionToken"] = sessionToken };
            await Framing.WriteAsync(ssl, header.ToJsonString(), ct);
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    /// <summary>
    /// TCP, then mutual TLS: this PC presents its identity; the phone must present exactly
    /// <paramref name="pinnedPhoneCert"/>, or — with <paramref name="pinnedPhoneCert"/> empty, only
    /// while the pairing window is open — any certificate, which the caller then pairs or drops.
    /// </summary>
    internal async Task<(SslStream Ssl, byte[] PhoneCert)> DialPhoneCoreAsync(
        IPEndPoint phone, byte[] pinnedPhoneCert, bool limitSendBuffer, CancellationToken ct)
    {
        var client = new TcpClient(phone.AddressFamily) { NoDelay = true };
        try
        {
            if (limitSendBuffer)
            {
                LimitSendBuffer(client);
            }
            await client.ConnectAsync(phone.Address, phone.Port, ct);
            var pairing = pinnedPhoneCert.Length == 0;
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, _) => cert is not null &&
                    (pairing ? PairingOpen() : cert.GetRawCertData().AsSpan().SequenceEqual(pinnedPhoneCert)));
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "linc-phone",
                    ClientCertificates = [_identity.Value],
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => _identity.Value,
                    // Pinning replaces chain validation; the callback above is the whole check.
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, handshake.Token);
                return (ssl, ssl.RemoteCertificate?.GetRawCertData() ?? []);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }


    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, TlsPort);
            _listener.Start();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            // M13f §4: the advert must track the machine's interfaces, not a snapshot taken at
            // startup — a phone's hotspot interface that is absent at launch was exactly what
            // left the fallback advertising a VPN address the phone could not reach.
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            Advertise();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Direct connections unavailable: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        // Null the fields before tearing down so an in-flight link-change handler sees a stopped
        // transport and bails rather than racing the disposal.
        var cts = _cts;
        _cts = null;
        var advertiser = _advertiser;
        _advertiser = null;
        var listener = _listener;
        _listener = null;
        cts?.Cancel();
        advertiser?.Dispose();
        listener?.Stop();
    }

    public void Dispose() => Stop();

    public void RefreshAdvert()
    {
        if (_listener is null)
        {
            return; // background connection is off
        }
        try
        {
            Advertise();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't refresh the direct-connection advert: {ex.Message}");
        }
    }

    /// <summary>
    /// Publishes (or republishes) the <c>_linc._tcp</c> advert for the CURRENT set of interfaces
    /// (M13f §4). Re-enumerated on every link change; reuses <see cref="HotspotInterfaces.Enumerate"/>
    /// and its virtual-adapter classification rather than a second copy of that heuristic, and
    /// ranks virtual/VPN adapters last so a Radmin VPN can no longer out-rank Wi-Fi in the advert
    /// the phone dials.
    /// </summary>
    private void Advertise()
    {
        var lan = LanAddresses();
        _advertiser?.Dispose();
        _advertiser = new ServiceDiscovery();
        var profile = new ServiceProfile(
            Environment.MachineName.ToLowerInvariant(), "_linc._tcp", (ushort)TlsPort, lan);
        // Belt-and-suspenders: Android's resolver returns only ONE host address, which
        // on multi-adapter PCs (Hyper-V/Docker/VPN) is often unreachable. Publish every
        // real LAN address in a TXT record so the phone can try them all (D-022).
        if (lan.Count > 0)
        {
            profile.AddProperty("addrs", string.Join(",", lan.Select(a => a.ToString())));
        }
        // Which PC this is, for a phone that knows several (D-088).
        profile.AddProperty("id", PcId);
        _advertiser.Advertise(profile);
        var shown = lan.Count > 0 ? string.Join(", ", lan) : "no LAN address found";
        log.Log(LogLevel.Info, $"Direct connection ready on {shown} — waiting for the phone to find this PC");
    }

    /// <summary>
    /// A link came or went: republish the advert so the phone can dial the addresses that exist
    /// NOW. Cooldown-gated because NetworkAddressChanged fires in bursts (adapter flaps, VPN
    /// connect/disconnect); the listener keeps running either way — a transient enumeration
    /// failure must not take the transport down.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_listener is null)
        {
            return; // stopped
        }
        var now = DateTime.UtcNow;
        if (now - _lastAdvertiseUtc < AdvertiseCooldown)
        {
            return;
        }
        _lastAdvertiseUtc = now;
        try
        {
            Advertise();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't refresh the direct-connection advert after a link change: {ex.Message}");
        }
    }

    public Task<Stream> AwaitChannelAsync(int channel, string sessionToken, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _channelWaiters.GetOrAdd((channel, sessionToken), _ => new()).Enqueue(waiter);
        Interlocked.Increment(ref _waiting);
        // A cancelled waiter stays queued but completed; TakeWaiter skips it.
        ct.Register(() => { if (waiter.TrySetCanceled(ct)) Interlocked.Decrement(ref _waiting); });
        return waiter.Task;
    }

    /// <summary>The oldest still-waiting request for this dial-back, if any.</summary>
    private TaskCompletionSource<Stream>? TakeWaiter(int channel, string token)
    {
        if (!_channelWaiters.TryGetValue((channel, token), out var queue)) return null;
        while (queue.TryDequeue(out var waiter))
        {
            if (!waiter.Task.IsCompleted) return waiter;
        }
        return null;
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
            catch (Exception)
            {
                return; // listener stopped
            }
            // Nagle would hold a small write back waiting for the previous ACK. Linc's
            // traffic is control messages and live video, so that only ever costs latency —
            // on Wi-Fi it is the difference between a smooth mirror and one that hitches.
            try { client.NoDelay = true; } catch { }
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        SslStream? ssl = null;
        try
        {
            // Every phone this PC knows is recognised, not just the active one: a known phone
            // dialling in is a returning phone, and rejecting it silently made it look brand new
            // (it had to come back over ADB and re-pair before Direct TLS worked again). Which
            // phone it was is read back from the certificate by the supervisor.
            var pinned = DeviceAdmission.PinnedCertificates(registry.KnownDevices.Select(d => d.PhoneCertBase64));
            var pairing = PairingOpen();
            if (pinned.Count == 0 && !pairing)
            {
                client.Dispose();
                return; // not TLS-paired yet — nothing can authenticate
            }
            // While the QR pairing window is open (v32) an unknown phone may connect, but only to
            // send its proof; CompletePairingAsync below pins it or drops it.
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, _) => cert is not null && (DeviceAdmission.MatchesAny(cert.GetRawCertData(), pinned) || pairing));
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Value,
                ClientCertificateRequired = true,
                // Pinning replaces chain validation; the callback above is the whole check.
            }, handshakeTimeout.Token);

            if (ssl.RemoteCertificate is null)
            {
                ssl.Dispose();
                return;
            }
            var presented = ssl.RemoteCertificate.GetRawCertData();
            if (!DeviceAdmission.MatchesAny(presented, pinned))
            {
                using (ssl)
                {
                    // The phone redials as a paired phone; the PC may dial its listener too.
                    var listener = client.Client.RemoteEndPoint is IPEndPoint from ? new IPEndPoint(from.Address, LinkProtocol.PhonePort) : null;
                    await CompletePairingAsync(ssl, presented, listener, ct);
                }
                return;
            }

            // Channel dial-backs announce themselves with a header frame immediately;
            // control connections stay silent (the desktop speaks first). Only try the
            // read when a dial-back is actually expected.
            if (Volatile.Read(ref _waiting) > 0)
            {
                var header = await TryReadHeaderAsync(ssl);
                if (header is not null)
                {
                    var channel = (int?)header["channel"] ?? -1;
                    var token = (string?)header["sessionToken"] ?? "";
                    if (TakeWaiter(channel, token) is { } waiter)
                    {
                        if (channel is VideoChannel or AudioChannel) LimitSendBuffer(client);
                        if (waiter.TrySetResult(ssl))
                        {
                            Interlocked.Decrement(ref _waiting);
                            return; // ownership handed to the waiter
                        }
                    }
                    ssl.Dispose(); // unknown/stale dial-back
                    return;
                }
            }
            // Say which socket and whether a dial-back was pending: a field log (1.0.0.7) showed a
            // dozen of these 100 ms apart while already connected, and from the bare line there was
            // no telling repeat control dials from dial-backs that missed their waiter.
            log.Log(LogLevel.Info,
                $"Phone connected directly (no ADB) from {client.Client.RemoteEndPoint}" +
                (_channelWaiters.IsEmpty ? "" : $"; {_channelWaiters.Count} channel dial-back(s) pending but this connection sent no header"));
            // Read the endpoint here, while the TcpClient is still in scope — this is the only
            // point in the process where it is reachable at all (M13b §2, v18).
            ControlArrived?.Invoke(ssl, client.Client.RemoteEndPoint as IPEndPoint);
        }
        catch (Exception)
        {
            // Unauthenticated/unparseable peers are dropped silently by design.
            ssl?.Dispose();
            client.Dispose();
        }
    }

    /// <summary>The pc-video channel (5) — the one connection whose send buffer is kept small.</summary>
    private const int VideoChannel = 5;

    /// <summary>The pc-audio channel (6, v20): live sound is worth even less late than video.</summary>
    private const int AudioChannel = 6;

    /// <summary>
    /// Cap the kernel send buffer on the pc-video connection.
    ///
    /// <para>Left alone, Windows auto-tunes a TCP send buffer up to megabytes on a busy
    /// connection. For a file that is throughput; for a live mirror it is a hidden queue the
    /// app cannot see or drop: on a Wi-Fi link that carries 3 Mb/s, a 2 MB buffer is five
    /// seconds of picture already committed to being late. Worse, while that buffer is
    /// absorbing the backlog every write returns instantly, so the sender's congestion control
    /// sees a healthy link right up until it is seconds behind.</para>
    ///
    /// <para>64 KB still leaves far more than a LAN's bandwidth-delay product (64 KB over a
    /// 20 ms round trip is ~26 Mb/s, three times the highest quality the phone can ask for), so
    /// throughput is unaffected — but a slow link now shows up within a frame or two as a write
    /// that blocks, which is the signal <see cref="PcMirrorSender"/> acts on.</para>
    /// </summary>
    private static void LimitSendBuffer(TcpClient client)
    {
        try { client.Client.SendBufferSize = 64 * 1024; } catch { /* keep the default */ }
    }

    /// <summary>Reads one JSON frame if it arrives within 3 s; null means "control connection".</summary>
    private static async Task<JsonObject?> TryReadHeaderAsync(SslStream ssl)
    {
        var readTask = Framing.ReadAsync(ssl, CancellationToken.None);
        var winner = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3)));
        if (winner != readTask)
        {
            return null; // silence ⇒ control (no bytes were consumed)
        }
        var json = await readTask;
        return json is null ? null : JsonNode.Parse(json) as JsonObject;
    }

    /// <summary>
    /// Real LAN IPv4 addresses to advertise: up, non-loopback, on an interface with a usable
    /// IPv4 default gateway. Reuses <see cref="HotspotInterfaces.Enumerate"/> — the same
    /// classification hotspotprobe reports — and ranks virtual/VPN adapters last (M13f §4), so
    /// a Radmin VPN adapter (whose description may say only "Ethernet Adapter") can no longer
    /// out-rank Wi-Fi in the advert the phone dials. Virtual addresses are still advertised,
    /// just last: ranking, never rejection, exactly as in HotspotAddress.
    /// </summary>
    private static List<IPAddress> LanAddresses()
    {
        var physical = new List<IPAddress>();
        var virtualAdapters = new List<IPAddress>();
        foreach (var iface in HotspotInterfaces.Enumerate())
        {
            if (iface.Address is not { Length: > 0 } text ||
                !IPAddress.TryParse(text, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                text.StartsWith("169.254"))
            {
                continue;
            }
            // Real networks have a usable IPv4 default gateway; host-only/virtual usually don't.
            if (iface.Gateway is not { Length: > 0 } gateway || gateway.StartsWith("169.254"))
            {
                continue;
            }
            (iface.IsVirtual ? virtualAdapters : physical).Add(address);
        }
        return physical.Concat(virtualAdapters).Distinct().ToList();
    }

    // SChannel (SslStream server auth) needs the private key in a persisted key set;
    // an ephemeral in-memory key fails the server handshake with "no credentials".
    private const X509KeyStorageFlags KeyFlags =
        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    private static X509Certificate2 LoadOrCreateIdentity(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                return new X509Certificate2(pfxPath, (string?)null, KeyFlags);
            }
            catch (CryptographicException)
            {
                // Corrupt identity: fall through and mint a new one (re-exchange re-pins it).
            }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Linc Desktop", key, HashAlgorithmName.SHA256);
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx));
        // Reload from the PFX so the private key is usable for TLS on Windows.
        return new X509Certificate2(pfxPath, (string?)null, KeyFlags);
    }
}
