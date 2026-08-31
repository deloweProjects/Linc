using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// Pure-static payload builders for the v15 display-control messages
/// (<see cref="Protocol.MessageType.DisplayRotationSet"/> and
/// <see cref="Protocol.MessageType.DisplayBrightnessSet"/>). Kept dependency-free so the
/// <c>tools\displaysim</c> harness can exercise them without WinUI / a live phone —
/// every other feature with a desktop widget follows the same pattern (MirrorService's
/// <c>BuildScrcpyArgs</c> is the precedent).
/// </summary>
public static class DisplayPayload
{
    /// <summary>
    /// Builds the exact JSON payload for <c>display.rotation.set</c>. [mode] must be one
    /// of <c>"auto"</c>, <c>"portrait"</c>, <c>"landscape"</c>; the harness asserts the
    /// payload shape matches PROTOCOL.md v15 byte-for-byte.
    /// </summary>
    public static JsonObject RotationPayload(string mode)
    {
        if (mode is not ("auto" or "portrait" or "landscape"))
        {
            throw new ArgumentException(
                $"Rotation mode must be 'auto', 'portrait' or 'landscape' (got '{mode}').",
                nameof(mode));
        }
        return new JsonObject { ["mode"] = mode };
    }

    /// <summary>
    /// Builds the exact JSON payload for <c>display.brightness.set</c>. When [auto] is
    /// true the <c>level</c> key is omitted — the phone ignores it anyway. When [auto]
    /// is false [level] must be in 0..100 (caller-validated; we throw rather than send a
    /// frame the phone would reject). Throws <see cref="ArgumentException"/> for a null
    /// or out-of-range [level] when [auto] is false so the caller can surface a clean
    /// message instead of a wire-time failure.
    /// </summary>
    public static JsonObject BrightnessPayload(bool auto, int? level)
    {
        var payload = new JsonObject { ["auto"] = auto };
        if (auto)
        {
            return payload;
        }
        if (level is null || level < 0 || level > 100)
        {
            throw new ArgumentException(
                "Brightness level must be between 0 and 100 when adaptive mode is off.",
                nameof(level));
        }
        payload["level"] = level.Value;
        return payload;
    }

    /// <summary>
    /// The version gate for the whole feature: <c>true</c> only on a v15-or-newer link,
    /// matching the negotiated-version check the SocketServer branches do on the phone
    /// (PROTOCOL.md v15, D-055). Mirrors the ConnectionManager.SendToPhoneAsync shape so
    /// callers can produce a single plain-language reason string.
    /// </summary>
    public static bool IsSupported(int? negotiatedVersion) => negotiatedVersion is >= 15;

    /// <summary>
    /// Plain-language reason matching the ConnectionManager.SendToPhoneAsync precedent
    /// (the supervisor logs it, the UI shows it via UnsupportedReason). Format is
    /// identical so the owner sees the same wording whether the gate was hit on send or
    /// on read.
    /// </summary>
    public static string UnsupportedReason(int? negotiatedVersion) =>
        negotiatedVersion is null
            ? "no phone connected"
            : $"the phone negotiated v{negotiatedVersion} (needs 15)";

    /// <summary>
    /// Parses the three v15 status fields out of a payload object. All three come back
    /// as nullable: absent means the phone never sent them (v14 phone, or any v15
    /// phone whose field was unreadable — practically impossible, reads are free). The
    /// UI must treat null as "unknown" rather than claim a default (D-055). Wrong-typed
    /// fields (a number where the phone should send a string, etc.) also yield null —
    /// a phone-side bug must not crash the desktop, and the wrong type carries no
    /// usable meaning on the wire.
    /// </summary>
    public static (string? RotationMode, bool? BrightnessAuto, int? BrightnessLevel) ParseStatus(JsonObject? payload)
    {
        if (payload is null)
        {
            return (null, null, null);
        }
        return (
            RotationMode: TryGetString(payload["rotationMode"]),
            BrightnessAuto: TryGetBool(payload["brightnessAuto"]),
            BrightnessLevel: TryGetInt(payload["brightnessLevel"]));
    }

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? TryGetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? TryGetInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
