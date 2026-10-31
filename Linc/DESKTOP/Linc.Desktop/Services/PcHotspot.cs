using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Devices.WiFiDirect;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.Security.Credentials;

namespace Linc.Desktop.Services;

/// <summary>
/// The PC's own Wi-Fi network for the phone when there's no shared Wi-Fi and no cable (LincLink
/// brief §3 step 6; the owner: "Windows won't allow everyday Mobile Hotspot with no internet").
///
/// 1. **Mobile Hotspot** (`NetworkOperatorTetheringManager`), with Windows' own hotspot name and
///    password, never changed. With internet it shares that connection. With none, Windows' switch
///    in Settings refuses, but the API still shares any network that is up with local access only
///    (a virtual switch, a VPN adapter, a cable to nothing): measured on the owner's PC 2026-11-01,
///    where Radmin VPN and the Hyper-V Default Switch both report tethering "Enabled".
/// 2. **Wi-Fi Direct legacy network** (`WiFiDirectAdvertisementPublisher`) as the last resort, under
///    a hard time limit: on the owner's PC (Realtek 8852BE, unpackaged app) `Start()` never returned,
///    so it runs on its own thread and is given up on after a few seconds, once per run of Linc.
///
/// Stop turns off only a hotspot Linc itself started. Started by hand from Settings › Connection;
/// the automatic "phone is near" start over Bluetooth (brief step 5) is not built.
/// </summary>
public sealed class PcHotspot(
    IDeviceRegistry registry, ILogService log, IConnectionSupervisor supervisor,
    IConnectionManager connection, IBlePresenceService ble)
{
    public enum Mode { Off, MobileHotspot, WifiDirect }

    private sealed record Saved(string Ssid, string Password);

    // ---- automatic (LincLink brief step 5) ----

    /// <summary>Heard the phone with no link: give the usual routes this long before the hotspot.</summary>
    private static readonly TimeSpan NearGrace = TimeSpan.FromSeconds(20);

    /// <summary>A hotspot Linc started by itself goes off again if the phone hasn't come on it by then.</summary>
    private static readonly TimeSpan AutoGiveUp = TimeSpan.FromMinutes(3);

    /// <summary>After an automatic try, don't try again for this long (the phone may simply be busy).</summary>
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _nearSinceUtc;
    private DateTimeOffset _lastAutoUtc = DateTimeOffset.MinValue;
    private bool _autoStarted;
    private int _autoBusy;

    /// <summary>Started once at launch (App.xaml.cs). Cheap: two event handlers.</summary>
    public void Begin()
    {
        supervisor.StateChanged += () => _ = Task.Run(OnLinkChangedAsync);
        ble.DeviceSighted += serial => _ = Task.Run(() => OnPhoneNearAsync(serial));
    }

    /// <summary>
    /// Connected: give the phone this PC's hotspot (once, and again only if it changed), never
    /// over plain ADB Wi-Fi (port 5555) where the password could be read. The phone adds it as a
    /// suggested network Android joins by itself.
    /// </summary>
    private async Task OnLinkChangedAsync()
    {
        if (supervisor.State != LinkState.Connected)
        {
            return;
        }
        _nearSinceUtc = null;
        _autoStarted = false; // the phone is here: an automatic hotspot no longer times out
        if (!registry.AutoHotspot || supervisor.LinkIsPlainWifi || connection.Current is not { Companion.V: >= 32 } live)
        {
            return;
        }
        var network = Current == Mode.MobileHotspot && Network is { } on ? on : ReadWindowsHotspot();
        if (network is not { } net)
        {
            return;
        }
        var key = registry.PairedSerial ?? live.Serial; // the same key the Bluetooth sighting carries
        var stamp = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{net.Ssid}|{net.Password}")))[..16];
        if (SentStamp(key) == stamp)
        {
            return; // the phone already has exactly this network
        }
        try
        {
            await connection.SendToPhoneAsync(Protocol.Envelope.Create(Protocol.MessageType.LinkHotspot,
                new System.Text.Json.Nodes.JsonObject { ["ssid"] = net.Ssid, ["password"] = net.Password }), CancellationToken.None);
            SaveSentStamp(key, stamp);
            log.Log(LogLevel.Info, $"PC hotspot: gave the phone this PC's hotspot (\"{net.Ssid}\") so it can join by itself later.");
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Info, $"PC hotspot: couldn't give the phone the hotspot yet ({ex.Message}); next connect.");
        }
    }

    /// <summary>The setting was just turned on: a connected phone gets the network now.</summary>
    public Task GiveToPhoneNowAsync() => Task.Run(OnLinkChangedAsync);

    /// <summary>The phone's beacon (about once a second while it's near). Idempotent.</summary>
    private async Task OnPhoneNearAsync(string serial)
    {
        if (supervisor.State == LinkState.Connected)
        {
            _nearSinceUtc = null;
            return;
        }
        if (!registry.AutoHotspot || Current != Mode.Off || SentStamp(serial) is null ||
            !string.Equals(serial, registry.PairedSerial, StringComparison.OrdinalIgnoreCase) ||
            DateTimeOffset.UtcNow - _lastAutoUtc < AutoCooldown)
        {
            return;
        }
        _nearSinceUtc ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _nearSinceUtc < NearGrace || Interlocked.CompareExchange(ref _autoBusy, 1, 0) != 0)
        {
            return;
        }
        try
        {
            _lastAutoUtc = DateTimeOffset.UtcNow;
            log.Log(LogLevel.Info, $"PC hotspot: the phone is near (Bluetooth) but hasn't connected in {NearGrace.TotalSeconds:0} s; starting the PC hotspot for it.");
            var result = await StartAsync();
            if (Current == Mode.Off)
            {
                log.Log(LogLevel.Info, $"PC hotspot: automatic start didn't work ({result}).");
                return;
            }
            _autoStarted = !WasAlreadyOn;
            await Task.Delay(AutoGiveUp);
            if (_autoStarted && supervisor.State != LinkState.Connected && Current != Mode.Off)
            {
                log.Log(LogLevel.Info, $"PC hotspot: the phone didn't join within {AutoGiveUp.TotalMinutes:0} minutes; turning the hotspot off again.");
                await StopAsync();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _autoBusy, 0);
        }
    }

    /// <summary>Windows' hotspot name and password, read without turning it on.</summary>
    private static (string Ssid, string Password)? ReadWindowsHotspot()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile() ?? NetworkInformation.GetConnectionProfiles()
                .FirstOrDefault(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None &&
                    NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(p) == TetheringCapability.Enabled);
            if (profile is null)
            {
                return null;
            }
            var config = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile).GetCurrentAccessPointConfiguration();
            return string.IsNullOrEmpty(config.Ssid) || string.IsNullOrEmpty(config.Passphrase) ? null : (config.Ssid, config.Passphrase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string SentPath => Path.Combine(registry.RootPath, "pc-hotspot-sent.json");

    private string? SentStamp(string serial)
    {
        try
        {
            return File.Exists(SentPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SentPath)) is { } map &&
                map.TryGetValue(serial, out var stamp) ? stamp : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveSentStamp(string serial, string stamp)
    {
        try
        {
            var map = File.Exists(SentPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SentPath)) ?? [] : [];
            map[serial] = stamp;
            File.WriteAllText(SentPath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"PC hotspot: couldn't note that the phone has the hotspot ({ex.Message})");
        }
    }

    private readonly object _lock = new();
    private WiFiDirectAdvertisementPublisher? _publisher;
    private NetworkOperatorTetheringManager? _tethering;
    private bool _wifiDirectHung;

    public Mode Current { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The network the phone joins while on: Windows' hotspot's own, or Linc's Wi-Fi Direct one.</summary>
    public (string Ssid, string Password)? Network { get; private set; }

    /// <summary>True when the hotspot was already on before Linc was asked: Stop leaves it on.</summary>
    public bool WasAlreadyOn { get; private set; }

    public event Action? Changed;

    private string StorePath => Path.Combine(registry.RootPath, "pc-hotspot.json");

    /// <summary>The text of a "join this Wi-Fi" QR code: a phone camera offers to join it.</summary>
    public static string JoinQr(string ssid, string password)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");
        return $"WIFI:T:WPA;S:{Escape(ssid)};P:{Escape(password)};;";
    }

    /// <summary>Turns a network on. Returns a plain sentence saying what happened.</summary>
    public async Task<string> StartAsync()
    {
        if (Current != Mode.Off)
        {
            return "The PC hotspot is already on.";
        }
        var mobile = await StartMobileHotspotAsync();
        if (mobile is null && Network is { } net)
        {
            Set(Mode.MobileHotspot, null);
            log.Log(LogLevel.Info, $"PC hotspot on: Mobile Hotspot \"{net.Ssid}\"{(WasAlreadyOn ? " (it was already on)" : "")}.");
            return $"On: join \"{net.Ssid}\" on the phone (or scan the code). Linc finds the PC on it by itself.";
        }
        log.Log(LogLevel.Info, $"PC hotspot: Mobile Hotspot didn't start ({mobile}); trying a Wi-Fi Direct network.");
        var direct = await StartWifiDirectAsync();
        if (direct is null && Network is { } wfd)
        {
            Set(Mode.WifiDirect, null);
            log.Log(LogLevel.Info, $"PC hotspot on: Wi-Fi Direct network \"{wfd.Ssid}\".");
            return $"On (Wi-Fi Direct): join \"{wfd.Ssid}\" on the phone (or scan the code).";
        }
        var why = $"Couldn't start a hotspot. Mobile Hotspot: {mobile}. Wi-Fi Direct: {direct}.";
        Network = null;
        Set(Mode.Off, why);
        log.Log(LogLevel.Warn, "PC hotspot: " + why);
        return why + " Is Wi-Fi turned on on this PC?";
    }

    public async Task StopAsync()
    {
        WiFiDirectAdvertisementPublisher? publisher;
        NetworkOperatorTetheringManager? tethering;
        lock (_lock)
        {
            publisher = _publisher;
            tethering = WasAlreadyOn ? null : _tethering; // never switch off the owner's own hotspot
            _publisher = null;
            _tethering = null;
        }
        try
        {
            if (publisher is not null)
            {
                publisher.Stop();
            }
            if (tethering is not null)
            {
                await tethering.StopTetheringAsync();
            }
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"PC hotspot: stopping it hit an error ({ex.Message})");
        }
        if (Current != Mode.Off)
        {
            log.Log(LogLevel.Info, WasAlreadyOn ? "PC hotspot: left Windows' hotspot on (it was on before Linc)." : "PC hotspot off.");
        }
        Network = null;
        WasAlreadyOn = false;
        _autoStarted = false;
        Set(Mode.Off, null);
    }

    /// <summary>Null when on; otherwise why not.</summary>
    private async Task<string?> StartMobileHotspotAsync()
    {
        try
        {
            var profiles = new List<ConnectionProfile>();
            if (NetworkInformation.GetInternetConnectionProfile() is { } internet)
            {
                profiles.Add(internet);
            }
            // No internet: share any network that is up with local access. Windows' own switch
            // refuses here; the API doesn't.
            profiles.AddRange(NetworkInformation.GetConnectionProfiles()
                .Where(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None && !profiles.Contains(p) &&
                    NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(p) == TetheringCapability.Enabled));
            string? last = profiles.Count == 0 ? "no network Windows can share" : null;
            foreach (var profile in profiles)
            {
                try
                {
                    var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                    var config = manager.GetCurrentAccessPointConfiguration();
                    if (manager.TetheringOperationalState == TetheringOperationalState.On)
                    {
                        Remember(manager, config, alreadyOn: true);
                        return null;
                    }
                    var start = await manager.StartTetheringAsync();
                    if (start.Status == TetheringOperationStatus.Success)
                    {
                        Remember(manager, config, alreadyOn: false);
                        log.Log(LogLevel.Info, $"PC hotspot: sharing \"{profile.ProfileName}\".");
                        return null;
                    }
                    last = $"{start.Status}{(string.IsNullOrEmpty(start.AdditionalErrorMessage) ? "" : " " + start.AdditionalErrorMessage)}";
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
            }
            return last;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void Remember(NetworkOperatorTetheringManager manager, NetworkOperatorTetheringAccessPointConfiguration config, bool alreadyOn)
    {
        lock (_lock)
        {
            _tethering = manager;
        }
        WasAlreadyOn = alreadyOn;
        Network = (config.Ssid, config.Passphrase);
        _ = WatchClientsAsync(manager);
    }

    /// <summary>
    /// While the hotspot is on, every 3 s: who has joined? The phone doesn't look for the PC at its
    /// hotspot's address by itself, so the PC dials each new device (LincLink, the phone's listener).
    /// </summary>
    private async Task WatchClientsAsync(NetworkOperatorTetheringManager manager)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            lock (_lock)
            {
                if (_tethering != manager)
                {
                    return; // stopped
                }
            }
            try
            {
                var fresh = manager.GetTetheringClients()
                    .SelectMany(c => c.HostNames)
                    .Where(h => h.Type == Windows.Networking.HostNameType.Ipv4)
                    .Select(h => h.CanonicalName)
                    .Where(seen.Add)
                    .ToList();
                if (fresh.Count > 0)
                {
                    log.Log(LogLevel.Info, $"PC hotspot: {fresh.Count} device(s) joined; looking for the phone among them.");
                    supervisor.HintPhoneAddresses(fresh);
                }
            }
            catch (Exception)
            {
                // Clients unavailable on this build of Windows; the phone's own beacon still works.
            }
            await Task.Delay(3000);
        }
    }

    /// <summary>Null when started; otherwise why not. Never blocks the caller beyond its time limit.</summary>
    private async Task<string?> StartWifiDirectAsync()
    {
        if (_wifiDirectHung)
        {
            return "this PC's Wi-Fi didn't answer earlier (restart Linc to try again)";
        }
        var (ssid, password) = WifiDirectCredentials();
        var settled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        WiFiDirectAdvertisementPublisher? publisher = null;
        // Start() itself can block for ever (owner's PC): on its own thread, never a pool thread.
        var thread = new Thread(() =>
        {
            try
            {
                publisher = new WiFiDirectAdvertisementPublisher();
                publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
                publisher.Advertisement.LegacySettings.IsEnabled = true;
                publisher.Advertisement.LegacySettings.Ssid = ssid;
                publisher.Advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = password };
                publisher.StatusChanged += (_, args) =>
                {
                    if (args.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                    {
                        settled.TrySetResult(null);
                    }
                    else if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                    {
                        if (!settled.TrySetResult($"aborted ({args.Error})") && Current == Mode.WifiDirect)
                        {
                            log.Log(LogLevel.Warn, $"PC hotspot: Windows stopped the Wi-Fi Direct network ({args.Error}).");
                            Network = null;
                            Set(Mode.Off, $"Windows stopped the hotspot ({args.Error}).");
                        }
                    }
                };
                publisher.Start();
            }
            catch (Exception ex)
            {
                settled.TrySetResult(ex.Message);
            }
        }) { IsBackground = true, Name = "Linc Wi-Fi Direct start" };
        thread.Start();
        var outcome = await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        if (outcome != settled.Task)
        {
            _wifiDirectHung = true;
            return "this PC's Wi-Fi didn't answer";
        }
        var result = await settled.Task;
        if (result is null && publisher is not null)
        {
            lock (_lock)
            {
                _publisher = publisher;
            }
            Network = (ssid, password);
        }
        return result;
    }

    /// <summary>Linc's own Wi-Fi Direct network name and password, made once and kept.</summary>
    private (string Ssid, string Password) WifiDirectCredentials()
    {
        try
        {
            if (File.Exists(StorePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(StorePath)) is { } saved &&
                saved.Ssid.Length > 0 && saved.Password.Length >= 8)
            {
                return (saved.Ssid, saved.Password);
            }
        }
        catch (Exception)
        {
        }
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // nothing that reads two ways
        string Pick(int n) => new(Enumerable.Range(0, n).Select(_ => letters[RandomNumberGenerator.GetInt32(letters.Length)]).ToArray());
        var made = new Saved($"Linc-{Pick(4)}", Pick(12));
        try
        {
            Directory.CreateDirectory(registry.RootPath);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(made));
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"PC hotspot: couldn't save its Wi-Fi Direct name and password ({ex.Message})");
        }
        return (made.Ssid, made.Password);
    }

    private void Set(Mode mode, string? error)
    {
        Current = mode;
        LastError = error;
        Changed?.Invoke();
    }
}
