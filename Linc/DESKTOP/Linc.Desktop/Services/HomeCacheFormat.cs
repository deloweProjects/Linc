namespace Linc.Desktop.Services;

/// <summary>
/// The literal <c>sync_cache.kind</c> strings Home's write-through/read-through use (M9d-1, §2.1).
/// A shared constant so the write site (HomeViewModel) and the read site (also HomeViewModel, plus
/// tools\homecachesim) cannot drift apart on spelling.
/// </summary>
public static class HomeCacheKinds
{
    public const string Photo = "photo";
    public const string Conversation = "conversation";
    public const string Call = "call";
}

/// <summary>
/// One photo's <c>sync_cache</c> payload shape (kind <see cref="HomeCacheKinds.Photo"/>, §2.1).
/// Named distinctly from <c>DeviceCacheService.CachedPhoto</c> (the older M02 per-device file
/// cache's own, differently-shaped, type of the same short name) — same namespace, so a
/// same-name record here is a compile error, not a shadow.
/// </summary>
public sealed record CachedSyncPhoto(string Id, string Path, long TakenAt);

/// <summary>One message inside a <see cref="CachedConversation"/>.</summary>
public sealed record CachedMessage(string Body, bool Incoming, long Date);

/// <summary>
/// One conversation's <c>sync_cache</c> payload (kind <see cref="HomeCacheKinds.Conversation"/>,
/// §2.1) — the whole thread lives under one row keyed by <see cref="Address"/>, so a re-load
/// upserts the same key rather than growing one row per message.
/// </summary>
public sealed record CachedConversation(string Address, IReadOnlyList<CachedMessage> Messages);

/// <summary>
/// One call-log entry's <c>sync_cache</c> payload (kind <see cref="HomeCacheKinds.Call"/>, §2.1).
/// <c>CallEntry</c> carries no id, so the row key HomeViewModel writes is
/// <c>"{Number}|{Date}"</c> — a stable composite identity for the same call across reloads.
/// </summary>
public sealed record CachedCall(string Number, string Type, long Date, long Duration);

/// <summary>
/// Formats the Home offline banner's text (M9d-1, §2.5). Dependency-free (no WinUI) so
/// tools\homecachesim can call it directly instead of modelling it — the same posture as
/// HomeLayout.cs (D-036/D-037) and the M9c lesson that a harness should call production code,
/// not re-implement it (§4.1).
/// </summary>
public static class HomeOfflineBanner
{
    /// <summary>
    /// <paramref name="restoredTimestamps"/> is every <c>updated_utc</c> actually restored from
    /// sync_cache for the active serial (across all three kinds) — NOT the current time. Returns
    /// <c>null</c> (no banner) when there is no active serial or nothing was restored (§2.5 — an
    /// empty Home with no phone ever paired must never claim to be showing a cache).
    /// </summary>
    public static string? FormatText(string? serial, IReadOnlyList<DateTimeOffset> restoredTimestamps)
    {
        if (string.IsNullOrEmpty(serial) || restoredTimestamps.Count == 0)
        {
            return null;
        }
        var latest = restoredTimestamps.Max();
        // M15b D2: this used to open with "Phone disconnected — ", which made it the THIRD
        // rendering of the link's state on one screen (the Phone card names the phone and states
        // the link; this banner sat above both saying it again). The banner keeps only what it
        // alone knows — how fresh the cache is. M14 §C2 allows the two statements to coexist
        // precisely because they now say different things: link state, and cache age.
        return $"Showing what was last synced at {latest.ToLocalTime():t}.";
    }
}
