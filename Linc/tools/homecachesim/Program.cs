using Linc.Desktop.Services;

// Verification harness for the M9d-1 offline Home cache (D-032/D-044's sync_cache table +
// HomeViewModel's write-through/read-through, ungated lane widgets, and offline banner).
//
// Per homecachesim.csproj's own comment: LincStore.cs is compiled in FULL so its real
// sync_cache methods (upsert/list/prune/count) are exercised against real temp-rooted SQLite
// files, exactly as storesim/outboxsim do for the notification/outbox tables — never modelled
// (the M9b/M9c lesson, §4.1: the more of this a harness can call, the less of it has to be
// re-implemented). HomeCacheFormat.cs's HomeOfflineBanner.FormatText is likewise the real
// pure static, not a copy of its formatting logic.
//
// HomeViewModel.cs itself is WinUI-side (ObservableObject, DispatcherQueue, XAML types) and
// cannot be compiled into a plain net8.0 console, so the two behaviours that live only in its
// method bodies — "the !IsConnected branch never blanks the lists" and "the lane gates dropped
// IsConnected" — are proven with crude source-text checks instead (§4.1's second pattern: if
// the harness genuinely cannot call the production code, assert against the source text).
// Checks 8-11 below are exactly that, scoped tightly to the relevant method/property bodies so
// they cannot pass by accident against unrelated code elsewhere in the file. Checks 10-11 were
// added in M9e: 10 is the task file's own hypothesis (construction-path wiring), 11 is the race
// this session actually measured and fixed (LincStore.IsAvailable vs. EnsureSchemaAsync) — see
// their own comments below for why both exist even though 10 was already true on disk.
//
//   dotnet run --project tools/homecachesim
//
// D-057: this harness never constructs a DeviceRegistry or points anything at a real store —
// every LincStore below takes an explicit temp root. homelayoutsim's scan enforces the
// DeviceRegistry half of that rule across every tools/*/Program.cs; this file has none to flag.

Console.WriteLine("=== Linc Home Cache Verification Harness (homecachesim) ===");

