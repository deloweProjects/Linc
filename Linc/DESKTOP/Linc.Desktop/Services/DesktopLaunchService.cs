using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

public enum DesktopLaunchState { Stopped, Running }

public interface IDesktopLaunchService : IDisposable
{
    DesktopLaunchState State { get; }
    event Action? StateChanged;
    event Action<string>? ErrorRaised;

    Task StartAsync(string serial, DesktopModeSettings settings);
    Task StopAsync();
}

public sealed class DesktopLaunchService(ILogService log) : IDesktopLaunchService
{
    private static class User32
    {
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    public DesktopLaunchState State { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? ErrorRaised;

    private readonly object _lock = new();
    private Process? _process;
    private bool _stopRequested;

    internal static (int Width, int Height, int Dpi) ResolveGeometry(
        DesktopModeSettings settings,
        Func<(int Width, int Height)>? getScreenResolution = null,
        ILogService? log = null)
    {
        if (settings.VirtualDisplayWidth != 0 &&
            settings.VirtualDisplayHeight != 0 &&
            settings.VirtualDisplayDpi != 0)
        {
            var clampedW = Math.Clamp(settings.VirtualDisplayWidth, 640, 3840);
            var clampedH = Math.Clamp(settings.VirtualDisplayHeight, 640, 3840);
            log?.Log(LogLevel.Info, $"Desktop Mode virtual display geometry (explicit settings): {clampedW}x{clampedH}/{settings.VirtualDisplayDpi}");
            return (clampedW, clampedH, settings.VirtualDisplayDpi);
        }

        int rawW, rawH;
        if (getScreenResolution is not null)
        {
            (rawW, rawH) = getScreenResolution();
        }
        else
        {
            rawW = User32.GetSystemMetrics(User32.SM_CXSCREEN);
            rawH = User32.GetSystemMetrics(User32.SM_CYSCREEN);
        }

        if (rawW <= 0) rawW = 1920;
        if (rawH <= 0) rawH = 1080;

        var scale = Math.Min(1600.0 / rawW, 900.0 / rawH);
        int targetW = scale < 1.0 ? (int)Math.Round(rawW * scale) : rawW;
        int targetH = scale < 1.0 ? (int)Math.Round(rawH * scale) : rawH;

        var w = Math.Clamp(targetW, 640, 3840);
        var h = Math.Clamp(targetH, 640, 3840);
        const int dpi = 200;

        log?.Log(LogLevel.Info, $"Desktop Mode virtual display geometry (computed fallback): {w}x{h}/{dpi}");
        return (w, h, dpi);
    }

    internal static List<string> BuildScrcpyArgs(
        string serial,
        DesktopModeSettings settings,
        Func<(int Width, int Height)>? getScreenResolution = null,
        ILogService? log = null)
    {
        var (w, h, dpi) = ResolveGeometry(settings, getScreenResolution, log);
        var mouseFlag = settings.CaptureMouse ? "--mouse=uhid" : "--mouse=sdk";
        var args = new List<string>
        {
            "-s", serial,
            $"--new-display={w}x{h}/{dpi}",
            mouseFlag,
            "--keyboard=uhid",
            "--window-title", "Linc Desktop",
            "--stay-awake",
            $"--max-fps={settings.MaxFps}",
            $"--video-bit-rate={settings.VideoBitRate}"
        };

        if (!settings.ForwardAudio)
        {
            args.Add("--no-audio");
        }

        return args;
    }

    public Task StartAsync(string serial, DesktopModeSettings settings)
    {
        lock (_lock)
        {
            if (State == DesktopLaunchState.Running)
            {
                return Task.CompletedTask;
            }
            var scrcpyPath = ToolLocator.FindScrcpy()
                ?? throw new LincException(
                    "Linc couldn't find its screen-mirroring engine (scrcpy) on this PC. " +
                    "Install it with:  winget install Genymobile.scrcpy  — then try again.");
            log.Log(LogLevel.Info, $"Resolved scrcpy at: {scrcpyPath}");
            _stopRequested = false;
            LaunchLocked(scrcpyPath, serial, settings);
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
                if (!process.CloseMainWindow() || !process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited
            }
        });
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private void LaunchLocked(string scrcpyPath, string serial, DesktopModeSettings settings)
    {
        var startInfo = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var adbPath = ToolLocator.FindAdb();
        if (adbPath is not null)
        {
            startInfo.EnvironmentVariables["ADB"] = adbPath;
        }
        var iconPath = ToolLocator.FindScrcpyIcon();
        if (iconPath is not null)
        {
            startInfo.EnvironmentVariables["SCRCPY_ICON_PATH"] = iconPath;
        }

        var args = BuildScrcpyArgs(serial, settings, null, log);
        foreach (var arg in args)
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
            SetState(DesktopLaunchState.Stopped);
            ErrorRaised?.Invoke("Linc couldn't start Desktop Mode. Reinstall scrcpy and try again.");
            return;
        }

        _process = process;
        SetState(DesktopLaunchState.Running);
        log.Log(LogLevel.Info, "Desktop Mode started.");
    }

    private void OnProcessExited(Process process)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(process, _process))
            {
                return;
            }
            _process = null;
            var cleanExit = _stopRequested || process.ExitCode == 0;
            process.Dispose();

            SetState(DesktopLaunchState.Stopped);
            if (cleanExit)
            {
                log.Log(LogLevel.Info, "Desktop Mode stopped");
            }
            else
            {
                log.Log(LogLevel.Error, "Desktop Mode stopped unexpectedly");
                ErrorRaised?.Invoke(
                    "Desktop Mode stopped unexpectedly. Check that the phone is still " +
                    "connected and unlocked, then try again.");
            }
        }
    }

    private void SetState(DesktopLaunchState newState)
    {
        if (State != newState)
        {
            State = newState;
            StateChanged?.Invoke();
        }
    }
}
