using System.Text.RegularExpressions;

namespace Linc.Desktop.Services;

/// <summary>
/// Per-device settings for the normal screen mirror (scrcpy without a virtual
/// display). Persisted on <see cref="KnownDevice"/> (M5b). Dependency-free so the
/// <c>tools\mirrorsettingssim</c> harness can exercise it (and
/// <see cref="ValidateSettingsFields"/>) without pulling in WinUI / the ViewModel.
/// Defaults reproduce the historical "Balanced" preset byte-for-byte so a device
/// that has never been touched launches with the same scrcpy args as before M5b.
/// </summary>
public sealed record MirrorSettings(
    int MaxSize = 0,
    string VideoBitRate = "8M",
    int MaxFps = 0,
    string? Crop = null,
    bool StayAwake = true,
    bool TurnScreenOff = false,
    bool ShowTouches = false,
    /// <summary>M11: scrcpy forwards phone audio to the PC by default on Android 11+, with no
    /// setting and no UI before this field existed. <c>true</c> preserves that behaviour exactly
    /// (no audio flag emitted at all — A2.3's byte-identity rule); <c>false</c> adds
    /// <c>--no-audio</c>.</summary>
    bool AudioEnabled = true,
    /// <summary>Bits/s passed to <c>--audio-bit-rate</c>. <c>0</c> means "use the scrcpy default
    /// (128K)" and omits the flag.</summary>
    int AudioBitRate = 0,
    /// <summary><c>null</c> means "use the scrcpy default (output)" and omits
    /// <c>--audio-source</c>. Only <c>"output"</c> or <c>"mic"</c> are otherwise accepted.</summary>
    string? AudioSource = null)
{
    public static readonly Regex BitRatePattern = new(@"^\d+[KM]$", RegexOptions.Compiled);

    /// <summary>
    /// The settings a never-previously-touched device launches with. Byte-for-byte
    /// identical to the historical "Balanced" <see cref="MirrorPreset"/> (MaxSize 1280,
    /// BitRate "8M", StayAwake on) so a brand-new user gets the same mirror quality as
    /// before M5b. The record's <see cref="new MirrorSettings()"/> positional defaults
    /// (MaxSize 0 = native) are the editor blanks; this is the persisted-for-null
    /// substitute returned by <see cref="DeviceRegistry.Mirror"/> for a device whose
    /// <see cref="KnownDevice.Mirror"/> field is still null (M5b byte-identity guarantee).
    /// </summary>
    public static MirrorSettings BalancedDefaults { get; } = new(
        MaxSize: 1280,
        VideoBitRate: "8M",
        MaxFps: 0,
        Crop: null,
        StayAwake: true,
        TurnScreenOff: false,
        ShowTouches: false,
        AudioEnabled: true,
        AudioBitRate: 0,
        AudioSource: null);

    /// <summary>
    /// Pure validation of the user-editable numeric/string fields. Returns null when
    /// everything is fine, otherwise a plain-language message. <c>0</c> means "use the
    /// scrcpy default" for <paramref name="maxSize"/> and <paramref name="maxFps"/>;
    /// null/empty <paramref name="crop"/> means "no crop." Lives here (on the dep-free
    /// record) so the <c>mirrorsettingssim</c> harness can exercise it without the VM.
    /// </summary>
    public static string? ValidateSettingsFields(
        int maxSize, string bitRate, int maxFps, string? crop,
        string? audioSource = null, int audioBitRate = 0)
    {
        if (maxSize != 0 && (maxSize < 320 || maxSize > 4096))
        {
            return "Maximum size must be between 320 and 4096 pixels (or 0 to use the phone's native size).";
        }
        if (string.IsNullOrWhiteSpace(bitRate) || !BitRatePattern.IsMatch(bitRate))
        {
            return "Video bit rate must look like a number followed by K or M, for example 8M or 4K.";
        }
        if (maxFps != 0 && (maxFps < 1 || maxFps > 120))
        {
            return "Maximum frame rate must be between 1 and 120 (or 0 for the default).";
        }
        if (!string.IsNullOrWhiteSpace(crop))
        {
            // Crop format is WxH:X:Y — Width x Height then X then Y, four non-negative
            // integers total. Split on either x/X/colon so any of those is a separator.
            var parts = crop.Split(['x', 'X', ':']);
            if (parts.Length != 4)
            {
                return "Crop must look like Width x Height : X : Y (for example, 1280x720:0:0) or left blank.";
            }
            foreach (var p in parts)
            {
                if (!TryParseNonNegative(p, out _))
                {
                    return "Crop must look like Width x Height : X : Y (for example, 1280x720:0:0) or left blank.";
                }
            }
        }
        if (audioSource is not (null or "output" or "mic"))
        {
            return "Audio source must be \"output\" or \"mic\" (or left blank to use the phone's default).";
        }
        if (audioBitRate != 0 && (audioBitRate < 32_000 || audioBitRate > 512_000))
        {
            return "Audio bit rate must be between 32,000 and 512,000 bits/s (or 0 to use the default).";
        }
        return null;
    }

    private static bool TryParseNonNegative(string s, out int value)
    {
        value = 0;
        return int.TryParse(s.Trim(), out value) && value >= 0;
    }
}
