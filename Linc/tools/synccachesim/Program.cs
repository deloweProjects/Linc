using System.Text.Json;
using Linc.Desktop.Services;

// Verification harness for M9d-2 — the Sync page's offline sync_cache read/write-through
// (§3.1/§3.5, reusing M9d-1's sync_cache table + HomeCacheFormat payload types exactly, no
// second cache layer) and the queued-message affordance (§3.2-§3.4: CanReply stays ungated,
// IsPending renders "Queued", and OutboxService.RowFlushed + the pure static MatchesFlushedRow
// flip a pending message back to sent).
//
// Per synccachesim.csproj's own comment: LincStore.cs and HomeCacheFormat.cs are compiled in
// FULL so the real sync_cache methods and the real HomeOfflineBanner.FormatText are exercised
// against real temp-rooted SQLite files — never modelled (§4.1's lesson). OutboxService.cs is
// likewise compiled in full so MatchesFlushedRow (§3.4) is the real pure static, not a copy of
// its comparison logic (§4.2).
//
// SyncViewModel.cs and SyncPage.xaml are WinUI-side (ObservableObject, DispatcherQueue, XAML)
// and cannot be compiled into a plain net8.0 console, so the three behaviours that live only in
// their bodies/markup — "CanReply never gained an IsConnected term" (§3.2), "SyncPage.xaml binds
// IsPending" (§3.3), and "the cache read/write-through methods exist and call the real
// LincStore/HomeCacheFormat surface" — are proven with crude source-text checks instead (§4.1's
// second pattern). Checks 6-7 below are exactly that, scoped tightly so they cannot pass — or
// fail — by accident against unrelated code.
//
//   dotnet run --project tools/synccachesim
//
// D-057: every LincStore/DeviceRegistry construction below takes an explicit temp root — never
// the owner's real store. homelayoutsim's scan enforces the DeviceRegistry half of that rule
// across every tools/*/Program.cs; this file has none to flag.

Console.WriteLine("=== Linc Sync Cache Verification Harness (synccachesim) ===");

var failures = new List<string>();
const int Total = 7;

void Check(int index, bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    [{index}/{Total}] PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    [{index}/{Total}] FAIL: {failText}");
        failures.Add(failure);
    }
}

void CheckSub(int index, bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"        PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"        FAIL: {failText}");
        failures.Add(failure);
    }
}

