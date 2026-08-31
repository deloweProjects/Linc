using System.Text.Json.Nodes;
using Linc.Desktop.Services;
using Microsoft.Data.Sqlite;

// Verification harness for the M9c offline outbox (D-044's outbox table + OutboxService).
//
// Per outboxsim.csproj's own comment: OutboxService.cs is compiled in FULL so its pure statics
// (BuildSmsPayload / ParseSmsPayload / DecideDropVsAttempt) are exercised directly rather than
// modelled (M9c.md §4.2 / M9c-amend.md A1 — the M9b lesson: the more of this a harness can call,
// the less of it a harness has to re-implement). IConnectionSupervisor / IConnectionManager /
// LinkState are declared below as minimal STUBS purely so OutboxService.cs compiles against
// their constructor parameter types — they take NO part in any check here. LincStore's real
// outbox methods (enqueue/list/increment/delete/count) are exercised against real temp-rooted
// SQLite files, exactly as storesim does for the notification methods.
//
//   dotnet run --project tools/outboxsim
//
// D-057: every DeviceRegistry/LincStore construction below takes an explicit temp root — never
// the owner's real store. homelayoutsim's [10/10] scan enforces the DeviceRegistry half of that
// across every tools/*/Program.cs; this file must stay in the "PASS: N constructions, all with
// an explicit root" bucket.

Console.WriteLine("=== Linc Outbox Verification Harness (outboxsim) ===");

