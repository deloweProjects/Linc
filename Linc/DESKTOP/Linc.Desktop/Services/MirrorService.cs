using System.Diagnostics;

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
    private (string Serial, string Title, MirrorSettings Settings)? _session;

    public Task StartAsync(string serial, string windowTitle, MirrorSettings settings)
    {
        lock (_lock)
        {
            if (State == MirrorState.Running)
            {
                return Task.CompletedTask;
            }
            var scrcpyPath = ToolLocator.FindScrcpy()
                ?? throw new LincException(
                    "Linc couldn't find its screen-mirroring engine (scrcpy) on this PC. " +
                    "Install it with:  winget install Genymobile.scrcpy  — then try again.");
            log.Log(LogLevel.Info, $"Resolved scrcpy at: {scrcpyPath}");
            _session = (serial, windowTitle, settings);
            _stopRequested = false;
            _autoRestarts = 0;
            SessionCount++;
            LaunchLocked(scrcpyPath);
        }
        return Task.CompletedTask;
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

    public void Dispose()
    {
        _ = StopAsync();
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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnProcessExited(process);
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
        _process = process;
        _startedUtc = DateTime.UtcNow;
        SetState(MirrorState.Running);
        log.Log(LogLevel.Info, $"Screen mirroring started ({settings.VideoBitRate}, max {settings.MaxSize})");
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
        if (s.MaxSize > 0)
        {
            args.Add("-m");
            args.Add(s.MaxSize.ToString());
        }
        args.Add("--window-title");
        args.Add("Linc");
        args.Add("--window-borderless");
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

            if (cleanExit)
            {
                SetState(MirrorState.Stopped);
                log.Log(LogLevel.Info, "Screen mirroring stopped");
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
            ErrorRaised?.Invoke(
                "Screen mirroring stopped unexpectedly. Check that the phone is still " +
                "connected and unlocked, then start mirroring again.");
        }
    }

    private void SetState(MirrorState newState)
    {
        if (State != newState)
        {
            State = newState;
            StateChanged?.Invoke();
        }
    }
}
