using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

public interface IAppLaunchService : IDisposable
{
    /// <summary>True while a window for this package is open (M7a: one window per app, keyed by package).</summary>
    bool IsOpen(string package);

    /// <summary>Packages with a window open right now — the view's "this one is already up" state.</summary>
    IReadOnlyCollection<string> OpenPackages { get; }

    /// <summary>Raised when a window opens or closes. May fire on background threads.</summary>
    event Action? WindowsChanged;

    /// <summary>Plain-language failures only (2.6). May fire on background threads.</summary>
    event Action<string>? ErrorRaised;

    /// <summary>How many app windows are open right now — the Apps section's counter (M7b).</summary>
    int OpenWindowCount { get; }

    /// <summary>
    /// Opens <paramref name="package"/> in its own PC window, or focuses the window it already has.
    /// <paramref name="phoneMetrics"/> is the phone's real size/density; pass null when it could not
    /// be read and a phone-shaped fallback is used.
    /// </summary>
    Task LaunchAsync(
        string serial, string package, string label, (int Width, int Height, int Dpi)? phoneMetrics);

    /// <summary>Closes every app window — what a disconnect does (2.7).</summary>
    Task CloseAllAsync();
}

/// <summary>
/// One scrcpy process per app window (D-059). Modelled on <see cref="DesktopLaunchService"/> —
/// same process ownership, same exit watching, same plain-language errors — but holding a
/// DICTIONARY of package → process, because several apps are open at once by design.
///
/// There is deliberately NO <c>am start</c> and no ADB shell anywhere in this path: scrcpy's own
/// <c>--start-app=+&lt;package&gt;</c> creates the virtual display, starts the app on it, and tears
/// the display down when the window closes, so there are no display ids to track or leak.
/// </summary>
/// <param name="spawn">
/// How a configured <see cref="ProcessStartInfo"/> becomes a running process. Production leaves
/// this null and gets <see cref="StartScrcpy"/>. <c>applaunchsim</c> passes a stand-in so the
/// one-window-per-package rule, the exit cleanup and the disconnect teardown are exercised for
/// real — without a phone, and without ever starting scrcpy.
/// </param>
/// <param name="windows">
/// Where each app's window rectangle is remembered (M7b). Null means "no memory": scrcpy places
/// every window itself, which is exactly the first-launch behaviour.
/// </param>
/// <param name="monitors">
/// How the current monitor layout is read, for the off-screen check. Production leaves this null
/// and gets <see cref="AppWindowStore.CurrentMonitors"/>; a harness injects a fixed layout so the
/// discard rule can be proven without unplugging anything.
/// </param>
public sealed class AppLaunchService(
    ILogService log,
    Func<ProcessStartInfo, Process>? spawn = null,
    AppWindowStore? windows = null,
    Func<IReadOnlyList<(int X, int Y, int Width, int Height)>>? monitors = null)
    : IAppLaunchService
{
    private readonly Func<ProcessStartInfo, Process> _spawn = spawn ?? StartScrcpy;
    private readonly bool _spawnOverridden = spawn is not null;

    private static Process StartScrcpy(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static class User32
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    }

    /// <summary>
    /// The shape used when the phone's own <c>wm size</c>/<c>wm density</c> could not be read (no
    /// ADB shell on this link, phone locked). A common 6" phone; the window still looks like a
    /// phone, which is the whole point of the MatchPhone rule.
    /// </summary>
    internal static readonly (int Width, int Height, int Dpi) FallbackPhoneMetrics = (1080, 2400, 420);

    /// <summary>
    /// M9e (B3.2): a cap on simultaneous app windows. <b>Not derived from a documented scrcpy or
    /// Android limit</b> — none exists in the vendored source (<c>SCRCPY/Linc.scrcpy/src</c>) or its
    /// docs. Measured directly this session instead: 8 concurrent <c>--new-display --start-app</c>
    /// windows on a Pixel 7 (Android 17) stayed up with no crash, so a hard per-window platform
    /// ceiling is NOT what the owner hit. What measurement DID find is real: (1) a virtual display
    /// whose owning scrcpy process is force-killed is never torn down on the phone — orphaned
    /// displays accumulate across a session with no code path to clean them up, and (2) running
    /// several full Android apps at once visibly pressures phone memory (background services and
    /// renderer processes were observed dying/restarting under 8 concurrent real apps in logcat).
    /// Both push the same direction — resource pressure that compounds the longer a session runs —
    /// without a single clean "N is where it breaks" number to point at. This cap is a precaution
    /// against that compounding, not a proven threshold; picked comfortably below where pressure was
    /// observed. See the M9e report for the full measurement.
    /// </summary>
    internal const int MaxConcurrentWindows = 6;

    /// <summary>
    /// One open app window: the process, the phone it belongs to, and the last rectangle its
    /// window was seen at. See <see cref="SampleGeometryAsync"/> for why the rectangle is sampled
    /// while the window lives rather than read when it dies.
    /// </summary>
    private sealed class OpenWindow(Process process, string serial)
    {
        public Process Process { get; } = process;
        public string Serial { get; } = serial;
        public AppWindowGeometry? LastSeen { get; set; }
        public CancellationTokenSource Sampling { get; } = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, OpenWindow> _windows = [];
    private readonly HashSet<string> _closeRequested = [];

    public event Action? WindowsChanged;
    public event Action<string>? ErrorRaised;

    public bool IsOpen(string package)
    {
        lock (_lock)
        {
            return _windows.ContainsKey(package);
        }
    }

    public IReadOnlyCollection<string> OpenPackages
    {
        get
        {
            lock (_lock)
            {
                return [.. _windows.Keys];
            }
        }
    }

    public int OpenWindowCount
    {
        get
        {
            lock (_lock)
            {
                return _windows.Count;
            }
        }
    }

    /// <summary>
    /// Pure builder for one app window's command line — the <see cref="DisplayPayload"/> /
    /// <see cref="MirrorService.BuildScrcpyArgs"/> pattern, so <c>applaunchsim</c> asserts the exact
    /// arguments without spawning anything. Geometry comes from the ONE shared routine
    /// (<see cref="DesktopModeSettings.ComputeRecommendedGeometry"/>) in MatchPhone mode, so an app
    /// window is phone-shaped. The package is always passed with a leading <c>+</c> (force-stop
    /// first); the <c>?</c> fuzzy-name form is never used — v16's <c>apps.get</c> gives us the exact
    /// package, so guessing would only introduce a way to open the wrong app.
    /// </summary>
    /// <param name="geometry">
    /// Where this app's window was last time (M7b), already checked against the current monitor
    /// layout by the caller. Null means "not remembered", and then <b>none</b> of the four window
    /// flags is emitted: scrcpy's own placement beats any default we could invent.
    /// </param>
    public static IReadOnlyList<string> BuildScrcpyArgs(
        string serial,
        string package,
        string label,
        (int Width, int Height, int Dpi)? phoneMetrics,
        AppWindowGeometry? geometry = null,
        ILogService? log = null)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new ArgumentException("A package name is required to open an app window.", nameof(package));
        }

        var (phoneW, phoneH, phoneDpi) = phoneMetrics ?? FallbackPhoneMetrics;
        var (w, h, dpi) = DesktopModeSettings.ComputeRecommendedGeometry(
            new DesktopModeSettings(AspectRatioMode: AspectRatioMode.MatchPhone),
            phoneW, phoneH, phoneDpi, getMonitorResolution: null, log: log);

        // The window caption is the app's own label, never the package id (2.5) — the owner reads
        // "Camera" in the taskbar, not "com.google.android.GoogleCamera".
        var title = string.IsNullOrWhiteSpace(label) ? package : label;

        List<string> args =
        [
            "-s", serial,
            $"--new-display={w}x{h}/{dpi}",
            $"--start-app=+{package}",
            "--window-title", title,
            "--keyboard=uhid",
            "--mouse=sdk",
            "--stay-awake",
            // App windows never take the phone's audio: several can be open at once and the mirror
            // may be running too, and three copies of the same stream is a bug, not a feature.
            "--no-audio",
            // M9f (A2): bare app windows, not a second desktop — system decorations (a StatusBar on
            // the virtual display) are what made these look like Desktop Mode. Desktop Mode's own
            // launch path (DesktopLaunchService) is untouched; it is SUPPOSED to look like a desktop.
            "--no-vd-system-decorations",
        ];

        // All four or none. A half-remembered window (position without size) would move the window
        // somewhere the owner never put it, which is worse than letting scrcpy choose.
        if (geometry is { } g && g.IsPlausible)
        {
            args.Add($"--window-x={g.X}");
            args.Add($"--window-y={g.Y}");
            args.Add($"--window-width={g.Width}");
            args.Add($"--window-height={g.Height}");
        }

        return args;
    }

    public Task LaunchAsync(
        string serial, string package, string label, (int Width, int Height, int Dpi)? phoneMetrics)
    {
        lock (_lock)
        {
            if (_windows.TryGetValue(package, out var existing))
            {
                // 2.2: one window per app. Raise the one that exists rather than opening a second.
                FocusExisting(existing.Process, label);
                return Task.CompletedTask;
            }

            // M9e (B3.2): the measured concurrent-window cap — see MaxConcurrentWindows' doc for
            // why this number and not a documented platform limit. A NEW package is refused once
            // the cap is hit; focusing an already-open window (above) is never blocked by it.
            if (_windows.Count >= MaxConcurrentWindows)
            {
                ErrorRaised?.Invoke(
                    $"Linc already has {MaxConcurrentWindows} app windows open. Close one before opening " +
                    $"{Describe(label, package)} — too many at once can overload the phone.");
                return Task.CompletedTask;
            }

            // With a stand-in launcher (the harness) nothing is executed, so a PC without scrcpy
            // installed still exercises the bookkeeping. Production keeps the hard failure.
            var scrcpyPath = ToolLocator.FindScrcpy()
                ?? (_spawnOverridden ? "scrcpy" : null)
                ?? throw new LincException(
                    "Linc couldn't find its screen-mirroring engine (scrcpy) on this PC. " +
                    "Install it with:  winget install Genymobile.scrcpy  — then try again.");

            _closeRequested.Remove(package);
            LaunchLocked(scrcpyPath, serial, package, label, phoneMetrics);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The rectangle this app's window will be restored to, or null when there is nothing usable
    /// to restore. Public because it is the whole of the restore rule and <c>applaunchsim</c>
    /// proves it directly: a rectangle that is not remembered, not plausible, or no longer on any
    /// monitor all come back null, and a null means no window flags at all.
    /// </summary>
    public AppWindowGeometry? RememberedGeometryFor(string serial, string package)
    {
        if (windows is null)
        {
            return null;
        }
        var remembered = windows.Get(serial, package);
        if (remembered is not { } geometry)
        {
            return null;
        }
        var layout = (monitors ?? AppWindowStore.CurrentMonitors)();
        if (AppWindowStore.IsOnScreen(geometry, layout))
        {
            return geometry;
        }
        // Monitor unplugged, resolution changed, or the window was dragged off the desktop before
        // it closed. Forgetting is the kind failure: a window nobody can see reads as "it didn't
        // open" (2.6), and scrcpy's own placement is always visible.
        log.Log(LogLevel.Info,
            $"Apps: the remembered window position for {package} is off-screen now; letting the window place itself.");
        return null;
    }

    /// <summary>
    /// M9f (B2.1): how long <see cref="CloseAllAsync"/> waits for a graceful <c>WM_CLOSE</c> before
    /// escalating to <c>Kill</c>. A forced kill skips scrcpy's own virtual-display teardown (M9e
    /// measured orphaned displays 60/74/75 surviving a killed process), so graceful close is the
    /// leak fix and Kill is strictly a fallback for a window that will not go away on its own.
    /// </summary>
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// M9f (B2.1): the graceful-close decision, factored out as a pure static so <c>applaunchsim</c>
    /// calls the real decision rather than modelling it (BRAIN.md's "a harness that re-implements
    /// the guard it is testing proves nothing"). Trivial by itself — this IS the one place the
    /// decision is made, and the only one a future change to the escalation rule could touch.
    /// </summary>
    internal static bool ShouldKillAfterGracefulClose(bool exitedWithinTimeout) => !exitedWithinTimeout;

    public Task CloseAllAsync()
    {
        List<KeyValuePair<string, OpenWindow>> open;
        lock (_lock)
        {
            open = [.. _windows];
            foreach (var package in _windows.Keys)
            {
                _closeRequested.Add(package); // an intentional close must not read as a crash
            }
        }
        if (open.Count == 0)
        {
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            foreach (var (package, window) in open)
            {
                var process = window.Process;
                try
                {
                    var exitedWithinTimeout = process.CloseMainWindow()
                        && process.WaitForExit((int)GracefulCloseTimeout.TotalMilliseconds);
                    if (ShouldKillAfterGracefulClose(exitedWithinTimeout))
                    {
                        // Never silent (BRAIN.md) — a future session needs to be able to see this
                        // happening, since a graceful close that never succeeds is itself a bug.
                        log.Log(LogLevel.Warn,
                            $"Apps: {package}'s window did not close gracefully within " +
                            $"{GracefulCloseTimeout.TotalSeconds:0}s; killing it.");
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        });
    }

    public void Dispose() => _ = CloseAllAsync();

    private void LaunchLocked(
        string scrcpyPath, string serial, string package, string label,
        (int Width, int Height, int Dpi)? phoneMetrics)
    {
        var startInfo = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // no console; scrcpy opens its own (frameless, M3.5) window
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

        var geometry = RememberedGeometryFor(serial, package);
        foreach (var arg in BuildScrcpyArgs(serial, package, label, phoneMetrics, geometry, log))
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = _spawn(startInfo);
        }
        catch (Exception)
        {
            ErrorRaised?.Invoke($"Linc couldn't open {Describe(label, package)} on this PC. Reinstall scrcpy and try again.");
            return;
        }

        var window = new OpenWindow(process, serial);
        _windows[package] = window;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(package, window);
        log.Log(LogLevel.Info, $"Opened an app window for {package}");
        WindowsChanged?.Invoke();

        if (windows is not null)
        {
            _ = SampleGeometryAsync(window);
        }

        // A process that died between Start and the subscription above would never raise Exited,
        // and the entry would block every future launch of this app. Check once, explicitly.
        if (process.HasExited)
        {
            _ = Task.Run(() => OnProcessExited(package, window));
        }
    }

    /// <summary>How often an open window's rectangle is re-read. See <see cref="SampleGeometryAsync"/>.</summary>
    private static readonly TimeSpan GeometrySampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Keeps the last rectangle an open window was seen at, in memory only.
    ///
    /// <para><b>Why not simply read it when the process exits?</b> Because by then there is no
    /// window: the HWND is destroyed before <c>Exited</c> runs, <c>GetWindowRect</c> fails, and
    /// what we would persist is whatever garbage the out-parameter happened to hold. The window
    /// also belongs to another process, so there is no close event of ours to hook. So the
    /// rectangle is sampled cheaply while the window lives — one <c>GetWindowRect</c> a second,
    /// no allocation, no UI thread — and <b>nothing is written to disk until the window closes</b>,
    /// at which point the last good sample is saved once. Sampling that reads nothing usable
    /// (window minimised, never shown, already gone) leaves <see cref="OpenWindow.LastSeen"/> null
    /// and then <b>nothing is persisted at all</b> rather than a rectangle we guessed at.</para>
    /// </summary>
    private async Task SampleGeometryAsync(OpenWindow window)
    {
        try
        {
            while (!window.Sampling.IsCancellationRequested)
            {
                await Task.Delay(GeometrySampleInterval, window.Sampling.Token);
                if (ReadGeometry(window.Process) is { } sample)
                {
                    window.LastSeen = sample;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed; OnProcessExited persists whatever the last sample was.
        }
        catch (Exception ex)
        {
            // Never silent (BRAIN.md): losing the sampler means losing the window's position, and
            // that is worth one log line rather than a mystery.
            log.Log(LogLevel.Warn, $"Apps: stopped tracking a window's position: {ex.Message}");
        }
    }

    /// <summary>
    /// The window's current rectangle, or null when it cannot be read honestly. Position comes
    /// from the frame (<c>GetWindowRect</c>) and size from the client area
    /// (<c>GetClientRect</c>), because that is exactly the pair scrcpy's <c>--window-x/y</c> and
    /// <c>--window-width/height</c> mean. A minimised window reports its parked
    /// off-screen position, so those samples are refused rather than remembered.
    /// </summary>
    private static AppWindowGeometry? ReadGeometry(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return null;
            }
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero || User32.IsIconic(handle))
            {
                return null;
            }
            if (!User32.GetWindowRect(handle, out var frame) || !User32.GetClientRect(handle, out var client))
            {
                return null;
            }
            var geometry = new AppWindowGeometry(
                frame.Left, frame.Top, client.Right - client.Left, client.Bottom - client.Top);
            return geometry.IsPlausible ? geometry : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 2.2's "focus, don't duplicate". Best effort by design: the window belongs to another
    /// process, and Windows only lets a foreground-eligible caller raise one, so this can silently
    /// fail. Doing nothing is still the right outcome — the window IS open, and a second one would
    /// be a bug that looks like a feature.
    /// </summary>
    private void FocusExisting(Process process, string label)
    {
        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                log.Log(LogLevel.Info, $"{label} is already open; its window is still starting up.");
                return;
            }
            if (User32.IsIconic(handle))
            {
                User32.ShowWindow(handle, User32.SW_RESTORE);
            }
            User32.SetForegroundWindow(handle);
            log.Log(LogLevel.Info, $"{label} is already open; raised its existing window.");
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and here; the Exited handler will clean up.
        }
    }

    private void OnProcessExited(string package, OpenWindow window)
    {
        bool intentional;
        var process = window.Process;
        lock (_lock)
        {
            if (!_windows.TryGetValue(package, out var tracked) || !ReferenceEquals(tracked, window))
            {
                return; // stale handler from a window we already replaced
            }
            _windows.Remove(package);
            intentional = _closeRequested.Remove(package) || process.ExitCode == 0;
        }

        // The one disk write of the whole geometry feature, and it happens here: on close, with
        // the last rectangle the window was actually seen at. No sample, nothing written.
        window.Sampling.Cancel();
        if (windows is not null && window.LastSeen is { } geometry)
        {
            windows.Save(window.Serial, package, geometry);
        }
        window.Sampling.Dispose();
        process.Dispose();

        // Dropping the entry is what makes relaunching work, so it happens before anything else.
        WindowsChanged?.Invoke();

        if (intentional)
        {
            log.Log(LogLevel.Info, $"Closed the app window for {package}");
            return;
        }
        log.Log(LogLevel.Error, $"The app window for {package} closed unexpectedly");
        ErrorRaised?.Invoke(
            $"The window for {package} closed unexpectedly. Check that your phone is still " +
            "connected and unlocked, then open the app again.");
    }

    private static string Describe(string label, string package) =>
        string.IsNullOrWhiteSpace(label) ? package : label;
}
