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

    /// <summary>
    /// Registers a waiter for a phone dial-back carrying `{channel, sessionToken}`,
    /// to be armed BEFORE sending `channel.open` (avoids the race). The returned task
    /// completes when the matching connection arrives.
    /// </summary>
    Task<Stream> AwaitChannelAsync(int channel, string sessionToken, CancellationToken ct);
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
    private readonly ConcurrentDictionary<(int Channel, string Token), TaskCompletionSource<Stream>> _channelWaiters = new();

    private TcpListener? _listener;
    private ServiceDiscovery? _advertiser;
    private CancellationTokenSource? _cts;

    /// <summary>Link changes arrive in bursts, so republish at most this often.</summary>
    private static readonly TimeSpan AdvertiseCooldown = TimeSpan.FromSeconds(2);
    private DateTime _lastAdvertiseUtc = DateTime.MinValue;

    public string CertificateBase64 => Convert.ToBase64String(_identity.Value.Export(X509ContentType.Cert));

    public event Action<Stream, IPEndPoint?>? ControlArrived;

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
        _channelWaiters[(channel, sessionToken)] = waiter;
        ct.Register(() =>
        {
            if (_channelWaiters.TryRemove((channel, sessionToken), out var pending))
            {
                pending.TrySetCanceled(ct);
            }
        });
        return waiter.Task;
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
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        SslStream? ssl = null;
        try
        {
            var pinned = registry.PhoneCertBase64;
            if (pinned is null)
            {
                client.Dispose();
                return; // not TLS-paired yet — nothing can authenticate
            }
            var pinnedBytes = Convert.FromBase64String(pinned);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, _) => cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(pinnedBytes));
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

            // Channel dial-backs announce themselves with a header frame immediately;
            // control connections stay silent (the desktop speaks first). Only try the
            // read when a dial-back is actually expected.
            if (!_channelWaiters.IsEmpty)
            {
                var header = await TryReadHeaderAsync(ssl);
                if (header is not null)
                {
                    var channel = (int?)header["channel"] ?? -1;
                    var token = (string?)header["sessionToken"] ?? "";
                    if (_channelWaiters.TryRemove((channel, token), out var waiter))
                    {
                        waiter.TrySetResult(ssl);
                        return; // ownership handed to the waiter
                    }
                    ssl.Dispose(); // unknown/stale dial-back
                    return;
                }
            }
            log.Log(LogLevel.Info, "Phone connected directly (no ADB)");
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
