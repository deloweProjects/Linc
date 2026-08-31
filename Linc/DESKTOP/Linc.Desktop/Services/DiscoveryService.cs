using Zeroconf;

namespace Linc.Desktop.Services;

public sealed record DiscoveredService(string InstanceName, string IpAddress, int Port);

public interface IDiscoveryService : IDisposable
{
    /// <summary>A phone with wireless debugging on is reachable (`_adb-tls-connect`).</summary>
    event Action<DiscoveredService>? ConnectServiceSeen;

    /// <summary>A phone is showing its pairing dialog (`_adb-tls-pairing`).</summary>
    event Action<DiscoveredService>? PairingServiceSeen;

    void Start();
    void Stop();

    /// <summary>
    /// Cut the wait before the next scan. Called when something independent says the phone is
    /// probably here — a BLE sighting (M04) — so discovery doesn't sit out its idle delay.
    /// </summary>
    void ScanNow();
}

/// <summary>
/// Watches the local network for Android wireless-debugging mDNS adverts.
/// Polling (short repeated scans) is used instead of a long-lived listener because
/// pairing adverts only exist while the phone's pairing dialog is open, and reappearing
/// connect adverts must be re-raised after sleep/network changes. Events fire on
/// background threads — subscribers marshal to the UI thread themselves.
/// </summary>
public sealed class DiscoveryService : IDiscoveryService
{
    private const string ConnectProtocol = "_adb-tls-connect._tcp.local.";
    private const string PairingProtocol = "_adb-tls-pairing._tcp.local.";

    public event Action<DiscoveredService>? ConnectServiceSeen;
    public event Action<DiscoveredService>? PairingServiceSeen;

    private CancellationTokenSource? _cts;

    /// <summary>Released by <see cref="ScanNow"/> to end the idle delay early.</summary>
    private readonly SemaphoreSlim _wakeUp = new(0, 1);

    public void ScanNow()
    {
        try
        {
            _wakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
            // A scan is already pending; a second nudge adds nothing.
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hosts = await ZeroconfResolver.ResolveAsync(
                    new[] { ConnectProtocol, PairingProtocol },
                    scanTime: TimeSpan.FromSeconds(2),
                    cancellationToken: ct);
                foreach (var host in hosts)
                {
                    if (string.IsNullOrEmpty(host.IPAddress))
                    {
                        continue;
                    }
                    foreach (var (serviceName, service) in host.Services)
                    {
                        var found = new DiscoveredService(host.DisplayName, host.IPAddress, service.Port);
                        if (serviceName.Contains("_adb-tls-connect"))
                        {
                            ConnectServiceSeen?.Invoke(found);
                        }
                        else if (serviceName.Contains("_adb-tls-pairing"))
                        {
                            PairingServiceSeen?.Invoke(found);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Race on shutdown: Stop()/Dispose() cancelled us mid-scan; exit cleanly.
                break;
            }
            catch
            {
                // Transient network/socket hiccups: keep scanning.
            }

            try
            {
                // Wakes early when ScanNow() fires; otherwise this is the normal idle delay.
                await _wakeUp.WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                // Race on shutdown: Stop()/Dispose() cancelled us during the idle wait; exit cleanly.
                break;
            }
        }
    }
}
