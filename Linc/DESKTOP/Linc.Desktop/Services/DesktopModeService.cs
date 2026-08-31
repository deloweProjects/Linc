using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace Linc.Desktop.Services;

public enum DesktopModeSetupResult
{
    AlreadyDone,
    RebootRequired,
    Success,
    Failed
}

public interface IDesktopModeService
{
    /// <summary>
    /// Checks the 4 ADB settings on device. Returns true if all 4 read "1".
    /// </summary>
    Task<bool> IsSetupAppliedAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Idempotently sets the 4 ADB settings. If already set, returns AlreadyDone.
    /// If newly written, returns RebootRequired.
    /// After writing, verifies that all 4 stuck; if not, logs via ILogService and returns Failed.
    /// </summary>
    Task<DesktopModeSetupResult> EnsureSetupAppliedAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Reboots the device over ADB and polls for it to reappear within 120 seconds.
    /// Throws LincException on timeout or error.
    /// </summary>
    Task RebootAndWaitAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Reads the phone's real display metrics (<c>wm size</c> / <c>wm density</c>) over ADB shell
    /// and returns <paramref name="current"/> with the geometry fields recomputed according to
    /// <see cref="AspectRatioMode"/>. Cap 1600x900, clamp 640–3840, DPI 120–320. Never throws;
    /// on any read/parse failure logs at Warning and returns <paramref name="current"/> unchanged.
    /// </summary>
    Task<DesktopModeSettings> GetRecommendedSettingsAsync(
        DeviceData device, DesktopModeSettings current, CancellationToken ct);

    /// <summary>
    /// The phone's real <c>wm size</c> / <c>wm density</c>, or null when they cannot be read or
    /// parsed (no ADB on this link, phone locked, unexpected output). Never throws. M7's per-app
    /// windows need the phone's SHAPE so an app window looks like a phone; this is the one place
    /// that reads it, and the caller decides what to do when it is unavailable.
    /// </summary>
    Task<(int Width, int Height, int Dpi)?> GetPhoneDisplayMetricsAsync(
        DeviceData device, CancellationToken ct);
}

public sealed class DesktopModeService(IAdbServerHost adbHost, ILogService log) : IDesktopModeService
{
    private readonly AdbClient _adb = new();

    private static readonly (string Namespace, string Key)[] RequiredSettings =
    [
        ("global", "force_resizable_activities"),
        ("global", "enable_freeform_support"),
        ("global", "force_desktop_mode_on_external_displays"),
        ("secure", "desktop_mode")
    ];

    public async Task<bool> IsSetupAppliedAsync(DeviceData device, CancellationToken ct)
    {
        await adbHost.EnsureRunningAsync(ct);
        foreach (var (ns, key) in RequiredSettings)
        {
            var value = await GetSettingAsync(device, ns, key, ct);
            if (value != "1")
            {
                return false;
            }
        }
        return true;
    }

    public async Task<DesktopModeSetupResult> EnsureSetupAppliedAsync(DeviceData device, CancellationToken ct)
    {
        await adbHost.EnsureRunningAsync(ct);

        // 1. Check idempotency before writing
        var readValues = new Dictionary<string, string>();
        var allApplied = true;

        foreach (var (ns, key) in RequiredSettings)
        {
            var val = await GetSettingAsync(device, ns, key, ct);
            readValues[$"{ns}:{key}"] = val;
            if (val != "1")
            {
                allApplied = false;
            }
        }

        if (allApplied)
        {
            return DesktopModeSetupResult.AlreadyDone;
        }

        // 2. Write missing settings
        foreach (var (ns, key) in RequiredSettings)
        {
            if (readValues[$"{ns}:{key}"] != "1")
            {
                await PutSettingAsync(device, ns, key, "1", ct);
            }
        }

        // 3. Confirm all four stuck
        var failedKeys = new List<string>();
        foreach (var (ns, key) in RequiredSettings)
        {
            var val = await GetSettingAsync(device, ns, key, ct);
            if (val != "1")
            {
                failedKeys.Add($"{ns} {key}");
            }
        }

        if (failedKeys.Count > 0)
        {
            var failedList = string.Join(", ", failedKeys);
            log.Log(LogLevel.Error, $"Failed to enable Desktop Mode ADB settings on device: {failedList}");
            return DesktopModeSetupResult.Failed;
        }

        // Trial-and-error approach: newly written settings require reboot
        return DesktopModeSetupResult.RebootRequired;
    }