var failures = new List<string>();
const int Total = 9;

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
    var root = Path.Combine(Path.GetTempPath(), $"Linc_outboxsim_{tag}_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

void Cleanup(string root)
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

// ---------------------------------------------------------------------------------------
// 1. Enqueue-then-list round-trip: what goes in for one serial comes back out, unattempted.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/9] Enqueue-then-list round-trip...");
var root1 = NewRoot("1");
try
{
    var store1 = new LincStore(root1);
    await store1.EnsureSchemaAsync();

    var payloadA = OutboxService.BuildSmsPayload("+15550000001", "hello").ToJsonString();
    var payloadB = OutboxService.BuildSmsPayload("+15550000002", "world").ToJsonString();
    var idA = await store1.EnqueueOutboxAsync("SER1", OutboxService.SmsSendKind, payloadA);
    var idB = await store1.EnqueueOutboxAsync("SER1", OutboxService.SmsSendKind, payloadB);
    CheckSub(1, idA >= 0 && idB >= 0 && idB > idA,
        $"both enqueues returned increasing ids (idA={idA}, idB={idB}).",
        $"enqueue ids were not increasing (idA={idA}, idB={idB}).", "Enqueue ids not increasing");

    var rows = store1.ListPendingOutbox("SER1");
    CheckSub(1, rows.Count == 2,
        $"ListPendingOutbox(SER1) returned {rows.Count} row(s) (expected 2).",
        $"ListPendingOutbox(SER1) returned {rows.Count} row(s), expected 2.", "List count wrong");
    CheckSub(1, rows.All(r => r.Kind == OutboxService.SmsSendKind && r.Attempts == 0 && r.Serial == "SER1"),
        "both rows carry kind=sms.send, attempts=0, serial=SER1.",
        "row shape wrong (kind/attempts/serial).", "Row shape wrong on enqueue");
    CheckSub(1, rows[0].PayloadJson == payloadA && rows[1].PayloadJson == payloadB,
        "payload_json round-trips byte-for-byte for both rows.",
        "payload_json did not round-trip.", "Payload round-trip wrong");

    var emptyOther = store1.ListPendingOutbox("SER-NOBODY");
    Check(1, emptyOther.Count == 0 && !failures.Any(f => f is "Enqueue ids not increasing" or "List count wrong" or "Row shape wrong on enqueue" or "Payload round-trip wrong"),
        "enqueue-then-list round-trips cleanly and an unrelated serial sees nothing.",
        "enqueue-then-list round-trip broken (see sub-failures above).", "Enqueue-list round-trip broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/9] FAIL: enqueue-list check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Enqueue-list check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root1); }

// ---------------------------------------------------------------------------------------
// 2. Ordering by id ASC across 4 rows, including a delete from the middle. The assertion
//    compares the FULL id sequence, not just the first element (M9c-amend.md A2's trap: a
//    loose first-element check can still pass under ORDER BY id DESC on some row shapes).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/9] Ordering by id ASC across 4 rows, delete-from-middle, full-sequence assert...");
var root2 = NewRoot("2");
try
{
    var store2 = new LincStore(root2);
    await store2.EnsureSchemaAsync();

    var ids = new List<long>();
    for (var i = 0; i < 4; i++)
    {
        var payload = OutboxService.BuildSmsPayload($"+1555000000{i}", $"msg{i}").ToJsonString();
        ids.Add(await store2.EnqueueOutboxAsync("SER2", OutboxService.SmsSendKind, payload));
    }
    CheckSub(2, ids.Count == 4 && ids.SequenceEqual(ids.OrderBy(x => x)),
        $"the 4 returned ids are strictly increasing: [{string.Join(",", ids)}].",
        $"the 4 returned ids were not increasing: [{string.Join(",", ids)}].", "Returned ids not increasing");

    var before = store2.ListPendingOutbox("SER2").Select(r => r.Id).ToList();
    CheckSub(2, before.SequenceEqual(ids),
        $"the FULL id sequence before delete matches insertion order: [{string.Join(",", before)}].",
        $"the full id sequence before delete was [{string.Join(",", before)}], expected [{string.Join(",", ids)}].",
        "Full sequence before delete wrong");

    // Delete from the middle (the second row, ids[1]).
    var deleted = await store2.DeleteOutboxRowAsync(ids[1]);
    CheckSub(2, deleted,
        "DeleteOutboxRowAsync on the middle row returned true.",
        "DeleteOutboxRowAsync on the middle row returned false.", "Middle delete returned false");

    var after = store2.ListPendingOutbox("SER2").Select(r => r.Id).ToList();
    var expectedAfter = new List<long> { ids[0], ids[2], ids[3] };
    Check(2, after.SequenceEqual(expectedAfter),
        $"the FULL id sequence after deleting the middle row is [{string.Join(",", after)}] — still ASC, gap closed cleanly.",
        $"the full id sequence after delete was [{string.Join(",", after)}], expected [{string.Join(",", expectedAfter)}]. " +
        "A loose first-element-only assertion would have missed this.",
        "Full sequence after middle-delete wrong");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/9] FAIL: ordering check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Ordering check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root2); }

// ---------------------------------------------------------------------------------------
// 3. Attempts increment persists — across a fresh LincStore instance over the same file, not
//    just in the in-memory return value of the call that did the incrementing.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[3/9] Attempts increment persists across a fresh store instance...");
var root3 = NewRoot("3");
try
{
    var store3 = new LincStore(root3);
    await store3.EnsureSchemaAsync();
    var payload = OutboxService.BuildSmsPayload("+15559998888", "retry me").ToJsonString();
    var id = await store3.EnqueueOutboxAsync("SER3", OutboxService.SmsSendKind, payload);

    var a1 = await store3.IncrementOutboxAttemptsAsync(id);
    var a2 = await store3.IncrementOutboxAttemptsAsync(id);
    var a3 = await store3.IncrementOutboxAttemptsAsync(id);
    CheckSub(3, a1 == 1 && a2 == 2 && a3 == 3,
        $"three increments returned 1, 2, 3 in order (got {a1}, {a2}, {a3}).",
        $"increments did not return 1,2,3 in order (got {a1}, {a2}, {a3}).", "Increment return values wrong");

    // Reload with a brand-new LincStore over the same root — proves the count is on disk,
    // not just the value handed back by the call that did the incrementing.
    var store3b = new LincStore(root3);
    await store3b.EnsureSchemaAsync();
    var reloaded = store3b.ListPendingOutbox("SER3").Single(r => r.Id == id);
    Check(3, reloaded.Attempts == 3,
        $"a fresh store instance reads attempts={reloaded.Attempts} for the same row (persisted).",
        $"a fresh store instance read attempts={reloaded.Attempts}, expected 3 — the increment did not persist.",
        "Attempts did not persist across a fresh instance");
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/9] FAIL: attempts-persist check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Attempts-persist check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root3); }

