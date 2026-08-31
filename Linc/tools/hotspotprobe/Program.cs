using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Linc.Desktop.Services;

// M13a §3.4 — the hotspot diagnostic. Run this WHILE a hotspot link is up and paste the whole
// output; it is what turns a hand-test into data.
//
//   dotnet run --project tools/hotspotprobe                       (CWD must be ...\yellow\Linc)
//   dotnet run --project tools/hotspotprobe -- 192.168.43.1       (dial that address too)
//   dotnet run --project tools/hotspotprobe -- --no-connect       (measure only, never adb connect)
//   dotnet run --project tools/hotspotprobe -- --serial <your-device-serial>
//
// It changes NOTHING about the machine: no `adb tcpip`, no firewall rule, no interface or
// route change, nothing written to %LOCALAPPDATA%. The single side effect it can have is the
// one the feature itself has — attaching the local ADB server to a phone at <ip>:5555 (and the
// `adb disconnect` for that same endpoint that must precede it). `--no-connect` removes even
// that, at the cost of the get-state / get-serialno lines.

var arguments = args.ToList();
var noConnect = Remove("--no-connect");
var expectedSerial = RemoveValue("--serial");
// Anything left is address input: the first is treated as the control connection's peer
// address, the rest as addresses the phone announced (the v18 `addrs` hook, M13b).
var peer = arguments.FirstOrDefault(a => !a.StartsWith('-'));
var announced = arguments.Where(a => !a.StartsWith('-') && a != peer).ToList();

Console.WriteLine("=========================================================================");
Console.WriteLine(" Linc hotspot probe");
Console.WriteLine($" {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}   machine: {Environment.MachineName}");
Console.WriteLine("=========================================================================");
if (peer is not null)
{
    Console.WriteLine($" peer address given on the command line : {peer}");
}
if (announced.Count > 0)
{
    Console.WriteLine($" announced addresses given              : {string.Join(", ", announced)}");
}
if (expectedSerial is not null)
{
    Console.WriteLine($" expected phone serial                  : {expectedSerial}");
}
Console.WriteLine();

// ---------------------------------------------------------------- 1. interfaces and roles
Console.WriteLine("[1] INTERFACES  (name / address / prefix / default gateway / role Linc picks)");
Console.WriteLine("    Role comes from the production HotspotRoleRule.DecideRole — AP means we host");
Console.WriteLine("    this link and only listen; CLIENT means we joined it and also dial the gateway.");
Console.WriteLine();

var interfaces = HotspotInterfaces.Enumerate();
if (interfaces.Count == 0)
{
    Console.WriteLine("    (no interfaces are up)");
}
foreach (var iface in interfaces)
{
    var role = HotspotInterfaces.RoleFor(iface);
    var dial = HotspotRoleRule.ShouldDial(role)
        ? $"dial {HotspotConnector.BuildEndpoint(iface.Gateway!)}"
        : "listen only";
    Console.WriteLine($"    {iface.Name,-28} {iface.Address,-40}/{iface.PrefixLength,-3} gw={iface.Gateway ?? "(none)",-30} {role.ToString().ToUpperInvariant(),-6} -> {dial}");
    Console.WriteLine($"        {iface.Description}{(iface.IsVirtual ? "   [virtual/VPN — ranked last when choosing what to dial]" : "")}");
}
Console.WriteLine();

// ---------------------------------------------------------------- 2. firewall / network category
Console.WriteLine("[2] WINDOWS NETWORK CATEGORY AND FIREWALL");
var network = ReadNetworkProfiles();
if (network is null)
{
    Console.WriteLine("    (could not read Get-NetConnectionProfile / Get-NetFirewallProfile)");
}
else
{
    var publicInterfaces = new List<string>();
    foreach (var profile in network.Value.Profiles)
    {
        var isPublic = profile.Category.Equals("Public", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"    {profile.Alias,-28} category={profile.Category}");
        if (isPublic)
        {
            publicInterfaces.Add(profile.Alias);
        }
    }
    foreach (var fw in network.Value.Firewall)
    {
        Console.WriteLine($"    firewall profile {fw.Name,-10} enabled={fw.Enabled,-6} default inbound={fw.Inbound}");
    }
    Console.WriteLine();
    if (publicInterfaces.Count > 0)
    {
        Console.WriteLine("    ***********************************************************************");
        Console.WriteLine("    *** WARNING — these interfaces are classified PUBLIC:");
        foreach (var name in publicInterfaces)
        {
            Console.WriteLine($"    ***     {name}");
        }
        Console.WriteLine("    *** Windows classifies a fresh hotspot link as Public and DROPS inbound");
        Console.WriteLine("    *** traffic on the app's listen port. It presents exactly as 'the phone");
        Console.WriteLine("    *** just isn't there', which sends you debugging the wrong layer.");
        Console.WriteLine("    *** If the link is otherwise healthy and nothing arrives, this is why.");
        Console.WriteLine("    ***********************************************************************");
    }
    else
    {
        Console.WriteLine("    No interface is classified Public.");
    }
}
Console.WriteLine();

