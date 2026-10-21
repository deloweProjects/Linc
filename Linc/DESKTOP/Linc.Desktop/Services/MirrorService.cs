using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linc.Desktop.Services;

public enum MirrorState { Stopped, Running }

public sealed record MirrorPreset(string Label, int MaxSize, string BitRate);

public interface IMirrorService : IDisposable
{
    MirrorState State { get; }
    IReadOnlyList<MirrorPreset> Presets { get; }

    /// <summary>How many mirroring sessions have run this process, and their combined duration.</summary>
    int SessionCount { get; }
    TimeSpan TotalSessionDuration { get; }

    /// <summary>May fire on background threads.</summary>
    event Action? StateChanged;

    /// <summary>May fire on background threads.</summary>
    event Action<string>? ErrorRaised;

    Task StartAsync(string serial, string windowTitle, MirrorSettings settings);
    Task StopAsync();

    /// <summary>Puts back a phone display that "Fill this monitor" resized, when a session was
    /// cut short (Linc closed or crashed, cable pulled). Safe to call on every connection: it does
    /// nothing unless this phone has something left to put back.</summary>
    Task RestoreLeftoverAsync(string serial);
}

/// <summary>
/// Owns the scrcpy child process (docs/DECISIONS.md D-004): launches it against the
/// connected device with no console window, watches for unexpected exits, and
/// auto-restarts sessions that die after having run healthily.
/// </summary>
public sealed class MirrorService(ILogService log) : IMirrorService
{
    public IReadOnlyList<MirrorPreset> Presets { get; } =
    [
        new("High quality (native, 12 Mbps)", 0, "12M"),
        new("Balanced (1280p, 8 Mbps)", 1280, "8M"),
        new("Performance (1024p, 4 Mbps)", 1024, "4M"),
    ];