// ---------------------------------------------------------------------------------------
// 4. DecideDropVsAttempt boundary at 0 / 4 / 5 / 6 (§2.7 — cap is MaxAttempts=5). This is the
//    check M9c-amend.md A1 names directly: FlushCoreAsync now calls this static instead of
//    re-implementing the comparison inline, so breaking the static's body (>= to >) must make
//    THIS check fail — that is the fourth negative proof A1 asks for at the M9c.md §6.6 level.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/9] DecideDropVsAttempt boundary at 0/4/5/6 (MaxAttempts=5)...");
try
{
    CheckSub(4, OutboxService.DecideDropVsAttempt(0) == false,
        "DecideDropVsAttempt(0) == false (attempt it).",
        "DecideDropVsAttempt(0) was true; a fresh row must never be dropped.", "Boundary 0 wrong");
    CheckSub(4, OutboxService.DecideDropVsAttempt(4) == false,
        "DecideDropVsAttempt(4) == false (one attempt left).",
        "DecideDropVsAttempt(4) was true; a row with attempts < MaxAttempts must still be attempted.", "Boundary 4 wrong");
    CheckSub(4, OutboxService.DecideDropVsAttempt(5) == true,
        "DecideDropVsAttempt(5) == true (== MaxAttempts, drop it).",
        "DecideDropVsAttempt(5) was false — a row AT the cap must be dropped, not attempted again.", "Boundary 5 wrong");
    CheckSub(4, OutboxService.DecideDropVsAttempt(6) == true,
        "DecideDropVsAttempt(6) == true (past MaxAttempts, drop it).",
        "DecideDropVsAttempt(6) was false.", "Boundary 6 wrong");
    Check(4, OutboxService.MaxAttempts == 5 && !failures.Any(f => f.StartsWith("Boundary", StringComparison.Ordinal)),
        "the 0/4/5/6 boundary matches MaxAttempts=5 exactly (4→attempt, 5→drop).",
        "the drop-vs-attempt boundary is wrong (see sub-failures above).", "DecideDropVsAttempt boundary broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/9] FAIL: boundary check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Boundary check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. Per-serial isolation — device A's queue is invisible to device B's flush (§2.3).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/9] Per-serial isolation...");
var root5 = NewRoot("5");
try
{
    var store5 = new LincStore(root5);
    await store5.EnsureSchemaAsync();

    var payloadA = OutboxService.BuildSmsPayload("+1555AAAA001", "for A").ToJsonString();
    var payloadB1 = OutboxService.BuildSmsPayload("+1555BBBB001", "for B 1").ToJsonString();
    var payloadB2 = OutboxService.BuildSmsPayload("+1555BBBB002", "for B 2").ToJsonString();
    await store5.EnqueueOutboxAsync("DEVICE-A", OutboxService.SmsSendKind, payloadA);
    await store5.EnqueueOutboxAsync("DEVICE-B", OutboxService.SmsSendKind, payloadB1);
    await store5.EnqueueOutboxAsync("DEVICE-B", OutboxService.SmsSendKind, payloadB2);

    var rowsA = store5.ListPendingOutbox("DEVICE-A");
    var rowsB = store5.ListPendingOutbox("DEVICE-B");
    CheckSub(5, rowsA.Count == 1 && rowsA[0].PayloadJson == payloadA,
        "DEVICE-A's list holds exactly its own 1 row.",
        $"DEVICE-A's list held {rowsA.Count} row(s) or the wrong payload.", "Serial A leaked or wrong");
    CheckSub(5, rowsB.Count == 2 && rowsB.All(r => r.Serial == "DEVICE-B"),
        "DEVICE-B's list holds exactly its own 2 rows, none from A.",
        $"DEVICE-B's list held {rowsB.Count} row(s) or a foreign serial leaked in.", "Serial B leaked or wrong");

    CheckSub(5, store5.CountPendingOutbox("DEVICE-A") == 1 && store5.CountPendingOutbox("DEVICE-B") == 2,
        "CountPendingOutbox agrees with the lists (A=1, B=2).",
        "CountPendingOutbox disagreed with the lists.", "Count disagreed with list");

    Check(5, !failures.Any(f => f is "Serial A leaked or wrong" or "Serial B leaked or wrong" or "Count disagreed with list"),
        "a row queued on one serial is invisible to another serial's flush.",
        "per-serial isolation broke (see sub-failures above).", "Per-serial isolation broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/9] FAIL: isolation check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Isolation check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root5); }

// ---------------------------------------------------------------------------------------
// 6. Delete-by-id removes exactly one row — the others survive untouched, and deleting an
//    already-gone id is a harmless false, not a duplicate delete.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/9] Delete-by-id removes exactly one row...");
var root6 = NewRoot("6");
try
{
    var store6 = new LincStore(root6);
    await store6.EnsureSchemaAsync();

    var ids = new List<long>();
    for (var i = 0; i < 3; i++)
    {
        var payload = OutboxService.BuildSmsPayload($"+1555DEL000{i}", $"del{i}").ToJsonString();
        ids.Add(await store6.EnqueueOutboxAsync("SER6", OutboxService.SmsSendKind, payload));
    }

    var deleted = await store6.DeleteOutboxRowAsync(ids[1]);
    CheckSub(6, deleted,
        "deleting the middle id returned true.",
        "deleting the middle id returned false.", "Delete-by-id returned false");

    var remaining = store6.ListPendingOutbox("SER6").Select(r => r.Id).ToList();
    CheckSub(6, remaining.Count == 2 && remaining.SequenceEqual(new[] { ids[0], ids[2] }),
        $"exactly 2 rows remain, the untouched ones: [{string.Join(",", remaining)}].",
        $"remaining rows were [{string.Join(",", remaining)}], expected [{ids[0]},{ids[2]}].", "Wrong survivors after delete");

    var deleteAgain = await store6.DeleteOutboxRowAsync(ids[1]);
    CheckSub(6, deleteAgain == false,
        "deleting the same id a second time returns false (already gone).",
        "deleting an already-gone id returned true.", "Double-delete returned true");

    Check(6, store6.CountPendingOutbox("SER6") == 2 && !failures.Any(f => f is "Delete-by-id returned false" or "Wrong survivors after delete" or "Double-delete returned true"),
        "delete-by-id removed exactly the one targeted row and left the others intact.",
        "delete-by-id broke (see sub-failures above).", "Delete-by-id broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [6/9] FAIL: delete-by-id check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Delete-by-id check threw: {ex.GetType().Name}: {ex.Message}");
}
finally { Cleanup(root6); }

// ---------------------------------------------------------------------------------------
// 7. Pure payload build/parse round-trip (no I/O) — including the exact JSON-string path the
//    real enqueue/flush uses (ToJsonString() then JsonNode.Parse back), and the degrade-not-throw
//    shape on a null/malformed node.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/9] Pure payload build/parse round-trip...");
try
{
    var built = OutboxService.BuildSmsPayload("+15551230000", "round trip me");
    var (address, body) = OutboxService.ParseSmsPayload(built);
    CheckSub(7, address == "+15551230000" && body == "round trip me",
        "parsing the JsonObject straight back out matches what was built.",
        "the in-memory JsonObject did not round-trip.", "In-memory round-trip wrong");

    // The real path: serialise to a string (what EnqueueOutboxAsync stores) then parse it back
    // (what SendSmsRowAsync does on flush).
    var json = built.ToJsonString();
    var reparsed = System.Text.Json.Nodes.JsonNode.Parse(json) as JsonObject;
    var (address2, body2) = OutboxService.ParseSmsPayload(reparsed);
    CheckSub(7, address2 == "+15551230000" && body2 == "round trip me",
        "the ToJsonString → JsonNode.Parse round-trip (the real enqueue/flush path) matches too.",
        "the string round-trip did not match the built payload.", "String round-trip wrong");

    var (emptyAddress, emptyBody) = OutboxService.ParseSmsPayload(null);
    CheckSub(7, emptyAddress == "" && emptyBody == "",
        "ParseSmsPayload(null) degrades to (\"\", \"\") rather than throwing.",
        "ParseSmsPayload(null) did not degrade to empty strings.", "Null-payload degrade wrong");

    var malformed = new JsonObject { ["body"] = "only body, no address" };
    var (malformedAddress, malformedBody) = OutboxService.ParseSmsPayload(malformed);
    CheckSub(7, malformedAddress == "" && malformedBody == "only body, no address",
        "a malformed node missing `address` degrades address to \"\" without throwing.",
        "a malformed node did not degrade cleanly.", "Malformed-payload degrade wrong");

    Check(7, !failures.Any(f => f is "In-memory round-trip wrong" or "String round-trip wrong" or "Null-payload degrade wrong" or "Malformed-payload degrade wrong"),
        "build/parse round-trips through both the object and the real string path, and degrades cleanly on bad input.",
        "the payload build/parse contract is broken (see sub-failures above).", "Payload build/parse broken");
}
catch (Exception ex)
{
    Console.WriteLine($"    [7/9] FAIL: payload round-trip check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Payload round-trip check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 8. CRUDE source-text check (Part 0 / displaysim M5c-3 style): SyncViewModel.cs's SendReplyAsync
//    still contains a LinkState.Connected comparison in the send path — the §2.2 decision point
//    the harness cannot call into (SyncViewModel has WinUI-side dependencies). Crude by design:
//    a tripwire proving the modelled decision point still exists in production, not a substitute
//    for review. Scoped to the SendReplyAsync method body so an unrelated LinkState.Connected
//    comparison elsewhere in the file cannot satisfy it by accident.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[8/9] CRUDE: SyncViewModel.cs still gates the send path on LinkState.Connected...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var syncPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "SyncViewModel.cs");
    if (!File.Exists(syncPath))
    {
        Check(8, false, "found SyncViewModel.cs.", $"could not locate {syncPath}; the crude check cannot run.", "SyncViewModel.cs not found");
    }
    else
    {
        var source = File.ReadAllText(syncPath).Replace("\r\n", "\n");
        var methodStart = source.IndexOf("private async Task SendReplyAsync", StringComparison.Ordinal);
        CheckSub(8, methodStart >= 0,
            "found the SendReplyAsync method.",
            "SendReplyAsync method not found in SyncViewModel.cs — the send path may have been renamed or removed.",
            "SendReplyAsync not found");

        if (methodStart >= 0)
        {
            // Crude method-body slice: from the signature to the next top-level "\n    }" that
            // closes a method at 4-space indent (matches this file's brace style). Good enough
            // for a tripwire — it does not need to be a real parser.
            var closeIdx = source.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
            var body = closeIdx > methodStart ? source[methodStart..closeIdx] : source[methodStart..];
            var bodyLines = body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var hasGuard = bodyLines.Any(l => l.Contains("LinkState.Connected", StringComparison.Ordinal));
            Check(8, hasGuard,
                "SendReplyAsync contains a non-comment `LinkState.Connected` comparison — the §2.2 queue decision point is real.",
                "SendReplyAsync has NO `LinkState.Connected` comparison — the §2.2 decision point is gone or moved.",
                "Production LinkState.Connected guard missing from SendReplyAsync");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [8/9] FAIL: SyncViewModel crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"SyncViewModel crude check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 9. CRUDE source-text check: AppShellViewModel.cs really constructs OutboxService and calls
//    Start() on it (§2.9 — the flush must run regardless of which page is open, so it is started
//    from the shell alongside clipboardSync/share, never from a page's view model). Crude by
//    design: it proves the wiring exists in production, not that it behaves correctly (§6.6's
//    second negative proof: deleting this call from AppShellViewModel.cs must fail this check).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[9/9] CRUDE: AppShellViewModel.cs constructs OutboxService and calls Start()...");
try
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var shellPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "AppShellViewModel.cs");
    if (!File.Exists(shellPath))
    {
        Check(9, false, "found AppShellViewModel.cs.", $"could not locate {shellPath}; the crude check cannot run.", "AppShellViewModel.cs not found");
    }
    else
    {
        var lines = File.ReadAllText(shellPath).Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var takesOutboxService = lines.Any(l => l.Contains("OutboxService outbox", StringComparison.Ordinal));
        CheckSub(9, takesOutboxService,
            "the constructor takes an `OutboxService outbox` parameter.",
            "no `OutboxService outbox` constructor parameter found — DI cannot supply it.",
            "OutboxService constructor parameter missing");

        var callsStart = lines.Any(l => l.Contains("outbox.Start()", StringComparison.Ordinal));
        CheckSub(9, callsStart,
            "`outbox.Start()` is called (eagerly, alongside clipboardSync/share).",
            "no `outbox.Start()` call found — the flush would never subscribe to reconnects.",
            "outbox.Start() call missing");

        Check(9, takesOutboxService && callsStart,
            "AppShellViewModel really constructs and starts the outbox service.",
            "the outbox service is not really wired into AppShellViewModel (see sub-failures above).",
            "Outbox service not started from AppShellViewModel");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [9/9] FAIL: AppShellViewModel crude check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"AppShellViewModel crude check threw: {ex.GetType().Name}: {ex.Message}");
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
    // depend on (see outboxsim.csproj's comment). Neither stub is ever constructed or referenced
    // by any check above — only OutboxService's pure statics and LincStore's outbox methods are
    // exercised. Kept intentionally minimal: only the members OutboxService.cs actually touches
    // (State, StateChanged, SmsSendAsync) rather than the full real interfaces.
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
