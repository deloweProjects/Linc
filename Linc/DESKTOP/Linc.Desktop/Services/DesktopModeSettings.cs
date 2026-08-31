using System.Text.RegularExpressions;

namespace Linc.Desktop.Services;

public enum AspectRatioMode
{
    MatchMonitor,
    MatchPhone,
    Custom
}

public enum DefaultWindowMode
{
    Freeform,
    Maximized,
    RememberLast
}

public sealed record DesktopModeSettings(
    int VirtualDisplayWidth = 0,
    int VirtualDisplayHeight = 0,
    int VirtualDisplayDpi = 0,
    AspectRatioMode AspectRatioMode = AspectRatioMode.MatchMonitor,
    /// <summary>No effect on devices that do not declare
    /// <c>android.software.freeform_window_management</c> (apps always run fullscreen on the
    /// second screen there). Retained for persisted-JSON compatibility and for future devices
    /// that may declare the feature. See decision D-053.</summary>
    DefaultWindowMode DefaultWindowMode = DefaultWindowMode.Freeform,
    /// <summary>No effect on devices that do not declare
    /// <c>android.software.freeform_window_management</c> (apps cannot be resized there). Retained
    /// for persisted-JSON compatibility and for future devices that may declare the feature.
    /// See decision D-053.</summary>
    bool ResizableWindows = true,
    /// <summary>No effect on devices that do not declare
    /// <c>android.software.freeform_window_management</c> (apps always launch fullscreen there).
    /// Retained for persisted-JSON compatibility and for future devices that may declare the
    /// feature. See decision D-053.</summary>
    bool AutoFullscreenApps = false,
    bool SetupCompleted = false,
    bool RebootPending = false,
    bool CaptureMouse = true,
    int MaxFps = 30,
    string VideoBitRate = "8M",
    bool ForwardAudio = false,
    /// <summary>No effect on devices that do not declare
    /// <c>android.software.freeform_window_management</c> (apps cannot launch freeform there).
    /// Retained for persisted-JSON compatibility and for future devices that may declare the
    /// feature. See decision D-053.</summary>
    bool LaunchAppsFreeform = true)
{
    /// <summary>
    /// Pure validation of the user-editable numeric/string fields. Returns null when everything is
    /// fine, otherwise a plain-language message. Width/height/dpi 0 mean "compute a fallback" and
    /// are allowed. Lives here (on the dep-free record) so the desktopsim harness can exercise it
    /// without pulling in WinUI / the ViewModel.
    /// </summary>
    public static readonly Regex BitRatePattern = new(@"^\d+[KM]$", RegexOptions.Compiled);

    /// <summary>
    /// Pure geometry calculation, shared by Desktop Mode's virtual display and M7's per-app
    /// windows. It lives on this dep-free record (rather than on <c>DesktopModeService</c>, which
    /// drags in the ADB client) so <c>applaunchsim</c> can call the very routine the app calls —
    /// there is exactly ONE geometry routine in this product and this is it.
    /// Inputs: the phone's real size (pixels) and DPI; an optional monitor-resolution resolver
    /// (used only when <see cref="AspectRatioMode.MatchMonitor"/>). The result's total pixel area
    /// is capped at 1,440,000 (the 1600x900 envelope) while preserving the source aspect ratio
    /// end-to-end; if a dimension would fall below 640 the result is scaled UP uniformly to reach
    /// that floor. Dimensions are rounded to even, then clamped to 640–3840 as a final safety net
    /// (a clamp that changes a value logs at Warning — the aspect was not preservable). DPI targets
    /// a desktop-like density: 160 (mdpi) scaled by chosenHeight/900, clamped to 120–320.
    /// <see cref="AspectRatioMode.Custom"/> leaves geometry untouched.
    /// </summary>
    public static (int Width, int Height, int Dpi) ComputeRecommendedGeometry(
        DesktopModeSettings current,
        int phoneWidth, int phoneHeight, int phoneDpi,
        Func<(int Width, int Height)>? getMonitorResolution = null,
        ILogService? log = null)
    {
        const int Min = 640, Max = 3840;
        const int MinDpi = 120, MaxDpi = 320;
        // Pixel budget: same performance envelope as the previous 1600x900 cap (1,440,000 px).
        const long Budget = 1600L * 900L;

        int baseW, baseH;
        switch (current.AspectRatioMode)
        {
            case AspectRatioMode.Custom:
                // Leave geometry untouched per spec.
                log?.Log(LogLevel.Info, "ComputeRecommendedGeometry: Custom mode — geometry left unchanged.");
                return (current.VirtualDisplayWidth, current.VirtualDisplayHeight, current.VirtualDisplayDpi);
            case AspectRatioMode.MatchPhone:
                baseW = phoneWidth;
                baseH = phoneHeight;
                break;
            case AspectRatioMode.MatchMonitor:
            default:
                if (getMonitorResolution is not null)
                {
                    var (mw, mh) = getMonitorResolution();
                    baseW = mw > 0 ? mw : phoneWidth;
                    baseH = mh > 0 ? mh : phoneHeight;
                }
                else
                {
                    // No resolver supplied (headless harness or no display): fall back to the
                    // phone's own aspect ratio so we still produce something sane.
                    baseW = phoneWidth;
                    baseH = phoneHeight;
                }
                break;
        }

        if (baseW <= 0) baseW = 1080;
        if (baseH <= 0) baseH = 1920;

        // Cap on a PIXEL BUDGET, preserving aspect end-to-end. Never adjust one axis alone.
        // scale = min(1.0, sqrt(budget / (baseW * baseH))).
        double baseArea = (double)baseW * (double)baseH;
        double scale = baseArea > Budget ? Math.Sqrt((double)Budget / baseArea) : 1.0;
        int targetW = (int)Math.Round(baseW * scale);
        int targetH = (int)Math.Round(baseH * scale);

        // If either dimension falls below the 640 floor, scale BOTH up uniformly until the smaller
        // reaches 640. This preserves the aspect ratio (never stretch one axis alone).
        int smaller = Math.Min(targetW, targetH);
        if (smaller < Min && smaller > 0)
        {
            double upScale = (double)Min / smaller;
            targetW = (int)Math.Round(targetW * upScale);
            targetH = (int)Math.Round(targetH * upScale);
        }

        // Round to even numbers (video encoders dislike odd dimensions). Adjust by ±1, never ±2.
        targetW = targetW - (targetW & 1);
        targetH = targetH - (targetH & 1);
        if (targetW < Min) targetW += 1;
        if (targetH < Min) targetH += 1;

        // Final hard 640–3840 safety clamp. If this ever changes a value the aspect ratio was not
        // preservable; log at Warning per spec.
        int clampedW = Math.Clamp(targetW, Min, Max);
        int clampedH = Math.Clamp(targetH, Min, Max);
        if (clampedW != targetW || clampedH != targetH)
        {
            log?.Log(LogLevel.Warn, $"ComputeRecommendedGeometry: hard clamp changed the result from {targetW}x{targetH} to {clampedW}x{clampedH}; aspect ratio could not be preserved (mode={current.AspectRatioMode}).");
        }
        targetW = clampedW;
        targetH = clampedH;

        // Desktop-like DPI from the chosen HEIGHT: 160 dpi (Android mdpi reference) scaled modestly
        // by chosenHeight / 900, clamped to 120–320. Decoupled from the phone's physical density
        // so the virtual display reads as a desktop, not an oversized finger-targeted phone.
        int dpi = (int)Math.Round(160.0 * targetH / 900.0);
        dpi = Math.Clamp(dpi, MinDpi, MaxDpi);

        log?.Log(LogLevel.Info, $"ComputeRecommendedGeometry: phone {phoneWidth}x{phoneHeight}/{phoneDpi} -> {targetW}x{targetH}/{dpi} (mode={current.AspectRatioMode}, dpi=round(160*h/900) clamped {MinDpi}-{MaxDpi})");
        return (targetW, targetH, dpi);
    }

    public static string? ValidateSettingsFields(int width, int height, int dpi, int maxFps, string bitRate)
    {
        if ((width != 0 && (width < 640 || width > 3840))
            || (height != 0 && (height < 640 || height > 3840)))
        {
            return "Width and height must be between 640 and 3840 pixels (or 0 to use a recommended value).";
        }
        if (dpi != 0 && (dpi < 120 || dpi > 320))
        {
            return "DPI must be between 120 and 320 (or 0 to use a recommended value).";
        }
        if (maxFps < 1 || maxFps > 120)
        {
            return "Maximum frame rate must be between 1 and 120.";
        }
        if (string.IsNullOrWhiteSpace(bitRate) || !BitRatePattern.IsMatch(bitRate))
        {
            return "Video bit rate must look like a number followed by K or M, for example 8M or 4K.";
        }
        return null;
    }
}
