using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Services;

/// <summary>One downloadable artefact: where it is and what it must hash to.</summary>
public sealed record UpdateAsset(string? Url, string? Sha256);

/// <summary>
/// M19 C1: the update manifest, exactly as <c>Releases/update.json</c> is committed in this repo.
/// A STATIC FILE, fetched with a plain GET from <see cref="UpdateChannel.ManifestUrl"/>. Nothing is
/// sent to it — no query string, no headers about the user, no body (M17b B5).
///
/// <code>
/// {
///   "latest": "1.0.0-beta.2",
///   "minimumSupported": "1.0.0-beta.1",
///   "notes": "One short line shown to the user.",
///   "desktop": { "url": "https://github.com/.../Linc-Desktop-1.0.0-beta.2.zip", "sha256": "..." },
///   "android": { "url": "https://github.com/.../Linc-Companion-1.0.0-beta.2.apk", "sha256": "..." }
/// }
/// </code>
///
/// <para><b>Raising <c>minimumSupported</c> turns an optional update into a forced one</b> for every
/// build below it. That is the entire release mechanism: edit the file, commit, done.</para>
///
/// <para>Every field is nullable ON PURPOSE: a partial or malformed manifest must degrade to
/// <see cref="UpdateAction.None"/>, never throw and never force. See <see cref="UpdateDecision"/>,
/// whose rule M19 left exactly as M17b built it — only the source of the manifest changed.</para>
/// </summary>
public sealed record UpdateManifest(
    string? Latest,
    string? MinimumSupported,
    string? Notes,
    UpdateAsset? Desktop,
    UpdateAsset? Android,
    // M17b's flat shape. Kept so a manifest written before the nested one still resolves rather
    // than silently offering an update with nowhere to download it from.
    [property: JsonPropertyName("url")] string? LegacyUrl = null,
    [property: JsonPropertyName("sha256")] string? LegacySha256 = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>This app's download URL — the desktop asset, falling back to M17b's flat field.</summary>
    [JsonIgnore]
    public string? Url => Blank(Desktop?.Url) ?? Blank(LegacyUrl);

    /// <summary>This app's expected hash. No hash means the download is refused, never trusted.</summary>
    [JsonIgnore]
    public string? Sha256 => Blank(Desktop?.Sha256) ?? Blank(LegacySha256);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Parses a manifest. Returns null for anything that is not valid JSON of this shape — the
    /// caller then does nothing at all, which is the safe direction (M17b B3: failures are silent).
    /// Garbage that IS valid JSON parses into garbage strings, which <see cref="UpdateDecision"/>
    /// then rejects on its own; both routes end at <see cref="UpdateAction.None"/>.
    /// </summary>
    public static UpdateManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
