using System.Net.NetworkInformation;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Linc.Desktop.Services;

/// <summary>
/// LincLink's helpers around the main search (owner, 2026-11-01: "when at home auto turn on
/// Bluetooth for both, then handshake, then establish the Wi-Fi connection… Bluetooth is just a
/// helper you can use alongside the main guessing"):
///
/// - **This PC's beacon** says what the PC is doing (linked, hotspot on, looking), so the phone
///   announces itself the moment it hears the PC and offers to join the PC's hotspot.
/// - **The phone's status**, heard over Bluetooth with no link, is logged as a plain clue ("its
///   Wi-Fi is off", "on a network but not answering"), and a phone with Wi-Fi on but no network
///   that knows this PC's hotspot gets the hotspot at once (when that setting is on).
/// - **Home networks** (Settings › Finding your phone): on one, turn this PC's Bluetooth on, and
///   the phone's too once ADB reaches it (<see cref="IDeviceRegistry.BluetoothAtHome"/>, off by default).
/// </summary>
public sealed class NearbyRules(
    IDeviceRegistry registry, ILogService log, IConnectionSupervisor supervisor,
    IConnectionManager connection, IBlePresenceService ble, PcHotspot hotspot)
{
    private Timer? _tick;
    private string? _lastClue;
    private bool _phoneBluetoothDone;

    /// <summary>Raised when something Settings shows changed (phone heard, home, Bluetooth). Any thread.</summary>
    public event Action? Changed;

    public void Begin()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => _ = Task.Run(CheckHomeAsync);
        supervisor.StateChanged += () =>
        {
            Publish();
            _ = Task.Run(OnLinkChangedAsync);
        };
        hotspot.Changed += Publish;
        ble.StatusHeard += OnPhoneStatus;
        _tick = new Timer(_ => Publish(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        _ = Task.Run(CheckHomeAsync);
    }

    /// <summary>Windows' names for the networks this PC is on now (Wi-Fi names, "Network 2" for a cable…).</summary>
    public static IReadOnlyList<string> CurrentNetworks()
    {
        try
        {
            return NetworkInformation.GetConnectionProfiles()
                .Where(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None &&
                    (p.IsWlanConnectionProfile || p.NetworkAdapter?.IanaInterfaceType == 6)) // Wi-Fi or Ethernet
                .Select(p => p.ProfileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public bool AtHome => CurrentNetworks().Any(n => registry.HomeNetworks.Contains(n, StringComparer.OrdinalIgnoreCase));

    public void MarkCurrentAsHome(bool home)
    {
        var current = CurrentNetworks();
        var list = registry.HomeNetworks.ToList();
        if (home)
        {
            list.AddRange(current);
        }
        else
        {
            list.RemoveAll(n => current.Contains(n, StringComparer.OrdinalIgnoreCase));
        }
        registry.SaveHomeNetworks(list);
        log.Log(LogLevel.Info, home ? $"Finding your phone: {string.Join(", ", current)} marked as home." : "Finding your phone: this network is no longer home.");
        Changed?.Invoke();
        _ = Task.Run(CheckHomeAsync);
    }

    /// <summary>The setting was turned on: act on it now.</summary>
    public void Recheck()
    {
        _phoneBluetoothDone = false;
        _ = Task.Run(CheckHomeAsync);
        _ = Task.Run(OnLinkChangedAsync);
    }

    private async Task CheckHomeAsync()
    {
        Changed?.Invoke();
        if (!registry.BluetoothAtHome || !AtHome)
        {
            return;
        }
        await TurnPcBluetoothOnAsync();
    }

    /// <summary>Switch this PC's Bluetooth radio on (Windows asks once whether Linc may control radios).</summary>
    public async Task<string> TurnPcBluetoothOnAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                return "Windows didn't let Linc control Bluetooth (Settings › Privacy › Radios).";
            }
            var bluetooth = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bluetooth is null)
            {
                return "This PC has no Bluetooth.";
            }
            if (bluetooth.State == RadioState.On)
            {
                return "Bluetooth is already on.";
            }
            var result = await bluetooth.SetStateAsync(RadioState.On);
            if (result != RadioAccessStatus.Allowed)
            {
                return $"Windows refused to turn Bluetooth on ({result}).";
            }
            log.Log(LogLevel.Info, "Finding your phone: at home, turned this PC's Bluetooth on.");
            ble.Start(); // listen now rather than at the next retry
            return "Turned Bluetooth on.";
        }
        catch (Exception ex)
        {
            return $"Couldn't turn Bluetooth on ({ex.Message}).";
        }
    }

    /// <summary>Linked with ADB at home: the phone's Bluetooth on too (`svc bluetooth enable`), once per run.</summary>
    private async Task OnLinkChangedAsync()
    {
        if (!registry.BluetoothAtHome || _phoneBluetoothDone || supervisor.State != LinkState.Connected || !AtHome ||
            (connection.RawDevice ?? connection.AdbOnlyDevice) is not { } device)
        {
            return;
        }
        try
        {
            var adb = new AdbClient();
            var on = await ShellAsync(adb, device, "settings get global bluetooth_on");
            _phoneBluetoothDone = true;
            if (on.Trim() == "1")
            {
                return;
            }
            await ShellAsync(adb, device, "svc bluetooth enable");
            log.Log(LogLevel.Info, "Finding your phone: at home, turned the phone's Bluetooth on, so the two hear each other next time.");
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Info, $"Finding your phone: couldn't turn the phone's Bluetooth on ({ex.Message}).");
        }
    }

    private static async Task<string> ShellAsync(AdbClient adb, AdvancedSharpAdbClient.Models.DeviceData device, string command)
    {
        var receiver = new ConsoleOutputReceiver();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await adb.ExecuteRemoteCommandAsync(command, device, receiver, cts.Token);
        return receiver.ToString();
    }

    /// <summary>What this PC's beacon says.</summary>
    private void Publish()
    {
        var flags = PcBeaconFlags.None;
        if (supervisor.State == LinkState.Connected) flags |= PcBeaconFlags.Linked;
        if (hotspot.Current != PcHotspot.Mode.Off) flags |= PcBeaconFlags.HotspotOn;
        if (supervisor.State == LinkState.Searching) flags |= PcBeaconFlags.Looking;
        ble.SetPcStatus(flags);
    }

    /// <summary>The phone's status changed (heard over Bluetooth): a clue in the log while there's no link.</summary>
    private void OnPhoneStatus(string serial, PhoneBeaconFlags flags)
    {
        Changed?.Invoke();
        if (supervisor.State == LinkState.Connected)
        {
            return;
        }
        var clue = BlePresenceService.Describe(flags);
        var hint = !flags.HasFlag(PhoneBeaconFlags.WifiOn) ? " Turn the phone's Wi-Fi on, plug in the cable, or start the PC hotspot."
            : !flags.HasFlag(PhoneBeaconFlags.OnWifi) ? " The phone isn't on any Wi-Fi: start the PC hotspot, or join the PC's network."
            : flags.HasFlag(PhoneBeaconFlags.Linked) ? " The phone thinks it's linked: the link it has may be stale; Linc keeps trying."
            : " Both are on Wi-Fi but can't reach each other: different networks, or the router keeps devices apart (Connection Doctor checks).";
        if (clue == _lastClue)
        {
            return;
        }
        _lastClue = clue;
        log.Log(LogLevel.Info, $"The phone over Bluetooth: {clue}.{hint}");
    }
}
