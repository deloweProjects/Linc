namespace Linc.Desktop.Services;

public interface IHotspotPromotion
{
    /// <summary>
    /// Asks adbd to rebind to the fixed port 5555 over an ADB link that is ALREADY up, so later
    /// link-ups on this boot need no announcement at all. Returns true only when adb reported
    /// the restart. Never throws.
    /// </summary>
    Task<bool> PromoteAsync(string endpoint, CancellationToken ct);
}

/// <summary>
/// Promotion to a fixed port (M13c §2.3) — <b>an optimisation, never the primary path.</b>
/// <para>
/// Once the desktop holds any working ADB connection it may issue <c>tcpip 5555</c>; adbd
/// rebinds to a stable port and the next link-up can be dialled speculatively with no
/// announcement. <b>It does not survive a reboot</b>, so the TLS/announce path from M13b stays
/// the thing that must work. If anything in this feature ever starts depending on 5555 being
/// there, the design has been inverted and this class is where that shows up.
/// </para>
/// <para>
/// <b>This lives in its own file on purpose.</b> M13a's harness asserts that
/// <c>HotspotConnect.cs</c> contains no <c>tcpip</c> argument anywhere — the guarantee that the
/// connect path cannot arm a phone. M13c adds arming as a deliberate, separate capability, so
/// rather than weaken that check the new capability got a new file and its own check. See the
/// M13c report.
/// </para>
/// <para>
/// It runs against a phone only when a caller passes a live endpoint. Nothing constructs a
/// promotion attempt on its own, and no harness invokes it.
/// </para>
/// </summary>
public sealed class HotspotPromotion(ILogService log) : IHotspotPromotion
{
    /// <summary>The port `tcpip` is asked for — the same constant the connect path dials.</summary>
    public const int FixedPort = HotspotConnector.AdbTcpPort;

    private const string TcpIpVerb = "tcpip";

    public async Task<bool> PromoteAsync(string endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }
        var adb = AdbProcess.Find();
        if (adb is null)
        {
            return false;
        }
        // `-s <endpoint>` is mandatory: an unqualified tcpip would pick whichever device adb
        // felt like, which on a machine with two phones attached is somebody else's.
        var output = await AdbProcess.RunAsync(adb, ct, "-s", endpoint, TcpIpVerb, FixedPort.ToString());
        var ok = output.Contains("restarting in TCP mode", StringComparison.OrdinalIgnoreCase);
        // Both outcomes are Info: a failed promotion costs nothing, because it is an
        // optimisation on top of a path that already works.
        log.Log(LogLevel.Info,
            ok
                ? $"Wireless debugging on {endpoint} is now on the fixed port {FixedPort} until the phone reboots."
                : $"Couldn't move {endpoint} to the fixed port {FixedPort}; the announce path still works. ({Summarise(output)})");
        return ok;
    }

    private static string Summarise(string output) =>
        string.IsNullOrWhiteSpace(output) ? "no output" : output.ReplaceLineEndings(" ").Trim();
}