// ---------------------------------------------------------------- 3. adb devices, verbatim
Console.WriteLine("[3] ADB");
var adbPath = FindAdb();
if (adbPath is null)
{
    Console.WriteLine("    adb.exe: NOT FOUND (ToolLocator.FindAdb returned null and no bundled copy was found)");
}
else
{
    Console.WriteLine($"    adb.exe: {adbPath}");
    Console.WriteLine("    $ adb devices -l");
    foreach (var line in Run(adbPath, "devices", "-l").Split('\n'))
    {
        Console.WriteLine($"      | {line.TrimEnd()}");
    }
}
Console.WriteLine();

// ---------------------------------------------------------------- 4. endpoint resolution
Console.WriteLine("[4] ENDPOINT RESOLUTION  (production HotspotAddress.SelectEndpoint)");
var chosen = HotspotAddress.SelectEndpoint(peer, announced, interfaces);
Console.WriteLine($"    chosen: {chosen ?? "(nothing survived the same-subnet filter — nothing to dial)"}");
if (peer is not null && chosen != peer)
{
    Console.WriteLine($"    note:   the peer address {peer} was NOT chosen — it is outside every subnet this PC is on,");
    Console.WriteLine("            or it is a link-local IPv6 address that an IPv4 candidate outranks.");
}
Console.WriteLine();

// Everything worth reaching: the chosen endpoint first, then every distinct gateway, so the
// owner sees the whole picture rather than only the winner.
var targets = new List<(string Address, string Why)>();
if (chosen is not null)
{
    targets.Add((chosen, "chosen by SelectEndpoint"));
}
foreach (var iface in interfaces)
{
    if (iface.Gateway is not null && targets.All(t => t.Address != iface.Gateway))
    {
        targets.Add((iface.Gateway, $"default gateway of {iface.Name}"));
    }
}

// ---------------------------------------------------------------- 5. reachability of 5555
Console.WriteLine($"[5] TCP {HotspotConnector.AdbTcpPort} REACHABILITY");
Console.WriteLine("    The source address is bound to the interface the target sits on where possible,");
Console.WriteLine("    so a VPN or a second NIC cannot quietly carry the probe out of the wrong door.");
Console.WriteLine();
var reachable = new List<string>();
if (targets.Count == 0)
{
    Console.WriteLine("    (no address to test)");
}
foreach (var (address, why) in targets)
{
    if (!IPAddress.TryParse(address, out var target))
    {
        Console.WriteLine($"    {address,-40} unparseable");
        continue;
    }
    var bind = BindAddressFor(target, interfaces);
    var (ok, elapsed, detail) = await TryTcpAsync(target, HotspotConnector.AdbTcpPort, bind, TimeSpan.FromSeconds(3));
    var bindText = bind is null ? "unbound" : $"from {bind}";
    Console.WriteLine($"    {address,-40} {(ok ? "ACCEPTED" : "no       ")} {elapsed,5} ms  ({bindText}; {detail})  [{why}]");
    if (ok)
    {
        reachable.Add(address);
    }
}
Console.WriteLine();

// ---------------------------------------------------------------- 6. identity, via production
Console.WriteLine("[6] IDENTITY  (production HotspotConnector: adb disconnect -> connect -> get-state -> get-serialno)");
if (noConnect)
{
    Console.WriteLine("    skipped: --no-connect was given.");
}
else if (reachable.Count == 0)
{
    Console.WriteLine($"    skipped: nothing accepted a TCP connection on {HotspotConnector.AdbTcpPort}, so there is no ADB");
    Console.WriteLine("    endpoint to identify. On a phone that has never had `adb tcpip 5555` run on it by");
    Console.WriteLine("    hand, this is the expected result and says nothing about the hotspot link itself.");
}
else
{
    var connector = new HotspotConnector();
    foreach (var address in reachable)
    {
        var result = await connector.ConnectAsync(address, expectedSerial, CancellationToken.None);
        Console.WriteLine($"    endpoint       : {result.Endpoint}");
        Console.WriteLine($"    adb connect    : {result.ConnectOutput.ReplaceLineEndings(" | ")}");
        Console.WriteLine($"    get-state      : {(string.IsNullOrEmpty(result.State) ? "(not run)" : result.State)}");
        Console.WriteLine($"    get-serialno   : {(string.IsNullOrEmpty(result.SerialNo) ? "(not run)" : result.SerialNo)}");
        Console.WriteLine($"    identity ok    : {result.IdentityVerified}");
        Console.WriteLine($"    elapsed        : {result.ElapsedMs} ms");
        if (result.Failure is not null)
        {
            Console.WriteLine($"    failure        : {result.Failure}");
        }
        Console.WriteLine();
    }
}