string NewRoot(string tag)
{
    var root = Path.Combine(Path.GetTempPath(), $"Linc_synccachesim_{tag}_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

void Cleanup(string root)
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

// ---------------------------------------------------------------------------------------
// 1. Conversation/call round-trip through the REAL payload shapes (§3.1): a CachedConversation
//    with several messages, and a CachedCall — the exact records SyncViewModel now reads/writes.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/7] Conversation & call cache round-trip, real payload shapes...");
var root1 = NewRoot("1");
try
{
    var store1 = new LincStore(root1);
    await store1.EnsureSchemaAsync();

    var convo = new CachedConversation("+15550001111",
        [new CachedMessage("hi there", true, 1000), new CachedMessage("hello back", false, 2000)]);
    var okConvo = await store1.UpsertSyncCacheRowAsync("SER1", HomeCacheKinds.Conversation, convo.Address,
        JsonSerializer.Serialize(convo), DateTimeOffset.UtcNow);
    CheckSub(1, okConvo, "conversation upsert returned true.", "conversation upsert returned false.", "Conversation upsert failed");

    var convoRows = store1.ListSyncCacheRows("SER1", HomeCacheKinds.Conversation, 10);
    CheckSub(1, convoRows.Count == 1, $"exactly 1 conversation row exists (got {convoRows.Count}).",
        $"{convoRows.Count} conversation row(s) exist, expected 1.", "Conversation row count wrong");
    if (convoRows.Count == 1)
    {
        var back = JsonSerializer.Deserialize<CachedConversation>(convoRows[0].PayloadJson);
        CheckSub(1, back is not null && back.Address == convo.Address && back.Messages.Count == 2
            && back.Messages[0].Body == "hi there" && back.Messages[0].Incoming
            && back.Messages[1].Body == "hello back" && !back.Messages[1].Incoming,
            "the deserialized conversation matches address, message count, bodies and incoming flags exactly.",
            "the deserialized conversation did not match what was written.", "Conversation payload did not round-trip");
    }

    var call = new CachedCall("+15559990000", "missed", 5000, 0);
    var okCall = await store1.UpsertSyncCacheRowAsync("SER1", HomeCacheKinds.Call, $"{call.Number}|{call.Date}",
        JsonSerializer.Serialize(call), DateTimeOffset.UtcNow);
    CheckSub(1, okCall, "call upsert returned true.", "call upsert returned false.", "Call upsert failed");

    var callRows = store1.ListSyncCacheRows("SER1", HomeCacheKinds.Call, 10);
    CheckSub(1, callRows.Count == 1, $"exactly 1 call row exists (got {callRows.Count}).",
        $"{callRows.Count} call row(s) exist, expected 1.", "Call row count wrong");
    if (callRows.Count == 1)
    {
        var back = JsonSerializer.Deserialize<CachedCall>(callRows[0].PayloadJson);
        CheckSub(1, back is not null && back.Number == call.Number && back.Type == call.Type
            && back.Date == call.Date && back.Duration == call.Duration,
            "the deserialized call matches number, type, date and duration exactly.",
            "the deserialized call did not match what was written.", "Call payload did not round-trip");
    }

    Check(1, !failures.Any(f => f is "Conversation upsert failed" or "Conversation row count wrong"
        or "Conversation payload did not round-trip" or "Call upsert failed" or "Call row count wrong"
        or "Call payload did not round-trip"),
        "both kinds round-trip cleanly through their real payload shapes.",
        "at least one kind's round-trip broke (see sub-failures above).", "Real-shape round-trip broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/7] FAIL: round-trip check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Round-trip check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root1); }

// ---------------------------------------------------------------------------------------
// 2. Newest-first ordering by updated_utc, full-sequence assert (not just the first element —
//    the M9c-amend.md A2 trap: a loose first-element check can still pass under a reversed sort).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/7] Newest-first ordering by updated_utc, full-sequence assert...");
var root2 = NewRoot("2");
try
{
    var store2 = new LincStore(root2);
    await store2.EnsureSchemaAsync();

    var t0 = DateTimeOffset.UtcNow;
    // Inserted out of chronological order on purpose: "b" is oldest, "c" is middle, "a" is newest.
    await store2.UpsertSyncCacheRowAsync("SER2", HomeCacheKinds.Call, "b", "{}", t0);
    await store2.UpsertSyncCacheRowAsync("SER2", HomeCacheKinds.Call, "a", "{}", t0.AddMinutes(10));
    await store2.UpsertSyncCacheRowAsync("SER2", HomeCacheKinds.Call, "c", "{}", t0.AddMinutes(5));

    var keys = store2.ListSyncCacheRows("SER2", HomeCacheKinds.Call, 10).Select(r => r.Key).ToList();
    Check(2, keys.SequenceEqual(new[] { "a", "c", "b" }),
        $"the full key sequence is newest-first: [{string.Join(",", keys)}].",
        $"the full key sequence was [{string.Join(",", keys)}], expected [a,c,b] (newest-first).",
        "Newest-first ordering wrong");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/7] FAIL: ordering check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Ordering check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root2); }

// ---------------------------------------------------------------------------------------
// 3. Per-serial isolation (§3.1 mirrors §2.6): switching device tabs must never blend one
//    phone's cached conversations into another's.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[3/7] Per-serial isolation...");
var root3 = NewRoot("3");
try
{
    var store3 = new LincStore(root3);
    await store3.EnsureSchemaAsync();

    await store3.UpsertSyncCacheRowAsync("DEVICE-A", HomeCacheKinds.Conversation, "addr1", "{\"who\":\"A\"}", DateTimeOffset.UtcNow);
    await store3.UpsertSyncCacheRowAsync("DEVICE-B", HomeCacheKinds.Conversation, "addr1", "{\"who\":\"B\"}", DateTimeOffset.UtcNow);
    await store3.UpsertSyncCacheRowAsync("DEVICE-B", HomeCacheKinds.Conversation, "addr2", "{\"who\":\"B\"}", DateTimeOffset.UtcNow);

    var rowsA = store3.ListSyncCacheRows("DEVICE-A", HomeCacheKinds.Conversation, 10);
    var rowsB = store3.ListSyncCacheRows("DEVICE-B", HomeCacheKinds.Conversation, 10);
    CheckSub(3, rowsA.Count == 1 && rowsA[0].PayloadJson == "{\"who\":\"A\"}",
        "DEVICE-A's list holds exactly its own 1 row, unblended.",
        $"DEVICE-A's list held {rowsA.Count} row(s) or the wrong payload — a foreign row leaked in.",
        "Serial A leaked or wrong");
    CheckSub(3, rowsB.Count == 2 && rowsB.All(r => r.PayloadJson == "{\"who\":\"B\"}"),
        "DEVICE-B's list holds exactly its own 2 rows, none from A.",
        $"DEVICE-B's list held {rowsB.Count} row(s) or a foreign payload leaked in.", "Serial B leaked or wrong");
    Check(3, !failures.Any(f => f is "Serial A leaked or wrong" or "Serial B leaked or wrong"),
        "a row cached for one serial is invisible to another serial, and never blends on switch.",
        "per-serial isolation broke (see sub-failures above).", "Per-serial isolation broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/7] FAIL: isolation check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Isolation check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root3); }

// ---------------------------------------------------------------------------------------
// 4. OutboxService.MatchesFlushedRow (§3.4) — the pure static SyncViewModel.OnOutboxRowFlushed
//    calls instead of modelling the match decision. One matching case, three non-matching.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/7] OutboxService.MatchesFlushedRow — matching and non-matching cases...");
try
{
    CheckSub(4, OutboxService.MatchesFlushedRow("+15551234567", "on my way", true, "+15551234567", "on my way"),
        "same address, same body, message IS pending → true (this is the flushed message).",
        "an exact address/body match on a pending message returned false.", "Matching case returned false");
    CheckSub(4, !OutboxService.MatchesFlushedRow("+15551234567", "on my way", false, "+15551234567", "on my way"),
        "same address, same body, message is NOT pending → false (already sent; never re-matched).",
        "a non-pending message with matching address/body still matched.", "Non-pending message matched");
    CheckSub(4, !OutboxService.MatchesFlushedRow("+1000000000", "on my way", true, "+19999999999", "on my way"),
        "different address, same body, pending → false.",
        "a different-address message still matched the flushed row.", "Different-address case matched");
    CheckSub(4, !OutboxService.MatchesFlushedRow("+15551234567", "wrong text", true, "+15551234567", "on my way"),
        "same address, different body, pending → false.",
        "a different-body message still matched the flushed row.", "Different-body case matched");
    Check(4, !failures.Any(f => f is "Non-pending message matched" or "Different-address case matched" or "Different-body case matched")
        && !failures.Contains("Matching case returned false"),
        "MatchesFlushedRow matches exactly the pending message with the same address+body, nothing else.",
        "MatchesFlushedRow's matching contract is broken (see sub-failures above).", "MatchesFlushedRow contract broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/7] FAIL: MatchesFlushedRow check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"MatchesFlushedRow check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. HomeOfflineBanner.FormatText reused by Sync (§3.5) — same static, known timestamps, and
//    the no-data cases that must suppress the banner rather than show a false claim.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/7] HomeOfflineBanner.FormatText reused, known timestamps + no-data suppression...");
try
{
    var older = DateTimeOffset.UtcNow.AddHours(-2);
    var newest = DateTimeOffset.UtcNow.AddMinutes(-1);
    var text = HomeOfflineBanner.FormatText("SER5", new[] { older, newest });
    // M15b D2 FLIPPED THIS DELIBERATELY, in step with homecachesim's copy of the same check: the
    // banner no longer opens with "Phone disconnected — ". Home states the link's state once, in
    // the Phone card; this banner says only how old the cache is. Still an exact-string pin.
    var expected = $"Showing what was last synced at {newest.ToLocalTime():t}.";
    CheckSub(5, text == expected,
        $"FormatText picked the MAX timestamp and rendered \"{text}\".",
        $"FormatText returned \"{text}\", expected \"{expected}\".", "Banner text formatting wrong");

    CheckSub(5, HomeOfflineBanner.FormatText(null, new[] { newest }) is null,
        "FormatText(null serial, non-empty timestamps) returns null.",
        "FormatText(null serial, non-empty timestamps) did not return null.", "Null-serial did not suppress banner");
    CheckSub(5, HomeOfflineBanner.FormatText("SER5", Array.Empty<DateTimeOffset>()) is null,
        "FormatText(a real serial, zero timestamps) returns null.",
        "FormatText(a real serial, zero timestamps) did not return null — an unpaired phone would wrongly claim a cache.",
        "Empty-timestamps did not suppress banner");

    Check(5, !failures.Any(f => f is "Banner text formatting wrong" or "Null-serial did not suppress banner" or "Empty-timestamps did not suppress banner"),
        "Sync reuses the exact same banner formatter Home does, with the exact same no-data rules.",
        "the reused banner formatter misbehaved (see sub-failures above).", "Reused banner formatter broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/7] FAIL: banner-text check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Banner-text check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 6. CRUDE source-text check (§4.1's second pattern — SyncViewModel.cs is WinUI-side and this
//    harness cannot compile or call into it): the CanReply property declaration line does NOT
//    contain IsConnected (§3.2 — a lane the user turned on must stay reachable offline, since
//    that's what makes M9c's queue reachable at all). Scoped to the declaration line itself so
//    an unrelated IsConnected elsewhere in the file cannot satisfy — or accidentally fail — this.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/7] CRUDE: SyncViewModel.cs's CanReply is not gated on IsConnected...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "SyncViewModel.cs");
    if (!File.Exists(vmPath))
    {
        Check(6, false, "found SyncViewModel.cs.", $"could not locate {vmPath}; the crude check cannot run.", "SyncViewModel.cs not found");
    }
    else
    {
        var lines = File.ReadAllText(vmPath).Replace("\r\n", "\n").Split('\n');
        var canReplyLine = lines.FirstOrDefault(l => l.Contains("public bool CanReply", StringComparison.Ordinal));
        CheckSub(6, canReplyLine is not null,
            "found the CanReply property declaration.",
            "CanReply property declaration not found in SyncViewModel.cs.", "CanReply declaration not found");
        var gated = canReplyLine?.Contains("IsConnected", StringComparison.Ordinal) ?? true;
        Check(6, !gated,
            "CanReply does not compare against IsConnected — a conversation stays replyable offline.",
            "CanReply still compares against IsConnected — the reply box (and M9c's queue) would be " +
            "unreachable offline, the exact §3.2 bug this task exists to avoid.",
            "CanReply still gated on IsConnected");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [6/7] FAIL: CanReply crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"CanReply crude check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 7. CRUDE source-text check: SyncPage.xaml actually binds IsPending (§3.3 — the "Queued"
//    affordance must be wired in the real markup, not just exist on MessageVm unused).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/7] CRUDE: SyncPage.xaml binds IsPending...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "SyncPage.xaml");
    if (!File.Exists(xamlPath))
    {
        Check(7, false, "found SyncPage.xaml.", $"could not locate {xamlPath}; the crude check cannot run.", "SyncPage.xaml not found");
    }
    else
    {
        var source = File.ReadAllText(xamlPath);
        var bindsIsPending = source.Contains("x:Bind IsPending", StringComparison.Ordinal);
        Check(7, bindsIsPending,
            "SyncPage.xaml contains an `x:Bind IsPending` binding — the pending affordance is really wired up.",
            "SyncPage.xaml has no `x:Bind IsPending` binding — MessageVm.IsPending exists but nothing " +
            "in the page shows it, so a queued message would still look identical to a sent one.",
            "SyncPage.xaml does not bind IsPending");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [7/7] FAIL: SyncPage.xaml crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"SyncPage.xaml crude check threw: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;

// ---- helpers ----

static string FindRepoRoot(string start)
{
    // The harness runs from anywhere — `dotnet run` keeps the parent's CWD, which may be the
    // workspace root (yellow\) rather than the Linc\ code root. Walk up, and at each ancestor
    // check both <ancestor>\DESKTOP\... and <ancestor>\Linc/DESKTOP/... so both layouts resolve.
    static string? Marker(string dir) =>
        File.Exists(Path.Combine(dir, "DESKTOP", "Linc.Desktop", "Services", "LincStore.cs"))
            ? dir
            : File.Exists(Path.Combine(dir, "Linc", "DESKTOP", "Linc.Desktop", "Services", "LincStore.cs"))
                ? Path.Combine(dir, "Linc")
                : null;

    for (var current = start; current != null; current = Directory.GetParent(current)?.FullName)
    {
        if (Marker(current) is { } hit)
        {
            return hit;
        }
    }
    // Fall back to current dir if nothing matched (best effort; the check becomes a WARN).
    return start;
}

namespace Linc.Desktop.Services
{
    // Minimal stand-ins so OutboxService.cs compiles without pulling in the real adb/usb/tls
    // ConnectionSupervisor / ConnectionManager machinery those interfaces' real implementations
    // depend on (identical to outboxsim's own stubs — see synccachesim.csproj's comment). Neither
    // stub is ever constructed or referenced by any check above — only OutboxService's pure
    // static MatchesFlushedRow and LincStore's sync_cache methods are exercised. Kept
    // intentionally minimal: only the members OutboxService.cs actually touches.
    public enum LinkState { NoDevice, Searching, Connecting, Connected, Paused }

    public interface IConnectionSupervisor
    {
        LinkState State { get; }
        event Action? StateChanged;
    }

    public interface IConnectionManager
    {
        Task SmsSendAsync(string address, string body, CancellationToken ct);
    }
}
