using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace Linc.Desktop.Services;

public enum DoctorStatus { Ok, Warning, Problem, Skipped }

/// <summary>One check's result, in plain words, with a fix Linc can apply itself where there is one.</summary>
public sealed record DoctorFinding(
    string Title, DoctorStatus Status, string Detail, string? FixLabel = null, Func<Task<string>>? Fix = null);

/// <summary>
/// Connection Doctor (2026-10-31; the idea from NectarLink's feature list, written from scratch for
/// Linc): the reasons a phone and PC on the same Wi-Fi can't find each other, checked one by one,
/// each with a plain explanation and a Fix button where Linc can fix it.
///
/// Windows-side fixes that need an administrator (the firewall rule, the network profile) ask
/// Windows once, the same prompt the installer shows. Phone-side fixes go over ADB and need the
/// cable or wireless debugging. Every check is best effort: one that can't run says "Skipped", never
/// a false "OK".
/// </summary>
public sealed class ConnectionDoctor(
    IConnectionSupervisor supervisor, IConnectionManager connection, IDeviceRegistry registry, ILogService log)
{
    /// <summary>The installer's rule name (installer/*.iss, FirewallRule), so both manage the same rule.</summary>
    private const string FirewallRule = "Linc-LincLink";
    private const string Package = "app.linc.android";

    public async Task<IReadOnlyList<DoctorFinding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<DoctorFinding> { LinkCheck(), AdbCheck() };
        findings.Add(await Safe("Windows Firewall", () => FirewallAsync(ct)));
        findings.Add(await Safe("Network type", () => ProfileAsync(ct)));
        findings.Add(VpnCheck());
        findings.Add(await Safe("Reaching the phone on Wi-Fi", () => ReachAsync(ct)));
        findings.Add(await Safe("Phone battery settings", () => PhoneBatteryAsync(ct)));
        log.Log(LogLevel.Info, "Connection Doctor: " + string.Join("; ", findings.Select(f => $"{f.Title} = {f.Status}")));
        return findings;
    }

    private static async Task<DoctorFinding> Safe(string title, Func<Task<DoctorFinding>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception ex)
        {
            return new DoctorFinding(title, DoctorStatus.Skipped, $"Couldn't check this ({ex.Message}).");
        }
    }

    // ---- the link itself ----

    private DoctorFinding LinkCheck()
    {
        if (supervisor.State != LinkState.Connected)
        {
            return supervisor.Presence switch
            {
                LinkPresence.ConnectedAdbOnly => new("Linc link", DoctorStatus.Warning,
                    "Connected with ADB only: the Linc app isn't installed on the phone. Home has an Install button."),
                LinkPresence.ConnectedStartingApp => new("Linc link", DoctorStatus.Warning,
                    "ADB reaches the phone, but the Linc app there isn't answering yet. Open Linc on the phone once."),
                _ => new("Linc link", DoctorStatus.Problem,
                    "Not connected. The checks below say what may be in the way."),
            };
        }
        if (supervisor.LinkIsPlainWifi)
        {
            return new("Linc link", DoctorStatus.Warning,
                "Connected, but over plain ADB on Wi-Fi (port 5555): anyone on this network could read what Linc sends.",
                "Encrypt Wi-Fi links", () =>
                {
                    registry.SaveEncryptWifiLinks(true);
                    supervisor.UpgradeToEncryptedIfPlain();
                    return Task.FromResult("Turned on Encrypt Wi-Fi links; Linc is moving to the encrypted link.");
                });
        }
        return new("Linc link", DoctorStatus.Ok, $"Connected. {supervisor.SecurityCaption}.");
    }

    private DoctorFinding AdbCheck() => supervisor.AdbUp
        ? new("ADB", DoctorStatus.Ok, "ADB is up: Mirror, Tools and fast file copies are ready.")
        : new("ADB", DoctorStatus.Warning,
            "ADB isn't up. Everything else works; Mirror, Tools and fast file copies need the USB cable or wireless debugging " +
            "(phone: Settings › System › Developer options › Wireless debugging).");

    // ---- Windows ----

    private async Task<DoctorFinding> FirewallAsync(CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? "";
        var output = await RunAsync("netsh", $"advfirewall firewall show rule name={FirewallRule} verbose", ct);
        if (output.Contains(exe, StringComparison.OrdinalIgnoreCase))
        {
            return new("Windows Firewall", DoctorStatus.Ok, "Linc is allowed through Windows Firewall on home networks.");
        }
        var why = output.Contains(FirewallRule, StringComparison.OrdinalIgnoreCase)
            ? "There is a Linc rule, but for a Linc in another folder (a test build runs from its own folder)."
            : "There's no rule letting Linc through.";
        return new("Windows Firewall", DoctorStatus.Problem,
            $"{why} The phone can't dial this PC or reach it with its beacon; the PC can still dial the phone, which is slower.",
            "Allow Linc (Windows asks once)", async () =>
            {
                var command = $"/c netsh advfirewall firewall delete rule name={FirewallRule} >nul 2>&1 & " +
                    $"netsh advfirewall firewall add rule name={FirewallRule} dir=in action=allow \"program={exe}\" enable=yes profile=private,domain";
                return await RunElevatedAsync("cmd.exe", command)
                    ? "Linc is now allowed through Windows Firewall."
                    : "Windows didn't allow it (the permission prompt was declined).";
            });
    }

    private async Task<DoctorFinding> ProfileAsync(CancellationToken ct)
    {
        var json = await RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-NetConnectionProfile | Select-Object InterfaceAlias,InterfaceIndex,@{n='Category';e={[int]$_.NetworkCategory}} | ConvertTo-Json -Compress\"", ct);
        var node = JsonNode.Parse(json.Trim() is { Length: > 0 } t ? t : "[]");
        var profiles = node is JsonArray array ? array.OfType<JsonObject>().ToList() : node is JsonObject one ? [one] : [];
        if (profiles.Count == 0)
        {
            return new("Network type", DoctorStatus.Skipped, "Windows didn't list any network.");
        }
        var publicOnes = profiles.Where(p => (int?)p["Category"] == 0).ToList();
        if (publicOnes.Count == 0)
        {
            return new("Network type", DoctorStatus.Ok, "Your network is set to Private, so the PC and phone may find each other.");
        }
        var first = publicOnes[0];
        var name = (string?)first["InterfaceAlias"] ?? "this network";
        var index = (int?)first["InterfaceIndex"] ?? -1;
        return new("Network type", DoctorStatus.Problem,
            $"Windows treats {name} as a Public network, so it hides this PC from other devices and the firewall rule doesn't apply. " +
            "Set it to Private if this is your home Wi-Fi.",
            index >= 0 ? "Make it Private (Windows asks once)" : null,
            index >= 0 ? async () => await RunElevatedAsync("powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Private\"")
                ? $"{name} is now a Private network."
                : "Windows didn't allow it (the permission prompt was declined)."
            : null);
    }

    private static DoctorFinding VpnCheck()
    {
        string[] hints = ["vpn", "tap-", "wireguard", "tunnel", "openvpn", "tailscale", "zerotier", "nordlynx", "fortinet", "cisco anyconnect", "protonvpn"];
        var vpns = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                (n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                 hints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(h, StringComparison.OrdinalIgnoreCase))))
            .Select(n => n.Name).Distinct().ToList();
        return vpns.Count == 0
            ? new("VPN", DoctorStatus.Ok, "No VPN is on.")
            : new("VPN", DoctorStatus.Warning,
                $"A VPN is on ({string.Join(", ", vpns)}). Some send everything, the phone's address included, away from your Wi-Fi. " +
                "If Linc can't find the phone, allow local network access in the VPN's settings or pause it.");
    }

    // ---- the network between them ----

    private async Task<DoctorFinding> ReachAsync(CancellationToken ct)
    {
        if (supervisor.State == LinkState.Connected && supervisor.Route == "wifi")
        {
            return new("Reaching the phone on Wi-Fi", DoctorStatus.Ok, "The PC reaches the phone over Wi-Fi.");
        }
        var address = connection.PeerAddress ?? AdbSerial.HostOf(registry.LastHostPort);
        if (address is null || !IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip))
        {
            return new("Reaching the phone on Wi-Fi", DoctorStatus.Skipped,
                "Linc doesn't know the phone's Wi-Fi address yet. Connect once (cable or Wi-Fi), then run the Doctor again.");
        }
        var sameSubnet = LocalSubnetHas(ip);
        var pinged = false;
        using (var ping = new Ping())
        {
            for (var i = 0; i < 2 && !pinged; i++)
            {
                pinged = (await ping.SendPingAsync(ip, 1000)).Status == IPStatus.Success;
            }
        }
        var port = await TcpOpenAsync(ip, LinkProtocol.PhonePort, ct);
        if (pinged || port)
        {
            return new("Reaching the phone on Wi-Fi", DoctorStatus.Ok,
                $"The phone answers at {address}{(port ? " and Linc there is listening" : "")}.");
        }
        return new("Reaching the phone on Wi-Fi", sameSubnet ? DoctorStatus.Problem : DoctorStatus.Warning, sameSubnet
            ? $"The PC can't reach the phone at {address}, though both are on the same network. The router probably keeps devices apart " +
              "(\"client isolation\" or \"AP isolation\", common on guest Wi-Fi). Use the main Wi-Fi, turn isolation off in the router, or use the USB cable."
            : $"The phone was last at {address}, which isn't on this PC's network now. Put both on the same Wi-Fi.");
    }

    private static bool LocalSubnetHas(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var target = ip.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }
                var local = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (local[i] & mask[i]) == (target[i] & mask[i])))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task<bool> TcpOpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            await client.ConnectAsync(ip, port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- the phone ----

    private async Task<DoctorFinding> PhoneBatteryAsync(CancellationToken ct)
    {
        if ((connection.RawDevice ?? connection.AdbOnlyDevice) is not { } device)
        {
            return new("Phone battery settings", DoctorStatus.Skipped,
                "Needs the USB cable or wireless debugging to look at the phone's battery settings.");
        }
        var adb = new AdbClient();
        var whitelist = await ShellAsync(adb, device, "dumpsys deviceidle whitelist", ct);
        var background = await ShellAsync(adb, device, $"cmd appops get {Package} RUN_ANY_IN_BACKGROUND", ct);
        var optimised = !whitelist.Contains(Package, StringComparison.Ordinal);
        var restricted = background.Contains("ignore", StringComparison.OrdinalIgnoreCase) ||
            background.Contains("deny", StringComparison.OrdinalIgnoreCase);
        if (!optimised && !restricted)
        {
            return new("Phone battery settings", DoctorStatus.Ok, "Android lets Linc keep running in the background.");
        }
        return new("Phone battery settings", restricted ? DoctorStatus.Problem : DoctorStatus.Warning,
            restricted
                ? "Android is restricting Linc in the background, so the phone drops off when the screen is off."
                : "Android's battery optimisation can pause Linc after a while with the screen off, which drops the link.",
            "Let Linc run in the background", async () =>
            {
                await ShellAsync(adb, device, $"dumpsys deviceidle whitelist +{Package}", CancellationToken.None);
                await ShellAsync(adb, device, $"cmd appops set {Package} RUN_ANY_IN_BACKGROUND allow", CancellationToken.None);
                return "Done: Android no longer pauses Linc to save battery.";
            });
    }

    private static async Task<string> ShellAsync(AdbClient adb, DeviceData device, string command, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        await adb.ExecuteRemoteCommandAsync(command, device, receiver, ct);
        return receiver.ToString();
    }

    // ---- processes ----

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"{file} didn't start");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    /// <summary>Runs one command as administrator: Windows shows its permission prompt. False when declined.</summary>
    private static async Task<bool> RunElevatedAsync(string file, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the prompt was declined
        }
    }
}