    public async Task RebootAndWaitAsync(DeviceData device, CancellationToken ct)
    {
        await adbHost.EnsureRunningAsync(ct);
        log.Log(LogLevel.Info, $"Rebooting device '{device.Serial}' for Desktop Mode setup...");

        try
        {
            await ShellAsync(device, "reboot", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Error, $"ADB reboot command failed for device '{device.Serial}': {ex.Message}");
            throw new LincException("Linc was unable to send the reboot command to your phone.", ex);
        }

        // Bounded poll of device list for up to 120 seconds
        var timeoutAt = DateTime.UtcNow.AddSeconds(120);
        var targetSerial = device.Serial;

        while (DateTime.UtcNow < timeoutAt)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            try
            {
                var devices = await _adb.GetDevicesAsync(ct);
                var onlineDevice = devices.FirstOrDefault(d =>
                    d.Serial == targetSerial && d.State == DeviceState.Online);

                if (onlineDevice is not null)
                {
                    log.Log(LogLevel.Info, $"Device '{targetSerial}' is back online after reboot.");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // During reboot ADB server may momentarily drop connection or device transitions state
            }
        }

        log.Log(LogLevel.Error, $"Timed out waiting for device '{targetSerial}' to reboot.");
        throw new LincException("Timed out waiting for your phone to finish rebooting. Please make sure your phone is unlocked and connected.");
    }

    public async Task<(int Width, int Height, int Dpi)?> GetPhoneDisplayMetricsAsync(
        DeviceData device, CancellationToken ct)
    {
        string? sizeOutput = null;
        string? densityOutput = null;
        try
        {
            await adbHost.EnsureRunningAsync(ct);
            sizeOutput = await ShellAsync(device, "wm size", ct);
            densityOutput = await ShellAsync(device, "wm density", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"Could not read phone display metrics: {ex.Message}");
            return null;
        }

        var (w, h, dpi, ok) = ParseWmOutput(sizeOutput, densityOutput);
        if (!ok)
        {
            log.Log(LogLevel.Warn, $"Could not parse phone display metrics (wm size='{sizeOutput}', wm density='{densityOutput}').");
            return null;
        }
        return (w, h, dpi);
    }

    public async Task<DesktopModeSettings> GetRecommendedSettingsAsync(
        DeviceData device, DesktopModeSettings current, CancellationToken ct)
    {
        if (await GetPhoneDisplayMetricsAsync(device, ct) is not { } metrics)
        {
            log.Log(LogLevel.Warn, "Leaving Desktop Mode settings unchanged — the phone's display metrics were unreadable.");
            return current;
        }
        var (phoneW, phoneH, phoneDpi) = metrics;

        // MatchMonitor uses the PC primary monitor's aspect ratio; MatchPhone uses the phone's
        // reported aspect ratio. Custom leaves geometry untouched (spec).
        Func<(int Width, int Height)>? monitorResolver = null;
        if (current.AspectRatioMode == AspectRatioMode.MatchMonitor)
        {
            var (mw, mh) = TryGetPrimaryMonitorResolution();
            if (mw > 0 && mh > 0)
            {
                monitorResolver = () => (mw, mh);
            }
        }

        var (newW, newH, newDpi) = ComputeRecommendedGeometry(
            current, phoneW, phoneH, phoneDpi, monitorResolver);

        log.Log(LogLevel.Info, $"Desktop Mode recommended geometry from phone {phoneW}x{phoneH}/{phoneDpi}: {newW}x{newH}/{newDpi} (mode={current.AspectRatioMode})");
        return current with
        {
            VirtualDisplayWidth = newW,
            VirtualDisplayHeight = newH,
            VirtualDisplayDpi = newDpi
        };
    }