    public MirrorState State { get; private set; }
    public int SessionCount { get; private set; }
    public TimeSpan TotalSessionDuration { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? ErrorRaised;

    private readonly object _lock = new();
    private Process? _process;
    private bool _stopRequested;
    private int _autoRestarts;
    private DateTime _startedUtc;
    private bool _starting;
    private (string Serial, string Title, MirrorSettings Settings)? _session;

    public async Task StartAsync(string serial, string windowTitle, MirrorSettings settings)
    {
        string scrcpyPath;
        lock (_lock)
        {
            if (State == MirrorState.Running || _starting)
            {
                return;
            }
            scrcpyPath = ToolLocator.FindScrcpy()
                ?? throw new LincException(
                    "Linc couldn't find its screen-mirroring engine (scrcpy) on this PC. " +
                    "Install it with:  winget install Genymobile.scrcpy  — then try again.");
            _starting = true;
            _stopRequested = false;
        }
        try
        {
            log.Log(LogLevel.Info, $"Resolved scrcpy at: {scrcpyPath}");
            // A session cut short earlier may have left the phone resized; put it back first so
            // the values saved below are always the user's own.
            await Task.Run(() => RestoreDisplay(serial));
            if (settings.FillsMonitor())
            {
                await Task.Run(() => ApplyMonitorShape(serial, settings.ScreenShape == MirrorSettings.ShapeMonitorLandscape));
            }
            lock (_lock)
            {
                if (_stopRequested)
                {
                    // Stopped while the phone was being resized: don't open the window.
                    _ = Task.Run(() => RestoreDisplay(serial));
                    return;
                }
                _session = (serial, windowTitle, settings);
                _autoRestarts = 0;
                SessionCount++;
                LaunchLocked(scrcpyPath);
            }
        }
        finally
        {
            lock (_lock)
            {
                _starting = false;
            }
        }
    }

    public Task RestoreLeftoverAsync(string serial)
    {
        lock (_lock)
        {
            if (State == MirrorState.Running || _starting)
            {
                return Task.CompletedTask;
            }
        }
        return Task.Run(() => RestoreDisplay(serial));
    }

    public Task StopAsync()
    {
        Process? process;
        lock (_lock)
        {
            _stopRequested = true;
            process = _process;
        }
        if (process is null)
        {
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            try
            {
                // Ask the scrcpy window to close; force-kill if it doesn't oblige.
                if (!process.CloseMainWindow() || !process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });
    }

    /// <summary>Stops the mirror and puts the phone's display back. Synchronous (up to a few
    /// seconds) so Exit can wait for it before the process ends.</summary>
    public void Dispose()
    {
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Best effort; the leftover is restored at the next connection.
        }
        RestoreDisplay(null);
    }

    private void LaunchLocked(string scrcpyPath)
    {
        var (serial, title, settings) = _session!.Value;
        var startInfo = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // no console; scrcpy opens its own video window
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var adbPath = ToolLocator.FindAdb();
        if (adbPath is not null)
        {
            startInfo.EnvironmentVariables["ADB"] = adbPath; // scrcpy honours this
        }
        var iconPath = ToolLocator.FindScrcpyIcon();
        if (iconPath is not null)
        {
            startInfo.EnvironmentVariables["SCRCPY_ICON_PATH"] = iconPath;
        }
        foreach (var arg in BuildScrcpyArgs(serial, settings))
        {
            startInfo.ArgumentList.Add(arg);
        }
        // The phone keeps its own sound while the PC plays it too (scrcpy --audio-dup, Android 13+;
        // older phones just mirror without sound). The owner: "it plays on pc with no hearable
        // delay but stops play on phone". Outside BuildScrcpyArgs so the harness's byte-identity
        // check of the saved settings stays as it was.
        // 1.0.0.18: "Silence the phone while mirroring" leaves --audio-dup off, so scrcpy's
        // default source (output) plays only here and the phone goes quiet: no echo between the
        // phone's speaker and the PC's slightly later copy.
        if (settings.AudioEnabled && string.IsNullOrWhiteSpace(settings.AudioSource) && !settings.MutePhone)
        {
            startInfo.ArgumentList.Add("--audio-dup");
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnProcessExited(process);
        // scrcpy's own lines (renderer, encoder, audio warnings) go to Linc's log, so a stutter or
        // a silent mirror can be read from Settings > Logs instead of guessed at.
        process.OutputDataReceived += (_, e) => LogScrcpyLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogScrcpyLine(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception)
        {
            SetState(MirrorState.Stopped);
            ErrorRaised?.Invoke("Linc couldn't start screen mirroring. Reinstall scrcpy and try again.");
            return;
        }
        try
        {
            // Decoding and drawing get the CPU ahead of background work, so a busy PC doesn't
            // delay frames.
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception)
        {
            // Not fatal: normal priority is what it always ran at.
        }
        _process = process;
        _startedUtc = DateTime.UtcNow;
        SetState(MirrorState.Running);
        var fills = settings.FillsMonitor();
        var sound = !settings.AudioEnabled ? "no sound"
            : settings.MutePhone && string.IsNullOrWhiteSpace(settings.AudioSource) ? "sound here only"
            : "sound on";
        log.Log(LogLevel.Info,
            $"Screen mirroring started ({settings.VideoBitRate}, max {(fills ? "native" : settings.MaxSize.ToString())}" +
            $"{(fills ? ", filling the monitor" : "")}, {sound})");
    }

    /// <summary>
    /// Pure builder for the scrcpy argument list from a <see cref="MirrorSettings"/> record,
    /// kept here so the <c>mirrorsettingssim</c> harness can verify the args without spawning
    /// a process. Mirrors the historical behaviour: <c>-s</c>, <c>-b <bitrate></c>,
    /// <c>-m <maxsize></c> (when non-zero), <c>--window-title Linc --window-borderless</c>,
    /// plus the optional flags. <c>title</c> is fixed to <c>"Linc"</c> as it always was — scrcpy
    /// uses it for the video window caption.
    /// </summary>
    public static IReadOnlyList<string> BuildScrcpyArgs(string serial, MirrorSettings s)
    {
        var args = new List<string>
        {
            "-s",
            serial,
            "-b",
            s.VideoBitRate,
        };
        // Filling the monitor streams at the phone's (monitor-sized) display resolution: a
        // smaller -m would be scaled back up and blur.
        if (s.MaxSize > 0 && !s.FillsMonitor())
        {
            args.Add("-m");
            args.Add(s.MaxSize.ToString());
        }
        args.Add("--window-title");
        args.Add("Linc");
        args.Add("--window-borderless");
        if (s.FillsMonitor())
        {
            args.Add("--fullscreen");
        }
        if (s.MaxFps > 0)
        {
            args.Add($"--max-fps={s.MaxFps}");
        }
        if (!string.IsNullOrWhiteSpace(s.Crop))
        {
            args.Add($"--crop={s.Crop}");
        }
        if (s.StayAwake)
        {
            args.Add("--stay-awake");
        }
        if (s.TurnScreenOff)
        {
            args.Add("--turn-screen-off");
        }
        if (s.ShowTouches)
        {
            args.Add("--show-touches");
        }
        // M11: AudioEnabled defaults true, matching scrcpy's own default (audio forwarded, phone
        // muted) with no flag at all — the byte-identity rule (A2.3). Only a false value or a
        // non-default source/bit-rate ever adds a flag.
        if (!s.AudioEnabled)
        {
            args.Add("--no-audio");
        }
        else
        {
            if (s.AudioBitRate != 0)
            {
                args.Add($"--audio-bit-rate={s.AudioBitRate}");
            }
            if (!string.IsNullOrWhiteSpace(s.AudioSource))
            {
                args.Add($"--audio-source={s.AudioSource}");
            }
        }
        return args;
    }

    private void OnProcessExited(Process process)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(process, _process))
            {
                return; // stale handler from a previous session
            }
            _process = null;
            var uptime = DateTime.UtcNow - _startedUtc;
            TotalSessionDuration += uptime;
            var cleanExit = _stopRequested || process.ExitCode == 0;
            process.Dispose();

            var serial = _session?.Serial;
            if (cleanExit)
            {
                SetState(MirrorState.Stopped);
                log.Log(LogLevel.Info, "Screen mirroring stopped");
                _ = Task.Run(() => RestoreDisplay(serial));
                return;
            }

            // Crash recovery: sessions that ran healthily get restarted quietly;
            // immediate crashes point at a real problem the user must see.
            if (uptime > TimeSpan.FromSeconds(10) && _autoRestarts < 2 && _session is not null)
            {
                _autoRestarts++;
                var scrcpyPath = ToolLocator.FindScrcpy();
                if (scrcpyPath is not null)
                {
                    log.Log(LogLevel.Warn, "Screen mirroring crashed after a healthy run; restarting");
                    LaunchLocked(scrcpyPath);
                    return;
                }
            }

            SetState(MirrorState.Stopped);
            log.Log(LogLevel.Error, "Screen mirroring stopped unexpectedly");
            _ = Task.Run(() => RestoreDisplay(serial));
            ErrorRaised?.Invoke(
                "Screen mirroring stopped unexpectedly. Check that the phone is still " +
                "connected and unlocked, then start mirroring again.");
        }
    }

    private void LogScrcpyLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            log.Log(line.Contains("ERROR", StringComparison.Ordinal) ? LogLevel.Warn : LogLevel.Info, $"scrcpy: {line.Trim()}");
        }
    }

    // ---- "Fill this monitor" (1.0.0.18) ----
    // The phone's display is resized (adb shell wm size) to this PC's monitor resolution in the
    // phone's own orientation, so turned sideways it is exactly the monitor's shape and the
    // full-screen mirror has no black bars. What was there before is written to a file first and
    // put back when mirroring stops, at Exit, or at the next connection after a crash or a pulled
    // cable: wm size survives a phone reboot, so it must never be left behind.

    /// <summary>The largest long side the phone is asked to draw. A 4K monitor gets 2560x1440
    /// (scaled up by the PC): phones draw and encode that comfortably, 3840x2160 they often don't.</summary>
    internal const int MaxShapeLongSide = 2560;

    /// <summary>Directory for the restore file (<see cref="IDeviceRegistry.RootPath"/>), set at
    /// startup by App. Only a harness that never sets it falls back to the temp folder: the store
    /// root must come from the registry alone (cleanroomsim guards LocalApplicationData lookups).</summary>
    public static string? StoreRoot { get; set; }

    private static readonly object DisplayGate = new();

    private static string RestoreFile => Path.Combine(
        StoreRoot ?? Path.Combine(Path.GetTempPath(), "Linc"),
        "mirror-display-restore.json");

    /// <summary>Pure: the <c>wm size</c> to ask for, in the phone's natural orientation, for a
    /// monitor of <paramref name="monitorW"/>x<paramref name="monitorH"/>. Even numbers, long
    /// side at most <see cref="MaxShapeLongSide"/>; (0, 0) when any input is unusable.</summary>
    internal static (int Width, int Height) MonitorShapeFor(int phoneW, int phoneH, int monitorW, int monitorH)
    {
        if (phoneW <= 0 || phoneH <= 0 || monitorW <= 0 || monitorH <= 0)
        {
            return (0, 0);
        }
        double longSide = Math.Max(monitorW, monitorH);
        double shortSide = Math.Min(monitorW, monitorH);
        if (longSide > MaxShapeLongSide)
        {
            shortSide = shortSide * MaxShapeLongSide / longSide;
            longSide = MaxShapeLongSide;
        }
        var l = (int)Math.Round(longSide) & ~1;
        var sh = (int)Math.Round(shortSide) & ~1;
        return phoneW <= phoneH ? (sh, l) : (l, sh);
    }

    /// <summary>Pure: reads "Physical size: 1080x2400" and an optional "Override size: ..." from
    /// <c>wm size</c> output.</summary>
    internal static (int Width, int Height, string? Override) ParseWmSize(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return (0, 0, null);
        }
        var physical = Regex.Match(output, @"Physical size:\s*(\d+)x(\d+)");
        var over = Regex.Match(output, @"Override size:\s*(\d+x\d+)");
        return physical.Success
            ? (int.Parse(physical.Groups[1].Value), int.Parse(physical.Groups[2].Value), over.Success ? over.Groups[1].Value : null)
            : (0, 0, null);
    }

    private void ApplyMonitorShape(string serial, bool landscape)
    {
        lock (DisplayGate)
        {
            try
            {
                var (monitorW, monitorH) = (GetSystemMetrics(0), GetSystemMetrics(1));
                var (ok, sizeOutput) = AdbShell(serial, "wm size");
                var (phoneW, phoneH, existingOverride) = ParseWmSize(sizeOutput);
                var (w, h) = MonitorShapeFor(phoneW, phoneH, monitorW, monitorH);
                if (!ok || w == 0)
                {
                    log.Log(LogLevel.Warn, $"Fill this monitor: couldn't read the phone's screen size ('{sizeOutput?.Trim()}') or the monitor's ({monitorW}x{monitorH}); mirroring in the phone's own shape.");
                    return;
                }
                string? accel = null, userRotation = null;
                if (landscape)
                {
                    accel = AdbShell(serial, "settings get system accelerometer_rotation").Output?.Trim();
                    userRotation = AdbShell(serial, "settings get system user_rotation").Output?.Trim();
                }
                // Saved before anything changes, so a crash at any point still has the way back.
                // The hardware serial finds the phone again over another link (USB vs Wi-Fi).
                var saved = new JsonObject
                {
                    ["serial"] = serial,
                    ["hardware"] = HardwareSerial(serial),
                    ["override"] = existingOverride,
                    ["landscape"] = landscape,
                    ["accelerometer_rotation"] = accel,
                    ["user_rotation"] = userRotation,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(RestoreFile)!);
                File.WriteAllText(RestoreFile, saved.ToJsonString());

                var sized = AdbShell(serial, $"wm size {w}x{h}").Ok;
                if (landscape)
                {
                    AdbShell(serial, "settings put system accelerometer_rotation 0");
                    AdbShell(serial, "settings put system user_rotation 1");
                }
                log.Log(sized ? LogLevel.Info : LogLevel.Warn,
                    $"Fill this monitor: phone {phoneW}x{phoneH} drawn at {w}x{h} for the {monitorW}x{monitorH} monitor" +
                    $"{(landscape ? ", turned sideways" : "")}{(sized ? "" : " (the phone refused the new size)")}.");
            }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"Fill this monitor: {ex.Message}");
            }
        }
    }

    /// <summary>Puts back what <see cref="ApplyMonitorShape"/> changed. <paramref name="onlySerial"/>
    /// null means whichever phone it was. Keeps the file when the phone can't be reached, so the
    /// next connection tries again.</summary>
    private void RestoreDisplay(string? onlySerial)
    {
        lock (DisplayGate)
        {
            try
            {
                if (!File.Exists(RestoreFile))
                {
                    return;
                }
                var saved = JsonNode.Parse(File.ReadAllText(RestoreFile));
                var serial = saved?["serial"]?.GetValue<string>();
                if (string.IsNullOrEmpty(serial))
                {
                    File.Delete(RestoreFile);
                    return;
                }
                if (onlySerial is not null && !string.Equals(serial, onlySerial, StringComparison.Ordinal))
                {
                    var hardware = saved?["hardware"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(hardware) || !string.Equals(hardware, HardwareSerial(onlySerial), StringComparison.Ordinal))
                    {
                        return; // another phone, or this one over a link that can't be asked
                    }
                    serial = onlySerial; // the same phone over another link
                }
                var over = saved?["override"]?.GetValue<string>();
                var ok = AdbShell(serial, over is null ? "wm size reset" : $"wm size {over}").Ok;
                if (saved?["landscape"]?.GetValue<bool>() == true)
                {
                    var accel = saved["accelerometer_rotation"]?.GetValue<string>();
                    var userRotation = saved["user_rotation"]?.GetValue<string>();
                    ok &= AdbShell(serial, $"settings put system accelerometer_rotation {(accel is "0" ? "0" : "1")}").Ok;
                    ok &= AdbShell(serial, userRotation is null || !int.TryParse(userRotation, out _)
                        ? "settings delete system user_rotation"
                        : $"settings put system user_rotation {userRotation}").Ok;
                }
                if (ok)
                {
                    File.Delete(RestoreFile);
                    log.Log(LogLevel.Info, "Fill this monitor: the phone's screen is back to its own size.");
                }
                else
                {
                    log.Log(LogLevel.Warn, "Fill this monitor: couldn't reach the phone to put its screen size back; trying again at the next connection.");
                }
            }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"Fill this monitor: restoring failed: {ex.Message}");
            }
        }
    }

    /// <summary>One <c>adb -s serial shell</c> command through Linc's own adb (its private server
    /// port comes from this process's environment). Never throws.</summary>
    private static (bool Ok, string? Output) AdbShell(string serial, string command)
    {
        var adb = ToolLocator.FindAdb();
        if (adb is null)
        {
            return (false, null);
        }
        try
        {
            var info = new ProcessStartInfo(adb)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-s");
            info.ArgumentList.Add(serial);
            info.ArgumentList.Add("shell");
            info.ArgumentList.Add(command);
            using var p = Process.Start(info);
            if (p is null)
            {
                return (false, null);
            }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return (false, null);
            }
            var output = stdout.Result + stderr.Result;
            return (p.ExitCode == 0 && !output.Contains("Exception", StringComparison.Ordinal), output);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private static string? HardwareSerial(string serial)
    {
        var (ok, output) = AdbShell(serial, "getprop ro.serialno");
        var value = output?.Trim();
        return ok && !string.IsNullOrEmpty(value) ? value : null;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private void SetState(MirrorState newState)
    {
        if (State != newState)
        {
            State = newState;
            StateChanged?.Invoke();
        }
    }
}
