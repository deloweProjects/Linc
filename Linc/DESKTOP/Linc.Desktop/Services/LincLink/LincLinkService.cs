using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace Linc.Desktop.Services;

/// <summary>A TLS link this PC dialled to the phone's LincLink listener.</summary>
public sealed record PhoneDial(SslStream Stream, IPEndPoint Peer, string Address);

/// <summary>
/// LincLink's I/O on the PC (Vibe/agent/design/linclink.md §3, §6): the UDP beacon (probes out,
/// replies and announcements in), dialling the phone's listener as a happy-eyeballs race, the
/// network fingerprint and the per-network memory of which route won. It reports and dials; the
/// supervisor decides what to adopt. Idle unless the new connection engine is on.
/// </summary>
public interface ILincLinkService : IDisposable
{
    void Start();
    void Stop();

    /// <summary>A known phone answered a probe or announced itself: its LincLink id and where to dial it.</summary>
    event Action<string, IPEndPoint>? PhoneSpotted;

    /// <summary>Broadcast probes for every phone with a link secret now, and a few more over the next seconds.</summary>
    void Probe(string why);

    /// <summary>The fingerprint of the network this PC is on now (cached for a few seconds).</summary>
    string CurrentNetwork { get; }

    /// <summary>Where the phone was seen by beacon in the last minute (freshest first).</summary>
    IReadOnlyList<IPEndPoint> RecentSightings(string phoneId);

    /// <summary>Race TLS dials to the phone's listener at these candidates; the first to finish TLS wins.</summary>
    Task<PhoneDial?> DialAsync(string phoneId, string phoneCertBase64, IReadOnlyList<RouteCandidate> candidates, CancellationToken ct);

    IReadOnlyList<RouteMemo> Remembered(string phoneId);
    void Succeeded(string phoneId, string route, string address, int rttMs);
    void Failed(string phoneId, string route, string address);
}