var failures = new List<string>();
const int Total = 11;

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
    var root = Path.Combine(Path.GetTempPath(), $"Linc_homecachesim_{tag}_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

void Cleanup(string root)
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

// ---------------------------------------------------------------------------------------
// 1. Upsert-then-list round-trip, once per kind (photo / conversation / call, §2.1's literal
//    kind strings from HomeCacheKinds).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/11] Upsert-then-list round-trip, per kind...");
var root1 = NewRoot("1");
try
{
    var store1 = new LincStore(root1);
    await store1.EnsureSchemaAsync();

    foreach (var kind in new[] { HomeCacheKinds.Photo, HomeCacheKinds.Conversation, HomeCacheKinds.Call })
    {
        var now = DateTimeOffset.UtcNow;
        var okA = await store1.UpsertSyncCacheRowAsync("SER1", kind, "key-a", $"{{\"kind\":\"{kind}\",\"n\":1}}", now);
        var okB = await store1.UpsertSyncCacheRowAsync("SER1", kind, "key-b", $"{{\"kind\":\"{kind}\",\"n\":2}}", now.AddSeconds(1));
        CheckSub(1, okA && okB,
            $"kind={kind}: both upserts returned true.",
            $"kind={kind}: an upsert returned false.", $"Upsert returned false for kind={kind}");

        var rows = store1.ListSyncCacheRows("SER1", kind, 10);
        CheckSub(1, rows.Count == 2 && rows.Any(r => r.Key == "key-a") && rows.Any(r => r.Key == "key-b"),
            $"kind={kind}: ListSyncCacheRows returned both rows.",
            $"kind={kind}: ListSyncCacheRows returned {rows.Count} row(s), expected 2 with keys key-a/key-b.",
            $"Round-trip count/keys wrong for kind={kind}");
        CheckSub(1, rows.First(r => r.Key == "key-a").PayloadJson == $"{{\"kind\":\"{kind}\",\"n\":1}}",
            $"kind={kind}: payload_json for key-a round-trips byte-for-byte.",
            $"kind={kind}: payload_json for key-a did not round-trip.", $"Payload round-trip wrong for kind={kind}");
    }
    Check(1, !failures.Any(f => f.EndsWith("kind=photo") || f.EndsWith("kind=conversation") || f.EndsWith("kind=call")),
        "upsert-then-list round-trips cleanly for all three kinds.",
        "upsert-then-list round-trip broken for at least one kind (see sub-failures above).",
        "Per-kind round-trip broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/11] FAIL: per-kind round-trip check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Per-kind round-trip check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root1); }

// ---------------------------------------------------------------------------------------
// 2. Re-upserting the same (serial, kind, key) REPLACES rather than duplicates (§2.1). This is
//    also the third negative proof's target: breaking the ON CONFLICT clause into a plain
//    INSERT must make this check fail.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/11] Re-upserting the same key replaces, never duplicates...");
var root2 = NewRoot("2");
try
{
    var store2 = new LincStore(root2);
    await store2.EnsureSchemaAsync();

    var t1 = DateTimeOffset.UtcNow;
    var t2 = t1.AddMinutes(5);
    await store2.UpsertSyncCacheRowAsync("SER2", HomeCacheKinds.Conversation, "addr-1", "{\"v\":1}", t1);
    await store2.UpsertSyncCacheRowAsync("SER2", HomeCacheKinds.Conversation, "addr-1", "{\"v\":2}", t2);

    var rows = store2.ListSyncCacheRows("SER2", HomeCacheKinds.Conversation, 10);
    CheckSub(2, rows.Count == 1,
        $"exactly 1 row exists for the repeated key (got {rows.Count}).",
        $"{rows.Count} row(s) exist for the repeated key — a re-upsert duplicated instead of replacing.",
        "Repeated key duplicated a row");
    if (rows.Count > 0)
    {
        CheckSub(2, rows[0].PayloadJson == "{\"v\":2}",
            "the surviving row carries the SECOND payload (the replace won).",
            $"the surviving row carries \"{rows[0].PayloadJson}\", expected the second write's payload.",
            "Replace kept the stale payload");
        CheckSub(2, rows[0].UpdatedUtc.UtcDateTime == t2.UtcDateTime,
            "the surviving row's updated_utc is the SECOND write's timestamp.",
            "the surviving row's updated_utc did not advance to the second write's timestamp.",
            "Replace kept the stale timestamp");
    }
    Check(2, !failures.Any(f => f is "Repeated key duplicated a row" or "Replace kept the stale payload" or "Replace kept the stale timestamp"),
        "a repeat (serial, kind, key) upsert replaces the row in place.",
        "the upsert-replace contract is broken (see sub-failures above).", "Upsert-replace broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/11] FAIL: replace check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Replace check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root2); }

// ---------------------------------------------------------------------------------------
// 3. Newest-first ordering by updated_utc, asserted against the FULL key sequence (not just
//    the first element — the M9c-amend.md A2 trap: a loose first-element check can still pass
//    under a reversed sort).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[3/11] Newest-first ordering by updated_utc, full-sequence assert...");
var root3 = NewRoot("3");
try
{
    var store3 = new LincStore(root3);
    await store3.EnsureSchemaAsync();

    var t0 = DateTimeOffset.UtcNow;
    // Inserted out of chronological order on purpose: "b" is oldest, "c" is middle, "a" is newest.
    await store3.UpsertSyncCacheRowAsync("SER3", HomeCacheKinds.Call, "b", "{}", t0);
    await store3.UpsertSyncCacheRowAsync("SER3", HomeCacheKinds.Call, "a", "{}", t0.AddMinutes(10));
    await store3.UpsertSyncCacheRowAsync("SER3", HomeCacheKinds.Call, "c", "{}", t0.AddMinutes(5));

    var keys = store3.ListSyncCacheRows("SER3", HomeCacheKinds.Call, 10).Select(r => r.Key).ToList();
    Check(3, keys.SequenceEqual(new[] { "a", "c", "b" }),
        $"the full key sequence is newest-first: [{string.Join(",", keys)}].",
        $"the full key sequence was [{string.Join(",", keys)}], expected [a,c,b] (newest-first).",
        "Newest-first ordering wrong");
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/11] FAIL: ordering check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Ordering check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root3); }

// ---------------------------------------------------------------------------------------
// 4. Per-serial isolation (§2.6): the same (kind, key) on two different serials never blends.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/11] Per-serial isolation...");
var root4 = NewRoot("4");
try
{
    var store4 = new LincStore(root4);
    await store4.EnsureSchemaAsync();

    await store4.UpsertSyncCacheRowAsync("DEVICE-A", HomeCacheKinds.Photo, "p1", "{\"who\":\"A\"}", DateTimeOffset.UtcNow);
    await store4.UpsertSyncCacheRowAsync("DEVICE-B", HomeCacheKinds.Photo, "p1", "{\"who\":\"B\"}", DateTimeOffset.UtcNow);
    await store4.UpsertSyncCacheRowAsync("DEVICE-B", HomeCacheKinds.Photo, "p2", "{\"who\":\"B\"}", DateTimeOffset.UtcNow);

    var rowsA = store4.ListSyncCacheRows("DEVICE-A", HomeCacheKinds.Photo, 10);
    var rowsB = store4.ListSyncCacheRows("DEVICE-B", HomeCacheKinds.Photo, 10);
    CheckSub(4, rowsA.Count == 1 && rowsA[0].PayloadJson == "{\"who\":\"A\"}",
        "DEVICE-A's list holds exactly its own 1 row, unblended.",
        $"DEVICE-A's list held {rowsA.Count} row(s) or the wrong payload — a foreign row leaked in.",
        "Serial A leaked or wrong");
    CheckSub(4, rowsB.Count == 2 && rowsB.All(r => r.PayloadJson == "{\"who\":\"B\"}"),
        "DEVICE-B's list holds exactly its own 2 rows, none from A.",
        $"DEVICE-B's list held {rowsB.Count} row(s) or a foreign payload leaked in.", "Serial B leaked or wrong");
    CheckSub(4, store4.CountSyncCache("DEVICE-A") == 1 && store4.CountSyncCache("DEVICE-B") == 2,
        "CountSyncCache agrees with the lists (A=1, B=2).",
        "CountSyncCache disagreed with the lists.", "Count disagreed with list");
    Check(4, !failures.Any(f => f is "Serial A leaked or wrong" or "Serial B leaked or wrong" or "Count disagreed with list"),
        "a row cached for one serial is invisible to another serial, and never blends on switch.",
        "per-serial isolation broke (see sub-failures above).", "Per-serial isolation broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/11] FAIL: isolation check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Isolation check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root4); }

// ---------------------------------------------------------------------------------------
// 5. The 200-row cap (§2.9): PruneSyncCacheAsync keeps the newest 200 and drops the rest.
//    205 rows go in with strictly increasing updated_utc; after pruning to keep=200 the
//    oldest 5 (k000..k004) must be gone and the newest 200 (k005..k204) must remain.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/11] 200-row retention cap prunes the oldest, keeps the newest...");
var root5 = NewRoot("5");
try
{
    var store5 = new LincStore(root5);
    await store5.EnsureSchemaAsync();

    var t0 = DateTimeOffset.UtcNow.AddDays(-1);
    for (var i = 0; i < 205; i++)
    {
        await store5.UpsertSyncCacheRowAsync("SER5", HomeCacheKinds.Call, $"k{i:D3}", "{}", t0.AddSeconds(i));
    }
    var beforePrune = store5.CountSyncCache("SER5");
    CheckSub(5, beforePrune == 205,
        $"before pruning, all 205 rows are present (got {beforePrune}).",
        $"before pruning, {beforePrune} row(s) were present, expected 205.", "Pre-prune count wrong");

    var removed = await store5.PruneSyncCacheAsync("SER5", HomeCacheKinds.Call, 200);
    CheckSub(5, removed == 5,
        $"PruneSyncCacheAsync reported removing 5 row(s) (got {removed}).",
        $"PruneSyncCacheAsync reported removing {removed} row(s), expected 5.", "Prune removed-count wrong");

    var afterKeys = store5.ListSyncCacheRows("SER5", HomeCacheKinds.Call, 300).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    var expectedKeys = Enumerable.Range(5, 200).Select(i => $"k{i:D3}").OrderBy(k => k, StringComparer.Ordinal).ToList();
    Check(5, afterKeys.Count == 200 && afterKeys.SequenceEqual(expectedKeys),
        "exactly the newest 200 rows (k005..k204) survive the prune; k000..k004 are gone.",
        $"after pruning, {afterKeys.Count} row(s) survived and the surviving key set did not match the expected newest-200 " +
        "(k005..k204) — the cap did not prune the OLDEST rows.", "Retention cap pruned the wrong rows");
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/11] FAIL: retention cap check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Retention cap check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root5); }

// ---------------------------------------------------------------------------------------
// 6. HomeOfflineBanner.FormatText (§2.5): the pure static, called directly, with a known set
//    of timestamps — picks the MAX, not the first or the current time.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/11] HomeOfflineBanner.FormatText with known timestamps...");
try
{
    var older = DateTimeOffset.UtcNow.AddHours(-3);
    var newest = DateTimeOffset.UtcNow.AddMinutes(-1);
    var middle = DateTimeOffset.UtcNow.AddHours(-1);
    var text = HomeOfflineBanner.FormatText("SER6", new[] { older, newest, middle });
    // M15b D2 FLIPPED THIS DELIBERATELY. The expected string used to open with
    // "Phone disconnected — ", and this check pinned it. The banner no longer states the link's
    // state at all: Home says that once, in the Phone card, and this banner says only the thing
    // it alone knows — how old the cache is. The check is flipped, not weakened; it still pins an
    // exact string and still proves FormatText picks the MAX of the three timestamps.
    var expected = $"Showing what was last synced at {newest.ToLocalTime():t}.";
    Check(6, text == expected,
        $"FormatText picked the MAX timestamp and rendered \"{text}\".",
        $"FormatText returned \"{text}\", expected \"{expected}\" (the max of the three timestamps, local time).",
        "Banner text formatting wrong");
}
catch (Exception ex)
{
    Console.WriteLine($"    [6/11] FAIL: banner-text check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Banner-text check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 7. An empty restore, or no active serial, yields NO banner (§2.5 — an empty Home with no
//    phone ever paired must never claim to be showing a cache).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/11] Empty cache / no serial yields no banner...");
try
{
    var ts = DateTimeOffset.UtcNow;
    CheckSub(7, HomeOfflineBanner.FormatText(null, new[] { ts }) is null,
        "FormatText(null serial, non-empty timestamps) returns null.",
        "FormatText(null serial, non-empty timestamps) did not return null.", "Null-serial case did not suppress the banner");
    CheckSub(7, HomeOfflineBanner.FormatText("", new[] { ts }) is null,
        "FormatText(\"\" serial, non-empty timestamps) returns null.",
        "FormatText(\"\" serial, non-empty timestamps) did not return null.", "Empty-serial case did not suppress the banner");
    CheckSub(7, HomeOfflineBanner.FormatText("SER7", Array.Empty<DateTimeOffset>()) is null,
        "FormatText(a real serial, zero timestamps) returns null.",
        "FormatText(a real serial, zero timestamps) did not return null — an unpaired phone would wrongly claim a cache.",
        "Empty-timestamps case did not suppress the banner");
    Check(7, !failures.Any(f => f is "Null-serial case did not suppress the banner" or "Empty-serial case did not suppress the banner" or "Empty-timestamps case did not suppress the banner"),
        "every no-data case suppresses the banner rather than showing a false claim.",
        "at least one no-data case still produced banner text (see sub-failures above).", "Banner shown with no data");
}
catch (Exception ex)
{
    Console.WriteLine($"    [7/11] FAIL: empty-cache check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Empty-cache check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 8. CRUDE source-text check (§4.1's second pattern — HomeViewModel.cs is WinUI-side and this
//    harness cannot compile or call into it): RefreshLaneWidgets' `!IsConnected` (else) branch
//    contains NO Photos.Clear() / Conversations.Clear() / RecentCalls.Clear() call. This is the
//    §1.1 bug this whole task exists to fix — a re-added Clear() in that branch must fail this
//    check. Scoped tightly to the else block's own body (not the whole file, and not
//    LoadCachedLaneWidgetsAsync's own legitimate clear-then-repopulate calls a few lines below)
//    so it cannot pass — or fail — by accident against unrelated code.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[8/11] CRUDE: RefreshLaneWidgets' !IsConnected branch has no list .Clear()...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "HomeViewModel.cs");
    if (!File.Exists(vmPath))
    {
        Check(8, false, "found HomeViewModel.cs.", $"could not locate {vmPath}; the crude check cannot run.", "HomeViewModel.cs not found");
    }
    else
    {
        var source = File.ReadAllText(vmPath).Replace("\r\n", "\n");
        var methodStart = source.IndexOf("private void RefreshLaneWidgets()", StringComparison.Ordinal);
        CheckSub(8, methodStart >= 0,
            "found the RefreshLaneWidgets method.",
            "RefreshLaneWidgets method not found in HomeViewModel.cs — it may have been renamed or removed.",
            "RefreshLaneWidgets not found");

        if (methodStart >= 0)
        {
            var elseStart = source.IndexOf("\n        else\n        {", methodStart, StringComparison.Ordinal);
            CheckSub(8, elseStart >= 0,
                "found the `else` (not-connected) block inside RefreshLaneWidgets.",
                "no `else` block found inside RefreshLaneWidgets — the connected/not-connected split may be gone.",
                "Not-connected branch not found");

            if (elseStart >= 0)
            {
                var elseClose = source.IndexOf("\n        }", elseStart + 1, StringComparison.Ordinal);
                var elseBody = elseClose > elseStart ? source[elseStart..elseClose] : source[elseStart..];
                var hasClear = elseBody.Contains("Photos.Clear()", StringComparison.Ordinal)
                    || elseBody.Contains("Conversations.Clear()", StringComparison.Ordinal)
                    || elseBody.Contains("RecentCalls.Clear()", StringComparison.Ordinal);
                Check(8, !hasClear,
                    "the not-connected branch calls none of Photos/Conversations/RecentCalls .Clear() — D-032 holds.",
                    "the not-connected branch calls Photos.Clear() / Conversations.Clear() / RecentCalls.Clear() " +
                    "— Home would blank itself on disconnect again (the exact §1.1 bug this task fixes).",
                    "Not-connected branch blanks the lists again");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [8/11] FAIL: RefreshLaneWidgets crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"RefreshLaneWidgets crude check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 9. CRUDE source-text check: MessagesOn / CallsOn no longer contain `IsConnected` (§2.3 — a
//    lane the user turned on must stay on while offline; only where the rows come from
//    changes). Scoped to the two property declaration lines themselves so an unrelated
//    IsConnected elsewhere in the file cannot satisfy — or accidentally fail — this check.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[9/11] CRUDE: MessagesOn / CallsOn are ungated from IsConnected...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "HomeViewModel.cs");
    if (!File.Exists(vmPath))
    {
        Check(9, false, "found HomeViewModel.cs.", $"could not locate {vmPath}; the crude check cannot run.", "HomeViewModel.cs not found (check 9)");
    }
    else
    {
        var lines = File.ReadAllText(vmPath).Replace("\r\n", "\n").Split('\n');
        var messagesOnLine = lines.FirstOrDefault(l => l.Contains("public bool MessagesOn", StringComparison.Ordinal));
        var callsOnLine = lines.FirstOrDefault(l => l.Contains("public bool CallsOn", StringComparison.Ordinal));

        CheckSub(9, messagesOnLine is not null,
            "found the MessagesOn property declaration.",
            "MessagesOn property declaration not found in HomeViewModel.cs.", "MessagesOn declaration not found");
        CheckSub(9, callsOnLine is not null,
            "found the CallsOn property declaration.",
            "CallsOn property declaration not found in HomeViewModel.cs.", "CallsOn declaration not found");

        var messagesGated = messagesOnLine?.Contains("IsConnected", StringComparison.Ordinal) ?? true;
        var callsGated = callsOnLine?.Contains("IsConnected", StringComparison.Ordinal) ?? true;
        Check(9, !messagesGated && !callsGated,
            "neither MessagesOn nor CallsOn compares against IsConnected — the lane survives a disconnect.",
            "MessagesOn and/or CallsOn still compares against IsConnected — a lane the user turned on would " +
            "switch itself off the moment the phone disconnects (the exact §1.2 bug this task fixes).",
            "Lane widget still gated on IsConnected");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [9/11] FAIL: MessagesOn/CallsOn crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"MessagesOn/CallsOn crude check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 10. CRUDE source-text check (M9e, §A3.4): the cache read-through must run from the
//     CONSTRUCTOR, not only from a state-transition handler. This is the literal M9e task-file
//     hypothesis for the cold-start-no-cache defect. Measurement during M9e found the
//     construction-path call already present in both HomeViewModel and SyncViewModel (so this
//     check is a regression guard, not evidence of the original bug) — but it is exactly the
//     check that would have caught it if the call were ever deleted or moved back inside only
//     the `_supervisor.StateChanged +=` handler. Scoped by searching for the trigger call
//     (`RefreshLaneWidgets();` for Home, `LoadCachedLaneDataAsync();` for Sync) in the portion of
//     the constructor source that comes AFTER the StateChanged handler's own closing `});` — so a
//     call that exists ONLY inside that handler cannot satisfy this check by accident.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[10/11] CRUDE: the cache read-through runs from the constructor, not only StateChanged...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());

    void CheckConstructorTrigger(string fileName, string ctorSignature, string triggerCall, string label)
    {
        var path = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", fileName);
        if (!File.Exists(path))
        {
            CheckSub(10, false, $"found {fileName}.", $"could not locate {path}; the crude check cannot run.", $"{fileName} not found");
            return;
        }
        var source = File.ReadAllText(path).Replace("\r\n", "\n");
        var ctorStart = source.IndexOf(ctorSignature, StringComparison.Ordinal);
        if (ctorStart < 0)
        {
            CheckSub(10, false, $"found {label}'s constructor.", $"constructor signature `{ctorSignature}` not found in {fileName}.", $"{label} constructor not found");
            return;
        }
        // The StateChanged handler is the first `_supervisor.StateChanged +=` after the ctor
        // opens; its own body is a `dispatcher.TryEnqueue(() => { ... });` or
        // `_dispatcher.TryEnqueue(() => { ... });` block closed by the FIRST standalone `});` that
        // follows it. Anything after that close is outside the handler.
        var handlerStart = source.IndexOf("_supervisor.StateChanged +=", ctorStart, StringComparison.Ordinal);
        if (handlerStart < 0)
        {
            CheckSub(10, false, $"found {label}'s StateChanged handler.", $"`_supervisor.StateChanged +=` not found after {label}'s constructor opens.", $"{label} StateChanged handler not found");
            return;
        }
        var handlerClose = source.IndexOf("\n        });", handlerStart, StringComparison.Ordinal);
        if (handlerClose < 0)
        {
            CheckSub(10, false, $"found the end of {label}'s StateChanged handler.", $"could not find the closing `}});` of {label}'s StateChanged handler.", $"{label} StateChanged handler close not found");
            return;
        }
        var afterHandler = source[(handlerClose + 1)..];
        var hasOutsideCall = afterHandler.Contains(triggerCall, StringComparison.Ordinal);
        CheckSub(10, hasOutsideCall,
            $"{label}: `{triggerCall.Trim()}` appears after the StateChanged handler closes (i.e. also on the construction path).",
            $"{label}: `{triggerCall.Trim()}` was not found anywhere after the StateChanged handler closes — the cache " +
            "read-through would run ONLY on a state transition, and a cold launch with no transition would never read " +
            "the cache (the exact defect A2.1 exists to fix).",
            $"{label} cache read-through not wired on the construction path");
    }

    CheckConstructorTrigger("HomeViewModel.cs", "public HomeViewModel(", "RefreshLaneWidgets();", "HomeViewModel");
    CheckConstructorTrigger("SyncViewModel.cs", "public SyncViewModel(", "LoadCachedLaneDataAsync();", "SyncViewModel");

    Check(10, !failures.Any(f => f is "HomeViewModel cache read-through not wired on the construction path" or "SyncViewModel cache read-through not wired on the construction path"),
        "both HomeViewModel and SyncViewModel read the offline cache from their constructor, independent of StateChanged.",
        "at least one view model's cache read-through is reachable ONLY from a state transition (see sub-failures above).",
        "Construction-path cache wiring broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [10/11] FAIL: construction-path wiring check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Construction-path wiring check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 11. CRUDE source-text check (M9e): the REAL root cause measured this session — the
//     construction-path call above already existed, but it ran before LincStore.IsAvailable
//     flipped true (App.xaml.cs fires EnsureSchemaAsync fire-and-forget, racing the ViewModel
//     constructor), so ListSyncCacheRows silently returned empty with no log. The fix awaits the
//     idempotent LincStore.EnsureSchemaAsync() before the first sync_cache read in each method.
//     This check fails if that await is removed, which is the actual regression this task's own
//     A2.1 diagnosis would have missed (its hypothesis was "wiring is missing", not "wiring races
//     the store"). Scoped to each method's own body, before its first ListSyncCacheRows call.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[11/11] CRUDE: cache read-through awaits EnsureSchemaAsync before reading (closes the M9e race)...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());

    void CheckAwaitsSchema(string fileName, string methodSignature, string label)
    {
        var path = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", fileName);
        if (!File.Exists(path))
        {
            CheckSub(11, false, $"found {fileName}.", $"could not locate {path}; the crude check cannot run.", $"{fileName} not found (check 11)");
            return;
        }
        var source = File.ReadAllText(path).Replace("\r\n", "\n");
        var methodStart = source.IndexOf(methodSignature, StringComparison.Ordinal);
        if (methodStart < 0)
        {
            CheckSub(11, false, $"found {label}.", $"method signature `{methodSignature}` not found in {fileName}.", $"{label} not found");
            return;
        }
        var firstRead = source.IndexOf("_store.ListSyncCacheRows(", methodStart, StringComparison.Ordinal);
        if (firstRead < 0)
        {
            CheckSub(11, false, $"found {label}'s first ListSyncCacheRows call.", $"no `_store.ListSyncCacheRows(` call found in {label}'s body.", $"{label} has no sync_cache read to guard");
            return;
        }
        var awaitIndex = source.IndexOf("await _store.EnsureSchemaAsync();", methodStart, StringComparison.Ordinal);
        var awaitsBeforeRead = awaitIndex >= 0 && awaitIndex < firstRead;
        CheckSub(11, awaitsBeforeRead,
            $"{label} awaits EnsureSchemaAsync() before its first sync_cache read.",
            $"{label} does not await EnsureSchemaAsync() before ListSyncCacheRows — a cold-launch call can race " +
            "LincStore.IsAvailable and silently read back nothing (the real M9e defect).",
            $"{label} missing the EnsureSchemaAsync race guard");
    }

    CheckAwaitsSchema("HomeViewModel.cs", "private async Task LoadCachedLaneWidgetsAsync()", "HomeViewModel.LoadCachedLaneWidgetsAsync");
    CheckAwaitsSchema("SyncViewModel.cs", "private async Task LoadCachedLaneDataAsync()", "SyncViewModel.LoadCachedLaneDataAsync");

    Check(11, !failures.Any(f => f is "HomeViewModel.LoadCachedLaneWidgetsAsync missing the EnsureSchemaAsync race guard" or "SyncViewModel.LoadCachedLaneDataAsync missing the EnsureSchemaAsync race guard"),
        "both read-through methods await EnsureSchemaAsync before their first sync_cache read.",
        "at least one read-through method can race LincStore.IsAvailable (see sub-failures above).",
        "EnsureSchemaAsync race guard missing");
}
catch (Exception ex)
{
    Console.WriteLine($"    [11/11] FAIL: EnsureSchemaAsync race-guard check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"EnsureSchemaAsync race-guard check threw: {ex.GetType().Name}: {ex.Message}");
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
