using System.Diagnostics;
using System.Text;

namespace Linc.Desktop.Services;

/// <summary>What one hotspot connect attempt actually did, with the evidence attached.</summary>
/// <param name="Endpoint">The <c>ip:port</c> string that was handed to adb.</param>
/// <param name="Connected">`adb connect` reported "connected to".</param>
/// <param name="ConnectOutput">Verbatim `adb connect` output.</param>
/// <param name="State">Verbatim `adb get-state` output ("device" when usable).</param>
/// <param name="SerialNo">The phone's hardware serial (`adb shell getprop ro.serialno`), or the
/// empty string when the identity half could not be run.</param>
/// <param name="IdentityVerified">All three checks passed AND the serial was the expected one.</param>
/// <param name="ElapsedMs">Wall time for the whole disconnect/connect/verify sequence.</param>
/// <param name="Failure">Plain-language reason this attempt is not usable, or null on success.</param>
public sealed record HotspotConnectResult(
    string Endpoint,
    bool Connected,
    string ConnectOutput,
    string State,
    string SerialNo,
    bool IdentityVerified,
    long ElapsedMs,
    string? Failure);

public interface IHotspotConnector
{
    /// <summary>
    /// Attaches the ADB server to a phone at <paramref name="address"/> on the hotspot link
    /// and proves it is the phone we meant. Never throws for a connect failure: the failure
    /// is part of the result, because the caller is a diagnostic as often as it is the app.
    /// </summary>
    /// <param name="expectedSerial">Hardware serial to require, or null to only report it.</param>
    /// <param name="port">
    /// The port adbd is accepting on. Defaults to <see cref="HotspotConnector.AdbTcpPort"/>, which
    /// is what M13a assumed and what promotion produces — but the phone announces an ephemeral
    /// port on a first connection, and until M13e nothing carried it here, so every announcement
    /// was dialled at 5555 whatever it said.
    /// </param>
    Task<HotspotConnectResult> ConnectAsync(
        string address, string? expectedSerial, CancellationToken ct, int port = HotspotConnector.AdbTcpPort);

    /// <summary>`adb -s &lt;endpoint&gt; get-state`, verbatim and trimmed. Never throws.</summary>
    Task<string> GetStateAsync(string endpoint, CancellationToken ct);

    /// <summary>`adb devices`, verbatim. The health loop's ground truth (M13c §3.2).</summary>
    Task<string> ListDevicesAsync(CancellationToken ct);

    /// <summary>
    /// `adb disconnect &lt;endpoint&gt;`. Exposed on its own because the health loop must issue it
    /// BEFORE re-resolving, not only as part of a connect (M13c §3.2).
    /// </summary>
    Task DisconnectAsync(string endpoint, CancellationToken ct);
}

/// <summary>
/// Runs one adb command and hands back stdout+stderr. Shared by everything in this feature that
/// speaks to adb, so the process plumbing exists once.
/// </summary>
public static class AdbProcess
{
    /// <summary>The adb executable Linc is using, or null when none was found.</summary>
    public static string? Find() => ToolLocator.FindAdb();

    /// <summary>Runs adb and returns stdout+stderr, trimmed. Never throws.</summary>
    public static async Task<string> RunAsync(string adbPath, CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo(adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return "";
            }
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            return new StringBuilder(await stdout).Append(await stderr).ToString().Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A hung or missing adb is a failure like any other, reported through the returned
            // text rather than thrown into a caller that is usually a diagnostic.
            return $"adb {string.Join(' ', args)} failed: {ex.Message}";
        }
    }
}

/// <summary>
/// The hotspot-link connect path (docs/ROADMAP.md M13). Port 5555 is hardcoded and the phone
/// is assumed to have been armed by hand with <c>adb tcpip 5555</c> — discovery of the port
/// and self-arming are later milestones, and nothing here ever arms anything.
/// <para>
/// Two rules are the whole reason this is a service and not three inline calls:
/// </para>
/// <list type="number">
///   <item><description><b>Disconnect before every connect.</b> The ADB server caches an
///   endpoint after the phone behind it has gone, and then silently refuses to replace it
///   when the same ip:port comes back — the classic "it worked yesterday" failure.</description></item>
///   <item><description><b>Reachability is not identity.</b> Something answering on 5555 at
///   the gateway address of a hotspot is not necessarily our phone, so all three of
///   `connected to`, <c>get-state</c> == <c>device</c>, and <c>get-serialno</c> are checked.</description></item>
/// </list>
/// <para>
/// This talks to adb.exe rather than to <c>AdbClient</c> (which <see cref="ConnectionManager"/>
/// uses) so that the three verifications are literally the three adb commands they are
/// specified as, and so tools\hotspotprobe exercises exactly the production sequence.
/// Source-address binding is deliberately absent: the socket to the phone is opened by the
/// ADB <i>server</i>, not by this process, and adb exposes no way to bind it to an interface.
/// The one place binding IS available — a direct reachability probe — does it, in
/// tools\hotspotprobe.
/// </para>
/// </summary>
public sealed class HotspotConnector(ILogService? log = null) : IHotspotConnector
{
    /// <summary>The port `adb tcpip 5555` opens. Hardcoded by design this milestone.</summary>
    public const int AdbTcpPort = 5555;