public sealed class LincLinkService(
    IDeviceRegistry registry,
    ITlsTransportService tls,
    LinkSecretStore secrets,
    ILogService log) : ILincLinkService
{
    private static readonly TimeSpan SightingKeep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SightingNews = TimeSpan.FromSeconds(30); // the same phone at the same place again is not news
    private static readonly TimeSpan NonceKeep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NetworkCache = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(4);

    private readonly object _lock = new();
    private readonly string _memoryPath = Path.Combine(registry.RootPath, "link-memory.json");
    private readonly ConcurrentDictionary<string, List<(IPEndPoint At, DateTime SeenUtc)>> _sightings = new();
    private readonly List<(byte[] Nonce, DateTime SentUtc)> _nonces = [];
    private NetMemory? _memory;
    private UdpClient? _probe;
    private UdpClient? _listen;
    private CancellationTokenSource? _cts;
    private string? _network;
    private DateTime _networkAtUtc;
    private int _burst;

    public event Action<string, IPEndPoint>? PhoneSpotted;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        try
        {
            var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            _probe = probe;
            var token = _cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(probe, token)); // off the UI thread: what it reports ends in the supervisor's gate
        }
        catch (SocketException ex)
        {
            log.Log(LogLevel.Warn, $"LincLink: couldn't open the network beacon ({ex.SocketErrorCode}); the phone is still found by its other routes.");
        }
        try
        {
            // The phone's own announcements arrive here, when Windows Firewall lets UDP 46003 in
            // (the installer's rule). Without the rule, the probe replies above still work.
            var listen = new UdpClient();
            listen.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listen.Client.Bind(new IPEndPoint(IPAddress.Any, LinkBeacon.Port));
            _listen = listen;
            var token = _cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(listen, token));
        }
        catch (SocketException ex)
        {
            log.Log(LogLevel.Info, $"LincLink: not listening for phone announcements on UDP {LinkBeacon.Port} ({ex.SocketErrorCode}); probes still work.");
        }
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        var pairingToken = _cts.Token;
        _ = Task.Run(() => PairingProbeLoopAsync(pairingToken));
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _cts?.Cancel();
        _cts = null;
        _probe?.Dispose();
        _probe = null;
        _listen?.Dispose();
        _listen = null;
    }

    public void Dispose() => Stop();

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _network = null; // recompute on the next ask
        }
    }

    // ---- the beacon --------------------------------------------------------------------------

    public void Probe(string why)
    {
        if (_probe is null || _cts is not { } cts)
        {
            return;
        }
        if (Interlocked.Exchange(ref _burst, 1) == 1)
        {
            return; // a burst is already going: it covers this ask
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Now, then 1, 2 and 4 s later: a phone that has just joined the Wi-Fi may not
                // hear the first one, and broadcasts are cheap (23 bytes).
                foreach (var wait in new[] { 0, 1000, 1000, 2000 })
                {
                    await Task.Delay(wait, cts.Token);
                    SendProbes();
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            finally
            {
                Interlocked.Exchange(ref _burst, 0);
            }
        });
        log.Log(LogLevel.Info, $"LincLink: asking the network for the phone ({why})."); // the log limits repeats
    }

    private void SendProbes()
    {
        var keys = secrets.All();
        foreach (var (_, secret) in keys)
        {
            Broadcast(LinkBeacon.RoleProbe, secret);
        }
    }

    private void Broadcast(byte role, byte[] secret)
    {
        if (_probe is not { } probe)
        {
            return;
        }
        var nonce = LinkBeacon.NewNonce();
        lock (_nonces)
        {
            _nonces.RemoveAll(n => DateTime.UtcNow - n.SentUtc > NonceKeep);
            _nonces.Add((nonce, DateTime.UtcNow));
        }
        var packet = LinkBeacon.Build(role, secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nonce, tls.TlsPort);
        foreach (var target in BroadcastTargets())
        {
            try
            {
                probe.Send(packet, packet.Length, new IPEndPoint(target, LinkBeacon.Port));
            }
            catch (SocketException)
            {
                // an interface that went away mid-send; the others still go
            }
        }
    }

    /// <summary>The limited broadcast plus each up interface's own subnet broadcast (Windows sends 255.255.255.255 out one interface only).</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var iface in HotspotInterfaces.Enumerate(includeIPv6: false))
        {
            if (IPAddress.TryParse(iface.Address, out var address) && iface.PrefixLength is > 0 and < 31 &&
                !iface.Address.StartsWith("169.254", StringComparison.Ordinal))
            {
                var bits = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
                var host = uint.MaxValue >> iface.PrefixLength;
                targets.Add(ToAddress(bits | host));
            }
        }
        return targets;
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // e.g. ICMP port unreachable from a probe target: not fatal on Windows
            }
            OnPacket(received.Buffer, received.RemoteEndPoint);
        }
    }

    private void OnPacket(byte[] data, IPEndPoint from)
    {
        if (LinkBeacon.Parse(data) is not { } packet || packet.Role == LinkBeacon.RoleProbe)
        {
            return; // our own probe echoing back, or not ours at all
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (packet.Role == LinkBeacon.RoleReply && !IsOurNonce(packet.Nonce))
        {
            return; // a reply to nothing this PC sent recently: a replay
        }
        if (tls.PairingToken is { } token && packet.Role == LinkBeacon.RoleReply && LinkBeacon.Matches(packet, token, now))
        {
            _ = PairByDialAsync(new IPEndPoint(from.Address, packet.Port));
            return;
        }
        foreach (var (phoneId, secret) in secrets.All())
        {
            if (!LinkBeacon.Matches(packet, secret, now))
            {
                continue;
            }
            var at = new IPEndPoint(from.Address, packet.Port);
            var list = _sightings.GetOrAdd(phoneId, _ => []);
            bool news;
            lock (list)
            {
                var nowUtc = DateTime.UtcNow;
                news = !list.Any(s => s.At.Equals(at) && nowUtc - s.SeenUtc < SightingNews);
                list.RemoveAll(s => s.At.Equals(at) || nowUtc - s.SeenUtc > SightingKeep);
                list.Insert(0, (at, nowUtc));
            }
            if (news)
            {
                // Reported (and logged) once per place per 30 s: every probe burst gets four
                // replies, and each report wakes the dialler.
                log.Log(LogLevel.Info, $"LincLink: the phone {(packet.Role == LinkBeacon.RoleReply ? "answered" : "announced itself")} at {at}.");
                PhoneSpotted?.Invoke(phoneId, at);
            }
            return;
        }
    }

    private bool IsOurNonce(byte[] nonce)
    {
        lock (_nonces)
        {
            return _nonces.Any(n => n.Nonce.AsSpan().SequenceEqual(nonce) && DateTime.UtcNow - n.SentUtc <= NonceKeep);
        }
    }

    public IReadOnlyList<IPEndPoint> RecentSightings(string phoneId)
    {
        if (!_sightings.TryGetValue(phoneId, out var list))
        {
            return [];
        }
        lock (list)
        {
            return list.Where(s => DateTime.UtcNow - s.SeenUtc <= SightingKeep).Select(s => s.At).ToList();
        }
    }

    // ---- pairing by QR code, when the phone can't reach this PC (its firewall) ---------------

    private int _pairing;

    /// <summary>While the QR code is showing, probe once a second with the code's token: a phone
    /// that just scanned it answers, and this PC dials it (outbound, so the firewall can't block).</summary>
    private async Task PairingProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (tls.PairingToken is { } token)
            {
                Broadcast(LinkBeacon.RoleProbe, token);
            }
        }
    }

    private async Task PairByDialAsync(IPEndPoint phone)
    {
        if (Interlocked.Exchange(ref _pairing, 1) == 1)
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (ssl, cert) = await tls.DialPhoneForPairingAsync(phone, timeout.Token);
            using (ssl)
            {
                await tls.CompletePairingAsync(ssl, cert, phone, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            log.Log(LogLevel.Info, $"LincLink: couldn't reach the phone at {phone} to pair ({ex.Message}); it can still dial this PC.");
        }
        finally
        {
            Interlocked.Exchange(ref _pairing, 0);
        }
    }

    // ---- dialling the phone --------------------------------------------------------------------

    public async Task<PhoneDial?> DialAsync(string phoneId, string phoneCertBase64, IReadOnlyList<RouteCandidate> candidates, CancellationToken ct)
    {
        var dials = candidates.Where(c => c.Route == LinkRoute.TlsOut).ToList();
        if (dials.Count == 0)
        {
            return null;
        }
        var attempts = dials.Select(c => (Func<CancellationToken, Task<PhoneDial?>>)(async attemptCt =>
        {
            if (!TryParseEndpoint(c.Address, out var endpoint))
            {
                return null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
            timeout.CancelAfter(DialTimeout);
            var ssl = await tls.DialPhoneAsync(endpoint, phoneCertBase64, timeout.Token);
            return new PhoneDial(ssl, endpoint, c.Address);
        })).ToList();
        var result = await RouteRace.FirstAsync(attempts, RouteRace.Stagger, d => d.Stream.Dispose(), ct);
        var tried = string.Join(", ", dials.Select((c, i) => $"{c.Address} ({c.Source.ToString().ToLowerInvariant()}" +
            (result.Failures.FirstOrDefault(f => f.Index == i) is { Error: { } e } ? $": {e})" : ")")));
        foreach (var (index, _) in result.Failures)
        {
            if (dials[index].Source == CandidateSource.Memory)
            {
                Failed(phoneId, LinkRoute.TlsOut, dials[index].Address); // five in a row and it's forgotten
            }
        }
        if (result.Winner is { } winner)
        {
            log.Log(LogLevel.Info, $"LincLink: reached the phone at {winner.Address} in {(int)result.Elapsed.TotalMilliseconds} ms (tried {tried}).");
        }
        else
        {
            log.Log(LogLevel.Info, $"LincLink: the phone didn't answer a direct dial (tried {tried}).");
        }
        return result.Winner;
    }

    private static bool TryParseEndpoint(string text, out IPEndPoint endpoint) =>
        IPEndPoint.TryParse(text, out endpoint!) && endpoint.Port > 0;

    // ---- where we are, and what worked here before ---------------------------------------------

    public string CurrentNetwork
    {
        get
        {
            lock (_lock)
            {
                if (_network is not null && DateTime.UtcNow - _networkAtUtc < NetworkCache)
                {
                    return _network;
                }
            }
            var network = ComputeNetwork();
            lock (_lock)
            {
                _network = network;
                _networkAtUtc = DateTime.UtcNow;
            }
            return network;
        }
    }

    /// <summary>The physical interface with a default gateway: its gateway's hardware address and subnet.</summary>
    private static string ComputeNetwork()
    {
        var iface = HotspotInterfaces.Enumerate(includeIPv6: false)
            .Where(i => i.Gateway is { Length: > 0 } g && !g.StartsWith("169.254", StringComparison.Ordinal))
            .OrderBy(i => i.IsVirtual)
            .FirstOrDefault();
        if (iface is null)
        {
            return NetMemory.Fingerprint(null, null);
        }
        var mac = NeighborTable.MacOf(iface.Gateway) ?? iface.Gateway;
        return NetMemory.Fingerprint(mac, Subnet(iface.Address, iface.PrefixLength));
    }

    /// <summary>"192.168.1.0/24" for 192.168.1.23 with a 24-bit prefix.</summary>
    private static string Subnet(string address, int prefix)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || prefix is < 0 or > 32)
        {
            return address;
        }
        var bits = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{ToAddress(bits & mask)}/{prefix}";
    }

    private static IPAddress ToAddress(uint bits)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, bits);
        return new IPAddress(bytes);
    }

    private NetMemory Memory()
    {
        if (_memory is not null)
        {
            return _memory;
        }
        try
        {
            _memory = File.Exists(_memoryPath)
                ? JsonSerializer.Deserialize<NetMemory>(File.ReadAllText(_memoryPath)) ?? new NetMemory()
                : new NetMemory();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _memory = new NetMemory();
        }
        return _memory;
    }

    public IReadOnlyList<RouteMemo> Remembered(string phoneId)
    {
        var network = CurrentNetwork;
        lock (_lock)
        {
            return Memory().Ranked(network, phoneId);
        }
    }

    public void Succeeded(string phoneId, string route, string address, int rttMs)
    {
        var network = CurrentNetwork;
        lock (_lock)
        {
            Memory().Succeeded(network, phoneId, route, address, rttMs, DateTime.UtcNow);
            SaveMemory();
        }
    }

    public void Failed(string phoneId, string route, string address)
    {
        var network = CurrentNetwork;
        lock (_lock)
        {
            Memory().Failed(network, phoneId, route, address);
            SaveMemory();
        }
    }

    private void SaveMemory()
    {
        try
        {
            File.WriteAllText(_memoryPath, JsonSerializer.Serialize(_memory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // memory is a hint; losing a write costs one slower race
        }
    }
}
