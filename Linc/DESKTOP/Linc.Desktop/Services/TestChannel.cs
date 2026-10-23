using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Services;

/// <summary>
/// Which build this is. A test build (test-build.yml, <c>-p:LincTestBuild=N</c>) is "Linc Test",
/// numbered by the workflow's run number, and follows the test feed (<see cref="TestChannel"/>);
/// every other build is plain Linc and never sees it. Read once from the assembly's metadata.
/// </summary>
public static class BuildFlavor
{
    public static int TestBuild { get; } = Read("LincTestBuild") is { } n && int.TryParse(n, out var v) && v > 0 ? v : 0;

    public static string? TestName { get; } = Read("LincTestName");

    public static bool IsTest => TestBuild > 0;

    /// <summary>"Linc" or "Linc Test": the window, the tray and About use it.</summary>
    public static string AppName => IsTest ? "Linc Test" : "Linc";

    /// <summary>"Test build 42 (linclink-b)", or empty for a normal build.</summary>
    public static string TestLabel => IsTest ? TestChannel.Label(TestBuild, TestName) : "";

    private static string? Read(string key)
    {
        try
        {
            var value = typeof(BuildFlavor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The test feed (owner: "update instead of reinstalling"). test-build.yml uploads each test build
/// to one pre-release, <see cref="FeedTag"/>, with <c>test-update.json</c> beside the files:
/// <code>
/// { "build": 43, "name": "linclink-b", "base": "1.0.0.18", "notes": "…",
///   "desktop": { "url": "…/linc-test-43-pc.zip", "sha256": "…" },
///   "android": { "url": "…/linc-test-43.apk",    "sha256": "…" } }
/// </code>
/// A test build offers a newer one; a hash that doesn't match is refused, as for releases. A
/// pre-release is invisible to <c>/releases/latest</c> and to <c>Releases/update.json</c>, so normal
/// Linc never sees any of this. Dependency-free: tools/updatesim checks it. The phone's twin is
/// <c>TestFeed.kt</c>.
/// </summary>
public static class TestChannel
{
    public const string FeedTag = "linc-test-feed";

    public const string FeedUrl = "https://github.com/deloweProjects/Linc/releases/download/" + FeedTag + "/test-update.json";

    public static string Label(int build, string? name) =>
        string.IsNullOrWhiteSpace(name) ? $"Test build {build}" : $"Test build {build} ({name})";

    /// <summary>The offer for a test build numbered <paramref name="mine"/>, or null: the feed is
    /// older or the same, has no PC file or no hash, or the user skipped exactly that build.</summary>
    public static TestManifest? Offer(TestManifest? feed, int mine, string? skipped)
    {
        if (mine <= 0 || feed is null || feed.Build <= mine) return null;
        if (string.IsNullOrWhiteSpace(feed.Desktop?.Url) || string.IsNullOrWhiteSpace(feed.Desktop?.Sha256)) return null;
        if (!feed.Desktop.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(skipped, Label(feed.Build, feed.Name), StringComparison.Ordinal) ? null : feed;
    }
}

/// <summary>One test build as the feed describes it. Every field may be missing; see
/// <see cref="TestChannel.Offer"/>.</summary>
public sealed record TestManifest(
    int Build,
    string? Name,
    string? Base,
    string? Notes,
    UpdateAsset? Desktop,
    UpdateAsset? Android)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static TestManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TestManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
