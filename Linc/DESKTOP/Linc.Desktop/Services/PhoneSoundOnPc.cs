using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// The phone's sound on this PC, the way the mirror already does it: scrcpy with no picture
/// (<c>--no-video --no-window</c>) and <c>--audio-dup</c>, so the phone keeps playing too. The
/// owner, after hearing the mirror do it with no delay: "mirror phone screen adds sound to
/// mirror, just take sound from there". No recording prompt, no Bluetooth pairing. Android 13+
/// for the duplicate; scrcpy needs an adb route (USB or wireless debugging), so a Direct-only
/// link falls back to the Bluetooth speaker route (<see cref="PcBluetoothAudio"/>).
/// Same surface as <see cref="PcBluetoothAudio"/>, so the v29 <c>pc.btaudio.*</c> messages and
/// the Device page drive it unchanged.
/// </summary>
public sealed class PhoneSoundOnPc(IConnectionManager connection, PcBluetoothAudio bluetooth, ILogService log)
{
    private readonly object _lock = new();
    private Process? _process;
    private string? _device;
    private string? _error;
    private bool _stopping;
    private bool _attached;

    /// <summary>Raised when the sound starts, stops or fails, from any thread.</summary>
    public event Action? Changed;

    public bool IsOn => _process is not null || bluetooth.IsOn;
    public string? Device => _process is not null ? _device : bluetooth.Device;
    public string? Error => _error ?? bluetooth.Error;

    /// <summary>{on, supported, device?, error?} — the v29 pc.btaudio.state payload.</summary>
    public JsonObject State() => new()
    {
        ["on"] = IsOn,
        ["supported"] = true,
        ["device"] = Device,
        ["error"] = IsOn ? null : Error,
    };

    public async Task SetAsync(bool on, IReadOnlyList<string> names)
    {
        Attach();
        if (!on)
        {
            Stop("stopped");
            if (bluetooth.IsOn) await bluetooth.SetAsync(false, names);
            _error = null;
            Changed?.Invoke();
            return;
        }
        var live = connection.Current;
        var scrcpy = ToolLocator.FindScrcpy();
        if (live is null || live.Transport == LinkTransport.DirectTls || scrcpy is null)
        {
            // No adb route for scrcpy: the Bluetooth speaker is the way left.
            log.Log(LogLevel.Info, "Phone sound: no USB or wireless-debugging link for the mirror's sound; trying Bluetooth");
            await bluetooth.SetAsync(true, names);
            _error = null;
            return; // bluetooth raises its own Changed
        }
        Start(scrcpy, live);
        Changed?.Invoke();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        bluetooth.Changed += () => Changed?.Invoke();
    }

    private void Start(string scrcpy, ConnectedDevice live)
    {
        lock (_lock)
        {
            if (_process is not null) return;
            var start = new ProcessStartInfo(scrcpy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (ToolLocator.FindAdb() is { } adb) start.EnvironmentVariables["ADB"] = adb;
            foreach (var arg in new[] { "-s", live.Serial, "--no-video", "--no-window", "--no-control", "--audio-dup" })
                start.ArgumentList.Add(arg);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var lastLine = "";
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastLine = e.Data.Trim(); };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastLine = e.Data.Trim(); };
            process.Exited += (_, _) => OnExited(process, () => lastLine);
            try
            {
                process.Start();
                ChildProcessJob.Add(process, log);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _error = "failed";
                log.Log(LogLevel.Warn, $"Phone sound: couldn't start the mirror's sound ({ex.Message})");
                process.Dispose();
                return;
            }
            _process = process;
            _stopping = false;
            _device = live.Model;
            _error = null;
            log.Log(LogLevel.Info, $"Phone sound: {live.Model} now plays on this PC too (the mirror's sound, no picture)");
        }
    }

    private void OnExited(Process process, Func<string> lastLine)
    {
        bool current;
        lock (_lock)
        {
            current = ReferenceEquals(process, _process);
            if (current)
            {
                _process = null;
                if (!_stopping)
                {
                    // Android 12 and older can't duplicate the sound; scrcpy says so and exits.
                    var line = lastLine();
                    _error = line.Contains("Android 13", StringComparison.OrdinalIgnoreCase) ? "needs-android-13" : "stopped";
                    log.Log(LogLevel.Info, $"Phone sound: the mirror's sound stopped ({(line.Length > 0 ? line : $"exit {SafeExitCode(process)}")})");
                }
            }
        }
        // Stop() lets go of the process before killing it, so this is the one place that frees it,
        // however it ended (it used to return early here and leak the handle on every Stop).
        try { process.Dispose(); } catch { }
        if (current) Changed?.Invoke();
    }

    private void Stop(string why)
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            if (process is null) return;
            _stopping = true;
            _process = null;
        }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or Windows refused (it is exiting): OnExited still frees it.
            log.Log(LogLevel.Info, $"Phone sound: scrcpy was already ending ({ex.GetType().Name})");
        }
        log.Log(LogLevel.Info, $"Phone sound: stopped playing on this PC ({why})");
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); } catch { return "?"; }
    }
}
