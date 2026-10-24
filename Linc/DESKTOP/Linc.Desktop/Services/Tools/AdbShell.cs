using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace Linc.Desktop.Services.Tools;

/// <summary>
/// The real <see cref="IAdbShell"/>: Linc's own adb server (port 5038, <see cref="AdbServerHost"/>)
/// for shell and file copies, and the bundled adb.exe for exec-out and adb's own verbs. Talks to
/// any phone adb sees, so Tools works even when the Linc app isn't running on the phone.
/// </summary>
public sealed class AdbShell(IAdbServerHost server, ILogService log) : IAdbShell
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly AdbClient _adb = new();
    private readonly ConcurrentDictionary<string, DeviceData> _devices = new();
    // Three at once per phone, so a long command (a backup listing, a bug report) can't hold up a
    // button press behind it.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _limits = new();

    public bool IsFake => false;

    public async Task<IReadOnlyList<ToolsDevice>> DevicesAsync(CancellationToken ct)
    {
        try
        {
            await server.EnsureRunningAsync(ct);
            var list = new List<ToolsDevice>();
            foreach (var d in await _adb.GetDevicesAsync(ct))
            {
                if (d.State is not (DeviceState.Online or DeviceState.Unauthorized)) continue;
                _devices[d.Serial] = d;
                var model = string.IsNullOrWhiteSpace(d.Model) ? "Phone" : d.Model.Replace('_', ' ');
                // Wireless debugging serials are ip:port or an mDNS name; a cable's is plain.
                var usb = !d.Serial.Contains(':') && !d.Serial.Contains("._adb-tls-");
                list.Add(new ToolsDevice(d.Serial, model, usb, d.State == DeviceState.Online));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Said once per distinct reason, so a broken adb server is in the log, not silent.
            if (ex.Message != _lastListError) log.Log(LogLevel.Warn, $"Tools: couldn't list phones from adb: {ex.Message}");
            _lastListError = ex.Message;
            return [];
        }
    }

    private string? _lastListError;

    public async Task<ShellResult> RunAsync(string serial, string command, TimeSpan? timeout = null, bool secret = false, CancellationToken ct = default)
    {
        log.Log(LogLevel.Info, secret ? "Tools: sent typed text (not logged)" : $"Tools: {command}");
        if (!_devices.TryGetValue(serial, out var device))
        {
            return new ShellResult(false, "The phone isn't connected.", command);
        }
        var limit = _limits.GetOrAdd(serial, _ => new SemaphoreSlim(3));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await limit.WaitAsync(cts.Token);
            try
            {
                var receiver = new ConsoleOutputReceiver();
                await _adb.ExecuteRemoteCommandAsync(command, device, receiver, cts.Token);
                return new ShellResult(true, receiver.ToString().TrimEnd(), command);
            }
            finally
            {
                limit.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ShellResult(false, "The phone took too long to answer.", command);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"Tools: command failed: {ex.Message}");
            return new ShellResult(false, "Couldn't reach the phone. Check the cable or wireless debugging.", command);
        }
    }

    public async Task<byte[]?> ExecOutAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        log.Log(LogLevel.Info, $"Tools: exec-out {command}");
        var adb = server.AdbPath ?? AdbProcess.Find();
        if (adb is null) return null;
        var info = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-s", serial, "exec-out", command }) info.ArgumentList.Add(arg);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null) return null;
            using var buffer = new MemoryStream();
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cts.Token);
            await process.WaitForExitAsync(cts.Token);
            _ = await stderr;
            return process.ExitCode == 0 && buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public async Task<bool> PullAsync(string serial, string remote, string local, IProgress<double>? progress, CancellationToken ct)
    {
        if (!_devices.TryGetValue(serial, out var device)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            using var sync = new SyncService(_adb, device);
            await using (var stream = File.Create(local))
            {
                await sync.PullAsync(remote, stream,
                    args => progress?.Report(args.TotalBytesToReceive > 0 ? args.ReceivedBytesSize * 100.0 / args.TotalBytesToReceive : 0),
                    false, ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(local); } catch { }
            if (ex is OperationCanceledException) throw;
            log.Log(LogLevel.Warn, $"Tools: copy of {remote} failed: {ex.Message}");
            return false;
        }
    }

    public async Task<ShellResult> AdbAsync(string serial, string[] args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var command = "adb " + string.Join(' ', args);
        log.Log(LogLevel.Info, $"Tools: {command}");
        var adb = server.AdbPath ?? AdbProcess.Find();
        if (adb is null) return new ShellResult(false, "Linc couldn't find adb.", command);
        var text = await AdbProcess.RunAsync(adb, timeout ?? TimeSpan.FromSeconds(60), ct, ["-s", serial, .. args]);
        return new ShellResult(!text.Contains(" failed: ", StringComparison.Ordinal), text, command);
    }
}