    private const string DisconnectVerb = "disconnect";
    private const string ConnectVerb = "connect";
    private const string UsableState = "device";

    /// <summary>`ip:port`, with IPv6 literals bracketed the way adb expects. Delegates to
    /// <see cref="HotspotEndpoint.Build"/>, which never double-appends a port when the address
    /// already IS an endpoint — the malformed <c>ip:port:5555</c> the health loop used to aim at
    /// (M13f §5). One endpoint builder, shared with tools\hotspotsim.</summary>
    public static string BuildEndpoint(string address, int port = AdbTcpPort) => HotspotEndpoint.Build(address, port);

    public async Task<HotspotConnectResult> ConnectAsync(
        string address, string? expectedSerial, CancellationToken ct, int port = AdbTcpPort)
    {
        var endpoint = BuildEndpoint(address, port);
        var started = Stopwatch.StartNew();

        var adb = AdbProcess.Find();
        if (adb is null)
        {
            return new HotspotConnectResult(endpoint, false, "", "", "", false, started.ElapsedMilliseconds,
                "Linc couldn't find its ADB engine on this PC, so it can't reach the phone over the hotspot.");
        }

        // Rule 1 — ALWAYS first. A stale cached entry for this exact ip:port makes the connect
        // below succeed-looking and useless. tools\hotspotsim asserts this ordering by source
        // text; do not reorder these two calls.
        await RunAdbAsync(adb, ct, DisconnectVerb, endpoint);
        var connect = await RunAdbAsync(adb, ct, ConnectVerb, endpoint);

        var connected = connect.Contains("connected to", StringComparison.OrdinalIgnoreCase);
        if (!connected)
        {
            log?.Log(LogLevel.Warn, $"Hotspot connect to {endpoint} failed: {Summarise(connect)}");
            return new HotspotConnectResult(endpoint, false, connect, "", "", false, started.ElapsedMilliseconds,
                $"Nothing answered ADB at {endpoint}. The phone may not have wireless debugging armed on this link.");
        }

        // Rule 2 — reachability is not identity. `get-serialno` over TCP returns the ENDPOINT
        // (ip:port), not the phone, so comparing that against the paired serial proved nothing;
        // the hardware serial comes from getprop (M13f §3). `get-state` stays the liveness half.
        var state = await RunAdbAsync(adb, ct, "-s", endpoint, "get-state");
        var serialNo = await RunAdbAsync(adb, ct, "-s", endpoint, "shell", "getprop", "ro.serialno");
        var stateOk = string.Equals(state.Trim(), UsableState, StringComparison.Ordinal);
        var serialOk = HotspotAnnounce.VerifySerial(expectedSerial, serialNo);

        string? failure = null;
        if (!stateOk)
        {
            failure = $"ADB reached {endpoint} but the phone reported '{state.Trim()}' instead of '{UsableState}'.";
        }
        else if (!serialOk)
        {
            failure = $"Something else answered at {endpoint}: it identifies as '{serialNo.Trim()}', not the paired phone.";
        }
        if (failure is not null)
        {
            log?.Log(LogLevel.Warn, $"Hotspot connect to {endpoint} rejected: {failure}");
        }
        else
        {
            log?.Log(LogLevel.Info, $"Hotspot link up: {endpoint} is {serialNo.Trim()} ({started.ElapsedMilliseconds} ms)");
        }

        return new HotspotConnectResult(
            endpoint, connected, connect, state.Trim(), serialNo.Trim(),
            IdentityVerified: stateOk && serialOk, started.ElapsedMilliseconds, failure);
    }

    public async Task<string> GetStateAsync(string endpoint, CancellationToken ct)
    {
        var adb = AdbProcess.Find();
        return adb is null ? "" : (await RunAdbAsync(adb, ct, "-s", endpoint, "get-state")).Trim();
    }

    public async Task<string> ListDevicesAsync(CancellationToken ct)
    {
        var adb = AdbProcess.Find();
        return adb is null ? "" : await RunAdbAsync(adb, ct, "devices");
    }

    public async Task DisconnectAsync(string endpoint, CancellationToken ct)
    {
        var adb = AdbProcess.Find();
        if (adb is not null)
        {
            await RunAdbAsync(adb, ct, DisconnectVerb, endpoint);
        }
    }

    /// <summary>Runs one adb command and returns stdout+stderr, trimmed. Never throws.</summary>
    private static Task<string> RunAdbAsync(string adbPath, CancellationToken ct, params string[] args) =>
        AdbProcess.RunAsync(adbPath, ct, args);

    private static string Summarise(string output) =>
        string.IsNullOrWhiteSpace(output) ? "(no output)" : output.ReplaceLineEndings(" ").Trim();
}