    /// <summary>
    /// The one geometry routine, now living on the dependency-free <see cref="DesktopModeSettings"/>
    /// record so M7's per-app windows (and <c>applaunchsim</c>) can call the same code without
    /// dragging the ADB client in. Kept here as a forwarder because <c>desktopsim</c> — and the
    /// habit of every reader — looks for it on this service.
    /// </summary>
    internal static (int Width, int Height, int Dpi) ComputeRecommendedGeometry(
        DesktopModeSettings current,
        int phoneWidth, int phoneHeight, int phoneDpi,
        Func<(int Width, int Height)>? getMonitorResolution = null,
        ILogService? log = null) =>
        DesktopModeSettings.ComputeRecommendedGeometry(
            current, phoneWidth, phoneHeight, phoneDpi, getMonitorResolution, log);

    /// <summary>
    /// Parses <c>wm size</c> and <c>wm density</c> output. <c>wm size</c> line format:
    /// "Physical size: 1080x2400" (override line "Override size:" if set takes precedence).
    /// <c>wm density</c>: "Physical density: 420" (override wins). Returns false if unparseable.
    /// </summary>
    internal static (int Width, int Height, int Dpi, bool Parsed) ParseWmOutput(string? sizeOutput, string? densityOutput)
    {
        int width = 0, height = 0, dpi = 0;

        if (!string.IsNullOrWhiteSpace(sizeOutput))
        {
            foreach (var line in sizeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                // Override size takes precedence over Physical size.
                if (!trimmed.StartsWith("Override size:", StringComparison.Ordinal)
                    && !trimmed.StartsWith("Physical size:", StringComparison.Ordinal))
                {
                    continue;
                }
                var colon = trimmed.IndexOf(':');
                if (colon < 0 || colon + 1 >= trimmed.Length) continue;
                var rest = trimmed.AsSpan().Slice(colon + 1).Trim();
                var xIdx = rest.IndexOf('x');
                if (xIdx <= 0 || xIdx + 1 >= rest.Length) continue;
                if (int.TryParse(rest.Slice(0, xIdx), out var w)
                    && int.TryParse(rest.Slice(xIdx + 1), out var h)
                    && w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                    // Override seen first wins; for a single Physical line just take it.
                    if (trimmed.StartsWith("Override size:", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(densityOutput))
        {
            foreach (var line in densityOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Override density:", StringComparison.Ordinal)
                    && !trimmed.StartsWith("Physical density:", StringComparison.Ordinal))
                {
                    continue;
                }
                var colon = trimmed.IndexOf(':');
                if (colon < 0 || colon + 1 >= trimmed.Length) continue;
                var rest = trimmed.AsSpan().Slice(colon + 1).Trim();
                if (int.TryParse(rest, out var d) && d > 0)
                {
                    dpi = d;
                    if (trimmed.StartsWith("Override density:", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
        }

        bool parsed = width > 0 && height > 0 && dpi > 0;
        return (width, height, dpi, parsed);
    }

    /// <summary>Reads the primary monitor's physical resolution via GetSystemMetrics (SM_CXSCREEN/
    /// SM_CYSCREEN return physical px under PerMonitorV2). Returns (0,0) if unavailable.</summary>
    internal static (int Width, int Height) TryGetPrimaryMonitorResolution()
    {
        try
        {
            int w = User32Helper.GetSystemMetrics(0);
            int h = User32Helper.GetSystemMetrics(1);
            return (w, h);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static class User32Helper
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    private async Task<string> GetSettingAsync(DeviceData device, string ns, string key, CancellationToken ct)
    {
        var output = await ShellAsync(device, $"settings get {ns} {key}", ct);
        return output.Trim();
    }

    private async Task PutSettingAsync(DeviceData device, string ns, string key, string value, CancellationToken ct)
    {
        await ShellAsync(device, $"settings put {ns} {key} {value}", ct);
    }

    private async Task<string> ShellAsync(DeviceData device, string command, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(command, device, receiver, ct);
            return receiver.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"ADB command '{command}' failed on device '{device.Serial}': {ex.Message}");
            return $"error: {ex.Message}";
        }
    }
}
