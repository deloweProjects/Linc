using System.IO;
using AdvancedSharpAdbClient;

namespace Linc.Desktop.Services;

/// <summary>
/// One QR pairing attempt: the QR payload tells the phone which service instance
/// name to advertise and which password to expect (the same scheme Android Studio uses).
/// </summary>
public sealed record PairingSession(string ServiceName, string Password, string QrText);

public interface IPairingService
{
    PairingSession CreateQrSession();
    Task PairAsync(string host, int port, string code, CancellationToken ct);
}

public sealed class PairingService(IAdbServerHost adbServerHost, ILogService log) : IPairingService
{
    private readonly AdbClient _adb = new();

    public PairingSession CreateQrSession()
    {
        var serviceName = $"linc-{Random.Shared.Next(100_000, 1_000_000)}";
        var password = Random.Shared.Next(100_000, 1_000_000).ToString();
        return new PairingSession(serviceName, password, $"WIFI:T:ADB;S:{serviceName};P:{password};;");
    }

    public async Task PairAsync(string host, int port, string code, CancellationToken ct)
    {
        await adbServerHost.EnsureRunningAsync(ct);
        string result;
        try
        {
            result = await _adb.PairAsync(host, port, code, ct);
        }
        catch (Exception first) when (first is not LincException and not OperationCanceledException)
        {
            // The ADB service, not the phone, is the usual culprit here — it was gone or got
            // dropped mid-request — and the code is still unused, so bring the service back and
            // try once more before blaming the code (D-078).
            log.Log(LogLevel.Info, $"Pairing with {host}:{port} hit a problem with Linc's ADB service ({first.Message}); restarting it and trying again.");
            try
            {
                await adbServerHost.EnsureRunningAsync(ct);
                result = await _adb.PairAsync(host, port, code, ct);
            }
            catch (Exception ex) when (ex is not LincException and not OperationCanceledException)
            {
                log.Log(LogLevel.Error, $"Pairing with {host}:{port} failed: {ex.Message}");
                throw new LincException(PairFailed, ex);
            }
        }
        if (!result.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            log.Log(LogLevel.Error, $"Pairing with {host}:{port} failed: {result}");
            throw new LincException(PairFailed, new IOException(result));
        }
        log.Log(LogLevel.Info, $"Paired with {host}:{port}");
    }

    private const string PairFailed =
        "Pairing didn't work. The code may have expired — close and reopen the pairing " +
        "dialog on the phone, then try again.";
}