Console.WriteLine();
Console.WriteLine("=========================================================================");
Console.WriteLine(" End of probe. Nothing above changed a network, firewall or phone setting.");
Console.WriteLine("=========================================================================");
return 0;

// ---------------------------------------------------------------- helpers

bool Remove(string flag)
{
    var found = arguments.RemoveAll(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) > 0;
    return found;
}

string? RemoveValue(string flag)
{
    var index = arguments.FindIndex(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= arguments.Count)
    {
        return null;
    }
    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

/// <summary>
/// ToolLocator first — the app's own search order. A `dotnet run` output folder sits four
/// levels below the repo root rather than the six a Debug WinUI build sits at, so
/// ToolLocator's dev-tree candidate does not resolve from here; the walk-up below finds the
/// bundled copy that production would actually prefer.
/// </summary>
static string? FindAdb()
{
    var root = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(root, "DESKTOP")) && Directory.GetParent(root) is not null)
    {
        root = Directory.GetParent(root)!.FullName;
    }
    var bundled = Path.Combine(root, "SCRCPY", "Linc.scrcpy", "bin", "adb.exe");
    return File.Exists(bundled) ? bundled : ToolLocator.FindAdb();
}

/// <summary>
/// Our own address on the interface the target belongs to, for source binding. Uses the
/// production same-subnet test, which is scope-id aware — binding a link-local IPv6 source
/// from the wrong adapter fails with AddressNotAvailable, and every fe80:: address matches
/// every other one on prefix bits alone.
/// </summary>
static IPAddress? BindAddressFor(IPAddress target, IReadOnlyList<HotspotInterface> interfaces)
{
    foreach (var iface in interfaces)
    {
        if (IPAddress.TryParse(iface.Address, out var mine) &&
            mine.AddressFamily == target.AddressFamily &&
            HotspotAddress.IsSameSubnet(target.ToString(), iface.Address, iface.PrefixLength))
        {
            return mine;
        }
    }
    return null;
}

static async Task<(bool Ok, long ElapsedMs, string Detail)> TryTcpAsync(
    IPAddress target, int port, IPAddress? bindTo, TimeSpan timeout)
{
    using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    var started = Stopwatch.StartNew();
    try
    {
        if (bindTo is not null)
        {
            socket.Bind(new IPEndPoint(bindTo, 0));
        }
        using var cts = new CancellationTokenSource(timeout);
        await socket.ConnectAsync(new IPEndPoint(target, port), cts.Token);
        return (true, started.ElapsedMilliseconds, "accepted");
    }
    catch (OperationCanceledException)
    {
        return (false, started.ElapsedMilliseconds, $"no answer within {timeout.TotalSeconds:0} s");
    }
    catch (SocketException ex)
    {
        return (false, started.ElapsedMilliseconds, ex.SocketErrorCode.ToString());
    }
    catch (Exception ex)
    {
        return (false, started.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
    }
}

static string Run(string exe, params string[] arguments)
{
    var info = new ProcessStartInfo(exe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }
    try
    {
        using var process = Process.Start(info);
        if (process is null)
        {
            return "(could not start)";
        }
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(20_000);
        return text.Trim().ReplaceLineEndings("\n");
    }
    catch (Exception ex)
    {
        return $"({ex.GetType().Name}: {ex.Message})";
    }
}

static (List<(string Alias, string Category)> Profiles, List<(string Name, string Enabled, string Inbound)> Firewall)?
    ReadNetworkProfiles()
{
    // Read-only: both cmdlets report state and neither takes a Set-. Shelling out to
    // PowerShell keeps this to a dozen lines instead of the NLM COM interop the same
    // information would otherwise need.
    const string script =
        "$p = Get-NetConnectionProfile | ForEach-Object { [pscustomobject]@{ Alias = [string]$_.InterfaceAlias; Category = [string]$_.NetworkCategory } };" +
        "$f = Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Name = [string]$_.Name; Enabled = [string]$_.Enabled; Inbound = [string]$_.DefaultInboundAction } };" +
        "[pscustomobject]@{ Profiles = @($p); Firewall = @($f) } | ConvertTo-Json -Depth 4 -Compress";
    var json = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);
    try
    {
        using var document = JsonDocument.Parse(json);
        var profiles = new List<(string, string)>();
        foreach (var element in document.RootElement.GetProperty("Profiles").EnumerateArray())
        {
            profiles.Add((element.GetProperty("Alias").GetString() ?? "?", element.GetProperty("Category").GetString() ?? "?"));
        }
        var firewall = new List<(string, string, string)>();
        foreach (var element in document.RootElement.GetProperty("Firewall").EnumerateArray())
        {
            firewall.Add((
                element.GetProperty("Name").GetString() ?? "?",
                element.GetProperty("Enabled").GetString() ?? "?",
                element.GetProperty("Inbound").GetString() ?? "?"));
        }
        return (profiles, firewall);
    }
    catch (Exception)
    {
        return null;
    }
}
