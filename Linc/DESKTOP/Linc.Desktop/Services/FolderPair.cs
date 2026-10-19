using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// One folder kept the same on the PC and the phone (the Folders lane). Several per phone, each
/// with its own direction; stored on <see cref="KnownDevice.FolderPairs"/>, edited from the PC's
/// Sync page or the phone's (v30 <c>sync.pairs.set</c>). Dependency-free so tools\syncsim
/// compiles it with the engine.
/// </summary>
/// <param name="Id">Stable key; the engine's per-file bookkeeping is filed under it, so it never changes.</param>
/// <param name="Name">What the person calls it ("Documents", "Music"); defaults to the PC folder's name.</param>
/// <param name="Direction"><see cref="Both"/>, <see cref="ToPhone"/> or <see cref="ToPc"/>.</param>
public sealed record FolderPair(
    string Id,
    string Name,
    string PcPath,
    string PhonePath,
    string Direction = FolderPair.Both,
    bool Enabled = true)
{
    /// <summary>New files go both ways; the newer copy wins.</summary>
    public const string Both = "both";

    /// <summary>The PC's folder is copied to the phone; the phone's changes stay on the phone.</summary>
    public const string ToPhone = "toPhone";

    /// <summary>The phone's folder is copied to the PC (like Photos).</summary>
    public const string ToPc = "toPc";

    /// <summary>The id the single pre-pairs folder (KnownDevice.FolderSyncPcPath) always used, so its history carries over.</summary>
    public const string LegacyId = "folders";

    public bool PushesToPhone => Direction is Both or ToPhone;
    public bool PullsToPc => Direction is Both or ToPc;

    public static string NormaliseDirection(string? direction) =>
        direction is ToPhone or ToPc ? direction : Both;

    public static string NewId() => "pair-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A sensible name from a folder path: its last part, or the path itself for a drive.</summary>
    public static string NameFor(string pcPath)
    {
        var trimmed = pcPath.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? pcPath : name;
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["pcPath"] = PcPath,
        ["phonePath"] = PhonePath,
        ["direction"] = Direction,
        ["enabled"] = Enabled,
    };

    /// <summary>A pair from the phone (v30). Null when it has no PC or phone folder.</summary>
    public static FolderPair? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var pc = ((string?)o["pcPath"])?.Trim();
        var phone = ((string?)o["phonePath"])?.Trim();
        if (string.IsNullOrEmpty(pc) || string.IsNullOrEmpty(phone)) return null;
        var id = (string?)o["id"];
        var name = ((string?)o["name"])?.Trim();
        return new FolderPair(
            string.IsNullOrWhiteSpace(id) ? NewId() : id!,
            string.IsNullOrEmpty(name) ? NameFor(pc!) : name!,
            pc!,
            phone!.StartsWith('/') ? phone : "/sdcard/" + phone,
            NormaliseDirection((string?)o["direction"]),
            (bool?)o["enabled"] ?? true);
    }
}
