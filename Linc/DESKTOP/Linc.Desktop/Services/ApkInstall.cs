using System.IO;

namespace Linc.Desktop.Services;

/// <summary>
/// Pure, dependency-free helpers for the "Install app" flow (M10 Part A): staged-progress
/// labels, path validation before anything touches the phone, and translating a raw adb
/// install failure into plain language. Kept free of WinUI/AdvancedSharpAdbClient so
/// tools/apkinstallsim can exercise it directly — the ADB call itself lives in
/// PhoneSetupService, the UI staging lives in DeviceViewModel.
/// </summary>
public static class ApkInstall
{
    public readonly record struct ValidationResult(bool IsValid, string? Message);

    /// <summary>
    /// Checks everything that can be known before touching the phone (A2.3): the file exists,
    /// has a .apk extension, isn't empty, and a device is connected. Each failure gets its own
    /// message so the UI never shows a generic "something went wrong."
    /// </summary>
    public static ValidationResult ValidateApkPath(string? path, bool connected)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ValidationResult(false, "That file doesn't exist anymore.");
        }
        if (!string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationResult(false, "That file isn't an Android app (.apk).");
        }
        if (new FileInfo(path).Length == 0)
        {
            return new ValidationResult(false, "That file is empty.");
        }
        if (!connected)
        {
            return new ValidationResult(false, "No phone is connected.");
        }
        return new ValidationResult(true, null);
    }

    /// <summary>The stages of an install run, in the order they happen (A2.4).</summary>
    public enum Stage
    {
        Validating,
        CopyingToPhone,
        Installing,
        Done,
        Failed,
    }

    /// <summary>Display text for a stage — the single place the wording lives.</summary>
    public static string StageText(Stage stage) => stage switch
    {
        Stage.Validating => "Validating…",
        Stage.CopyingToPhone => "Copying to phone…",
        Stage.Installing => "Installing…",
        Stage.Done => "Installed.",
        Stage.Failed => "Install failed.",
        _ => "",
    };

    /// <summary>
    /// Maps a raw adb install failure to a plain-language message (A2.5). The raw string is
    /// logged by the caller and never shown — this is the only text the UI is allowed to see.
    /// </summary>
    public static string TranslateInstallFailure(string raw)
    {
        if (raw.Contains("INSTALL_FAILED_ALREADY_EXISTS", StringComparison.Ordinal))
        {
            return "That app is already installed.";
        }
        if (raw.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", StringComparison.Ordinal))
        {
            return "Not enough space on the phone.";
        }
        if (raw.Contains("INSTALL_FAILED_INVALID_APK", StringComparison.Ordinal) ||
            raw.Contains("INSTALL_PARSE_FAILED", StringComparison.Ordinal))
        {
            return "That file isn't a valid Android app.";
        }
        if (raw.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.Ordinal) ||
            raw.Contains("SIGNATURE", StringComparison.Ordinal))
        {
            return "A different version is already installed. Uninstall it first.";
        }
        if (raw.Contains("INSTALL_FAILED_USER_RESTRICTED", StringComparison.Ordinal))
        {
            return "The phone refused the install. Check it for a prompt.";
        }
        return "Install failed. The phone rejected the app.";
    }
}
