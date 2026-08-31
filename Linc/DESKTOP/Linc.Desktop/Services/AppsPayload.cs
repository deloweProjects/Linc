using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// One launchable app on the phone (PROTOCOL.md v16, D-058). <see cref="VersionName"/> is
/// optional and cosmetic — some builds omit it entirely. <see cref="IsSystem"/> means the
/// package carries <c>FLAG_SYSTEM</c>; it is a grouping hint, never a reason to hide the app
/// (Camera, Settings and Photos are all system packages users want to reach).
/// </summary>
public sealed record AppInfo(string Package, string Label, string? VersionName, bool IsSystem);

/// <summary>
/// Pure-static parsing for the v16 <c>apps</c> reply, kept dependency-free so
/// <c>tools\appssim</c> can exercise it without a socket or WinUI — the
/// <see cref="DisplayPayload"/> pattern.
/// </summary>
public static class AppsPayload
{
    /// <summary>The <c>apps.get</c> request payload: deliberately empty per the spec.</summary>
    public static JsonObject RequestPayload() => [];

    /// <summary>
    /// Parses the <c>apps</c> reply into a list, tolerating everything the envelope rules say
    /// it must: a missing <c>versionName</c>, an absent <c>system</c> (defaults to false —
    /// showing a user app as a system one would be a worse guess than the reverse), unknown
    /// extra fields (ignored), a wrong-typed field (treated as absent, never a crash), and a
    /// missing or non-array <c>apps</c> key (an empty list).
    ///
    /// An entry with no <c>package</c> or no <c>label</c> is skipped rather than repaired:
    /// v16 forbids deriving a display name from the package id, so there is nothing honest to
    /// show for it.
    /// </summary>
    public static IReadOnlyList<AppInfo> Parse(JsonObject? payload)
    {
        var list = new List<AppInfo>();
        if (payload?["apps"] is not JsonArray array)
        {
            return list;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }
            var package = TryGetString(obj["package"]);
            var label = TryGetString(obj["label"]);
            if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }
            var version = TryGetString(obj["versionName"]);
            list.Add(new AppInfo(
                package,
                label,
                string.IsNullOrWhiteSpace(version) ? null : version,
                TryGetBool(obj["system"]) ?? false));
        }
        return list;
    }

    /// <summary>
    /// The version gate for the whole feature: <c>true</c> only on a v16-or-newer link,
    /// matching the <c>negotiated &gt;= 16</c> check in the phone's SocketServer branch.
    /// Same shape as <see cref="DisplayPayload.IsSupported"/>.
    /// </summary>
    public static bool IsSupported(int? negotiatedVersion) => negotiatedVersion is >= 16;

    /// <summary>
    /// Plain-language reason the Apps section is empty on an older link — shown to the owner
    /// verbatim, so it names no version numbers and no protocol (CONTRIBUTING.md).
    /// </summary>
    public const string UnsupportedReason =
        "This phone's Linc app is older and can't list apps yet. Update it on the phone to see them here.";

    /// <summary>Display order for the Apps section: by label, case-insensitively. The wire
    /// order is explicitly not a contract (PROTOCOL.md v16), so the desktop always sorts.</summary>
    public static IReadOnlyList<AppInfo> SortForDisplay(IEnumerable<AppInfo> apps) =>
        apps.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Package, StringComparer.Ordinal)
            .ToList();

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? TryGetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
