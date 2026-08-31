using Linc.Desktop.Services;
using Microsoft.Data.Sqlite;

// Verification harness for the M9a SQLite foundation (D-044) and the M9b notification history
// (D-045): the LincStore schema, its versioned-but-additive migration, its idempotent device
// import, the D-057 store-root rule, and the M9b notification methods (insert/query round-trips,
// the §§2.2 off-means-empty guard, prune, delete-all, retention-change prune, and a crude check
// that LincStore.cs never writes a notification body to the log). Runs entirely offline â€” no
// phone, no WinUI â€” against throwaway temp roots.
//
//   dotnet run --project tools/storesim
//
// D-057: every DeviceRegistry and every LincStore here is built against one throwaway temp
// root, so nothing in this file can reach the owner's real settings.json OR their real
// linc.db. The D-057 check (6) also asserts the resolved PATH STRING against the real store,
// never by writing to it.

Console.WriteLine("=== Linc Store Verification Harness (storesim) ===");

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "Linc_storesim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

void Check(int index, int total, bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    [{index}/{total}] PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    [{index}/{total}] FAIL: {failText}");
        failures.Add(failure);
    }
}

void CheckSub(int index, int total, bool ok, string passText, string failText, string failure)
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

// ---------------------------------------------------------------------------------------
// 1. Schema creates cleanly on an empty directory; schema_version reads back 1.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/12] Schema creates cleanly on an empty directory; schema_version reads back 1...");
try
{
    var dbPath = Path.Combine(root, "linc.db");
    Check(1, 12, Directory.GetFiles(root).Length == 0,
        $"the temp root started empty ({root}).",
        $"the temp root was not empty at the start.", "Temp root not empty at start");

    var store = new LincStore(root);
    await store.EnsureSchemaAsync();

    Check(1, 12, File.Exists(dbPath),
        "linc.db was created at <root>\\linc.db.",
        "linc.db was not created at <root>\\linc.db.", "linc.db not created");

    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var version = ReadScalarInt(conn, "SELECT value FROM schema_version LIMIT 1;");
        Check(1, 12, version == LincStore.CurrentSchemaVersion && version == 1,
            $"schema_version reads back {version} (== CurrentSchemaVersion == 1).",
            $"schema_version read back {version}, expected 1.", "schema_version not 1");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/12] FAIL: schema-create check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Schema-create check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. EnsureSchemaAsync twice in a row is a no-op â€” same tables, version still 1.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/12] EnsureSchemaAsync twice is a no-op...");
try
{
    var dbPath = Path.Combine(root, "linc.db");
    var store = new LincStore(root);
    var beforeTables = ListTables(dbPath);
    await store.EnsureSchemaAsync();
    await store.EnsureSchemaAsync();
    var afterTables = ListTables(dbPath);

    var tablesUnchanged = beforeTables.Count == afterTables.Count
                          && !beforeTables.Except(afterTables).Any()
                          && !afterTables.Except(beforeTables).Any();
    CheckSub(2, 12, tablesUnchanged,
        "table set is unchanged after the second ensure.",
        "the table set changed after the second ensure.", "Duplicate table set mismatch");

    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var version = ReadScalarInt(conn, "SELECT value FROM schema_version LIMIT 1;");
        CheckSub(2, 12, version == 1,
            $"schema_version is still {version} after two more ensures.",
            $"schema_version drifted to {version} after re-ensure.", "Version drifted on re-ensure");

        var svRows = ReadScalarInt(conn, "SELECT count(*) FROM schema_version;");
        var notifIdx = ReadScalarInt(conn,
            "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_notifications_serial_posted';");
        var outboxIdx = ReadScalarInt(conn,
            "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_outbox_serial_id';");
        CheckSub(2, 12, svRows == 1 && notifIdx == 1 && outboxIdx == 1,
            $"one schema_version row, one of each named index (sv={svRows}, notif={notifIdx}, outbox={outboxIdx}).",
            $"counts drifted (sv={svRows}, notif={notifIdx}, outbox={outboxIdx}).",
            "Schema rows/indexes duplicated");
    }

    Check(2, 12, !failures.Any(f => f.StartsWith("Duplicate table", StringComparison.Ordinal)
                                   || f.StartsWith("Version drifted", StringComparison.Ordinal)
                                   || f.StartsWith("Schema rows", StringComparison.Ordinal)),
        "EnsureSchemaAsync twice left the schema byte-identical to once.",
        "double-ensure was not a no-op (see sub-failures above).", "Double-ensure not a no-op");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/12] FAIL: double-ensure check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Double-ensure check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 3. All five tables exist with the stated columns (D-044 2.3). The harness opens the file
//    directly with Microsoft.Data.Sqlite so it does not depend on LincStore's accessors.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[3/12] All five tables exist with the stated columns...");
try
{
    var dbPath = Path.Combine(root, "linc.db");
    using var c = new SqliteConnection($"Data Source={dbPath}");
    c.Open();

    var expected = new Dictionary<string, string[]>
    {
        ["schema_version"] = ["value"],
        ["devices"] = ["serial", "model", "first_paired_utc"],
        ["notifications"] = ["id", "serial", "posted_utc", "app_package", "title", "body", "key"],
        ["outbox"] = ["id", "serial", "queued_utc", "kind", "payload_json", "attempts"],
        ["sync_cache"] = ["serial", "kind", "key", "payload_json", "updated_utc"],
    };

    var allPresent = true;
    foreach (var (table, cols) in expected)
    {
        var actual = ListColumns(c, table);
        var missing = cols.Except(actual).ToList();
        if (missing.Count == 0)
        {
            Console.WriteLine($"        PASS: {table} has columns: {string.Join(", ", cols)}.");
        }
        else
        {
            Console.WriteLine($"        FAIL: {table} missing columns: {string.Join(", ", missing)}; actual: {string.Join(", ", actual)}.");
            allPresent = false;
        }
    }

    var deviceDdl = ReadScalarString(c, "SELECT sql FROM sqlite_master WHERE type='table' AND name='devices';");
    CheckSub(3, 12, deviceDdl.Contains("serial TEXT PRIMARY KEY", StringComparison.OrdinalIgnoreCase),
        "devices(serial) is the PRIMARY KEY.",
        "devices did not have serial as PRIMARY KEY.", "devices PK shape");

    var syncDdl = ReadScalarString(c, "SELECT sql FROM sqlite_master WHERE type='table' AND name='sync_cache';");
    CheckSub(3, 12, syncDdl.Contains("PRIMARY KEY (serial, kind, key)", StringComparison.OrdinalIgnoreCase),
        "sync_cache has the composite PRIMARY KEY (serial, kind, key).",
        "sync_cache composite PK wrong.", "sync_cache PK shape");

    var notifDdl = ReadScalarString(c, "SELECT sql FROM sqlite_master WHERE type='table' AND name='notifications';");
    CheckSub(3, 12, notifDdl.Contains("id INTEGER PRIMARY KEY AUTOINCREMENT", StringComparison.OrdinalIgnoreCase),
        "notifications.id is INTEGER PRIMARY KEY AUTOINCREMENT.",
        "notifications.id was not AUTOINCREMENT.", "notifications AUTOINCREMENT");

    var outboxDdl = ReadScalarString(c, "SELECT sql FROM sqlite_master WHERE type='table' AND name='outbox';");
    CheckSub(3, 12, outboxDdl.Contains("id INTEGER PRIMARY KEY AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                  && outboxDdl.Contains("attempts INTEGER DEFAULT 0", StringComparison.OrdinalIgnoreCase),
        "outbox.id is AUTOINCREMENT and attempts defaults to 0.",
        "outbox AUTOINCREMENT/attempts-default missing.", "outbox shape");

    var notifIdx = ReadScalarString(c,
        "SELECT sql FROM sqlite_master WHERE type='index' AND name='idx_notifications_serial_posted';");
    CheckSub(3, 12, notifIdx.Contains("(serial, posted_utc)", StringComparison.OrdinalIgnoreCase),
        "idx_notifications_serial_posted is on (serial, posted_utc).",
        "idx_notifications_serial_posted not on (serial, posted_utc).", "notifications index columns");

    var outboxIdx = ReadScalarString(c,
        "SELECT sql FROM sqlite_master WHERE type='index' AND name='idx_outbox_serial_id';");
    CheckSub(3, 12, outboxIdx.Contains("(serial, id)", StringComparison.OrdinalIgnoreCase),
        "idx_outbox_serial_id is on (serial, id).",
        "idx_outbox_serial_id not on (serial, id).", "outbox index columns");

    Check(3, 12, allPresent && !failures.Any(f => f.StartsWith("devices", StringComparison.Ordinal)
                                                  || f.StartsWith("sync_cache", StringComparison.Ordinal)
                                                  || f.StartsWith("notifications", StringComparison.Ordinal)
                                                  || f.StartsWith("outbox", StringComparison.Ordinal)),
        "all five tables present with the stated columns and key shapes.",
        "one or more tables had missing columns / wrong PK shape (see sub-failures above).",
        "Missing columns / wrong PK shape");
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/12] FAIL: table/column check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Table/column check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 4. Import from a registry holding two devices lands two devices rows; running it again
//    still leaves two (2.4 idempotence). The registry itself is constructed against the SAME
//    temp root so D-057 holds end-to-end.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/12] Import lands two devices, and re-importing is a no-op...");
try
{
    var regRoot = Path.Combine(root, "registry");
    Directory.CreateDirectory(regRoot);
    var registry = new DeviceRegistry(regRoot);
    registry.SavePairedDevice("TEST-0001", "Pixel 7");
    registry.SavePairedDevice("TEST-0002", "Pixel 8");

    CheckSub(4, 12, registry.KnownDevices.Count == 2,
        $"the registry fixture holds {registry.KnownDevices.Count} device(s).",
        $"the registry fixture held {registry.KnownDevices.Count} device(s).", "Registry fixture count");

    var store = new LincStore(root);
    await store.EnsureSchemaAsync();
    await store.ImportFromRegistryAsync(registry);
    CheckSub(4, 12, store.DeviceCount() == 2,
        $"first import landed {store.DeviceCount()} device(s).",
        $"first import landed {store.DeviceCount()} device(s), expected 2.", "First import count");

    await store.ImportFromRegistryAsync(registry);
    var secondCount = store.DeviceCount();
    Check(4, 12, secondCount == 2,
        $"re-importing still leaves {secondCount} device(s) â€” never duplicates (idempotence, 2.4).",
        $"re-importing left {secondCount} device(s) â€” it duplicated (2.4 violated).",
        "Import duplicated devices");

    var devices = store.ListDevices();
    CheckSub(4, 12,
        devices.Count == 2
        && devices.Any(d => d.Serial == "TEST-0001" && d.Model == "Pixel 7")
        && devices.Any(d => d.Serial == "TEST-0002" && d.Model == "Pixel 8"),
        "stored devices match the registry (serial + model).",
        "stored devices did not match the registry.", "Imported device content");

    using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "linc.db")}"))
    {
        conn.Open();
        CheckSub(4, 12, ReadScalarInt(conn, "SELECT count(*) FROM devices;") == 2,
            "SELECT count(*) FROM devices is 2.",
            "SELECT count(*) FROM devices was not 2.", "device row count SQL");
        CheckSub(4, 12,
            ReadScalarInt(conn, "SELECT count(DISTINCT serial) FROM devices;") == 2,
            "no duplicate serials in devices.",
            "there were duplicate serials in devices.", "duplicate serials");
    }

    var store2 = new LincStore(root);
    await store2.EnsureSchemaAsync();
    await store2.ImportFromRegistryAsync(registry);
    CheckSub(4, 12, store2.DeviceCount() == 2,
        "a second store instance re-importing over the same file still leaves 2.",
        "a fresh store instance over an existing DB duplicated.", "Fresh-instance idempotence");
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/12] FAIL: import check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Import check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. A corrupt linc.db (garbage bytes) degrades without throwing (D-044 2.6).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/12] A corrupt linc.db degrades without throwing...");
try
{
    var corruptRoot = Path.Combine(Path.GetTempPath(), "Linc_storesim_corrupt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(corruptRoot);
    var dbPath = Path.Combine(corruptRoot, "linc.db");
    File.WriteAllBytes(dbPath, "not a sqlite database, this is garbage bytes"u8.ToArray());

    var threw = false;
    LincStore? corruptStore = null;
    try
    {
        corruptStore = new LincStore(corruptRoot);
        await corruptStore.EnsureSchemaAsync();
    }
    catch (Exception ex)
    {
        threw = true;
        Console.WriteLine($"        FAIL: EnsureSchemaAsync on a corrupt DB threw {ex.GetType().Name}.");
    }

    CheckSub(5, 12, !threw,
        "EnsureSchemaAsync on a corrupt DB returned without throwing.",
        "EnsureSchemaAsync on a corrupt DB threw (2.6 violated).",
        "Corrupt DB threw");

    if (corruptStore is not null)
    {
        CheckSub(5, 12, !corruptStore.IsAvailable,
            "the corrupt store reports IsAvailable=false after the failure.",
            "the corrupt store reported IsAvailable=true after a failure.",
            "Corrupt store IsAvailable not false");
        CheckSub(5, 12, corruptStore.DeviceCount() == 0,
            "DeviceCount on a corrupt store returns 0 (no store).",
            "DeviceCount on a corrupt store returned non-zero.",
            "Corrupt store returned non-zero count");
        CheckSub(5, 12, corruptStore.ListDevices().Count == 0,
            "ListDevices on a corrupt store returns an empty list.",
            "ListDevices on a corrupt store returned non-empty.",
            "Corrupt store returned non-empty list");

        var opsOk = opsDegradeNoThrow(corruptStore, Path.Combine(corruptRoot, "reg"));
        CheckSub(5, 12, opsOk,
            "ListDevices and ImportFromRegistry on a corrupt store returned without throwing.",
            "a post-corrupt operation threw.", "Post-corrupt operation threw");
    }

    try { Directory.Delete(corruptRoot, recursive: true); } catch (IOException) { }

    Check(5, 12, !threw && (corruptStore is null || (!corruptStore.IsAvailable && corruptStore.DeviceCount() == 0)),
        "all corrupt-store paths degraded to 'no store' cleanly.",
        "a corrupt-store path did not degrade cleanly (above).", "Corrupt DB did not degrade");
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/12] FAIL: corrupt-DB check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Corrupt-DB check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 6. D-057: the store resolves under the injected root, and the real %LOCALAPPDATA%\Linc is
//    never touched. Asserted on the resolved path STRING only â€” never by writing to the real
//    store to find out.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/12] D-057: the store root is the registry's root, never %LOCALAPPDATA% directly...");
try
{
    var tempStore = new LincStore(root);
    CheckSub(6, 12, tempStore.DbPath == Path.Combine(root, "linc.db"),
        $"a temp-rooted store resolves to <root>\\linc.db.",
        $"a temp-rooted store resolved to {tempStore.DbPath}, expected {Path.Combine(root, "linc.db")}.",
        "Temp store path not under temp root");

    CheckSub(6, 12,
        !tempStore.DbPath.StartsWith(DeviceRegistry.DefaultRootPath, StringComparison.OrdinalIgnoreCase),
        "a temp-rooted store cannot resolve into the owner's real %LOCALAPPDATA%\\Linc\\linc.db.",
        "a temp-rooted store resolved INTO the owner's real store â€” D-057 is broken.",
        "Temp store escaped into the real store");

    var fromRegistry = new LincStore(new DeviceRegistry(root));
    CheckSub(6, 12, fromRegistry.DbPath == Path.Combine(root, "linc.db"),
        "LincStore(registry) puts linc.db under the registry's root (the rule, D-058).",
        $"LincStore(registry) resolved {fromRegistry.DbPath}, expected {Path.Combine(root, "linc.db")}.",
        "LincStore root injection");

    // The production path resolves to the real store â€” asserted AS A STRING ONLY. Nothing here
    // opens, creates, or writes %LOCALAPPDATA%\Linc/linc.db (D-057).
    var productionStore = new LincStore(DeviceRegistry.DefaultRootPath);
    var expectedProd = Path.Combine(DeviceRegistry.DefaultRootPath, "linc.db");
    CheckSub(6, 12, productionStore.DbPath == expectedProd,
        $"a production-rooted store resolves to {expectedProd} (string only â€” nothing written).",
        $"the production root resolved to {productionStore.DbPath}.",
        "Production store path");

    CheckSub(6, 12, File.Exists(productionStore.DbPath) == false
                  || productionStore.DbPath.StartsWith(DeviceRegistry.DefaultRootPath, StringComparison.OrdinalIgnoreCase),
        "the production path string is under %LOCALAPPDATA%\\Linc; nothing in this harness opened it.",
        "the production path check failed.", "Production path string");

    // No Environment.GetFolderPath anywhere in the new code â€” the rule D-057 was rebuilt twice
    // to enforce. Assert the file does not import or call it. Walk to the repo root; the build
    // runs from the harness directory so the parent walk finds DESKTOP\.
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var lincStoreSource = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "LincStore.cs");
    var lincStoreSourceOk = File.Exists(lincStoreSource);
    if (!lincStoreSourceOk)
    {
        Console.WriteLine($"        WARN: could not locate {lincStoreSource}; skipping source-string emission check.");
    }
    else
    {
        var source = File.ReadAllText(lincStoreSource);
        // A real call MUST have a `(` after the method name; the only mentions in LincStore.cs are
        // <see cref="Environment.GetFolderPath"/> (no paren) warning readers not to add the call.
        // Searching for the paren form keeps the docs honest without false-firing on themselves.
        var hasGetFolderPath = source.Contains("Environment.GetFolderPath(", StringComparison.Ordinal);
        CheckSub(6, 12, !hasGetFolderPath,
            "LincStore.cs contains no Environment.GetFolderPath( call (D-057).",
            "LincStore.cs contains an Environment.GetFolderPath( call — D-057 is broken.",
            "LincStore calls Environment.GetFolderPath");
    }

    Check(6, 12, !failures.Any(f => f.StartsWith("Temp store", StringComparison.Ordinal)
                                   || f.StartsWith("LincStore", StringComparison.Ordinal)
                                   || f.StartsWith("Production", StringComparison.Ordinal)
                                   || f.StartsWith("LincStore.cs", StringComparison.Ordinal)),
        "the store resolves under the injected root and never touches %LOCALAPPDATA% directly.",
        "a D-057 path check failed (above).", "D-057 path check failed");
}
catch (Exception ex)
{
    Console.WriteLine($"    [6/12] FAIL: D-057 check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"D-057 check threw: {ex.GetType().Name}: {ex.Message}");
}

// A killed run leaks this folder instead of damaging the owner's data (D-057).
try { Directory.Delete(root, recursive: true); } catch (IOException) { }

// ---------------------------------------------------------------------------------------
// 7. Notification insert + query round-trip (M9b/D-045). Each M9b check throws away its own
//    temp root so the §2.2 / prune / delete-all behaviour is exercised on a clean schema each
//    time. The LincStore methods carry the degrade-on-failure posture from §1, so a fresh
//    EnsureSchemaAsync runs before each check.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/12] Notification insert + query round-trip...");
try
{
    var root7 = Path.Combine(Path.GetTempPath(), "Linc_storesim_7_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root7);
    var db7 = Path.Combine(root7, "linc.db");
    var store7 = new LincStore(root7);
    await store7.EnsureSchemaAsync();

    var t = DateTimeOffset.UtcNow;
    var ok1 = await store7.InsertNotificationAsync("SER1", t, "pkg.app", "Hello", "secret body 1", "k1");
    var ok2 = await store7.InsertNotificationAsync("SER1", t.AddSeconds(-1), "pkg.app2", "Second", "secret body 2", "k2");
    var ok3 = await store7.InsertNotificationAsync("SER2", t, "pkg.other", "Other", "another body", "k3");
    CheckSub(7, 12, ok1 && ok2 && ok3,
        "three inserts across two serials returned true.",
        "insert returned false; something aborted early.", "Insert returned false");

    var ser1Recent = store7.ListRecentNotifications("SER1", limit: 100);
    CheckSub(7, 12, ser1Recent.Count == 2,
        $"ListRecent(SER1) returned {ser1Recent.Count} rows (expected 2).",
        $"ListRecent(SER1) returned {ser1Recent.Count} rows, expected 2.", "ListRecent count wrong");

    // Newest first is load-bearing (the index is (serial, posted_utc) but the SELECT DESCs it).
    var newestFirst = ser1Recent[0].Key == "k1" && ser1Recent[1].Key == "k2";
    CheckSub(7, 12, newestFirst,
        "rows come back newest-first.",
        "rows were not newest-first.", "Newest-first ordering broke");

    // Per-serial isolation: SER2 sees one row, not three.
    var ser2Recent = store7.ListRecentNotifications("SER2", limit: 100);
    CheckSub(7, 12, ser2Recent.Count == 1 && ser2Recent[0].Key == "k3",
        "ListRecent(SER2) returns only SER2's row (per-serial isolation).",
        "ListRecent(SER2) leaked rows from another serial.", "Per-serial isolation broke");

    // The row content round-trips — title/app/key preserved. Body is in the row but never logged
    // (the §11 crude check enforces that separately below).
    var first = ser1Recent[0];
    CheckSub(7, 12,
        first.AppPackage == "pkg.app" && first.Title == "Hello" && first.Key == "k1"
        && first.Serial == "SER1",
        "row content round-trips (serial/appPackage/title/key).",
        "row content did not round-trip.", "Row content round-trip");

    // The cap is respected — insert more than the limit and only the newest `limit` are returned.
    for (var i = 0; i < 5; i++)
    {
        await store7.InsertNotificationAsync("SER1", t.AddSeconds(i), "pkg.app", $"t{i}", $"b{i}", $"kc{i}");
    }
    var capped = store7.ListRecentNotifications("SER1", limit: 3);
    CheckSub(7, 12, capped.Count == 3 && capped[0].Key == "kc4",
        $"the limit caps the query to {capped.Count} rows, newest returned first.",
        $"the limit/pruning of the query was wrong: returned {capped.Count}.", "ListRecent limit");

    using (var c = new SqliteConnection($"Data Source={db7}"))
    {
        c.Open();
        var total = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        CheckSub(7, 12, total == 8, // 2 + 1 + 5 = 8
            $"total stored rows is {total} (expected 8).",
            $"total stored rows was {total}, expected 8.", "Total stored count");
    }

    Check(7, 12, !failures.Any(f => f.StartsWith("Insert", StringComparison.Ordinal)
                                    || f.StartsWith("ListRecent", StringComparison.Ordinal)
                                    || f.StartsWith("Newest", StringComparison.Ordinal)
                                    || f.StartsWith("Per-serial", StringComparison.Ordinal)
                                    || f.StartsWith("Row content", StringComparison.Ordinal)
                                    || f.StartsWith("Total stored", StringComparison.Ordinal)),
        "insert + query round-trip behaves (newest-first, per-serial isolation, capped, content round-trips).",
        "an insert/query check failed (see sub-failures above).", "Insert/query round-trip broken");

    try { Directory.Delete(root7, recursive: true); } catch (IOException) { }
}
catch (Exception ex)
{
    Console.WriteLine($"    [7/12] FAIL: insert/query check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Insert/query check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 8. §2.2 — ingesting with the toggle off leaves the table EMPTY. The store itself enforces no
//    toggle (it knows nothing about the preference; §2.2's check is at the ingestion point in
//    NotificationSyncService). Here we simulate that contract: a SIMULATED ingestion with the
//    toggle off is a NO-OP — no INSERT is issued. The harness mirrors exactly what the call site
//    does: `if (historyEnabled) await store.InsertNotificationAsync(...)`. The default for an
//    upgraded install with no stored preference is OFF (§2.1), so a fresh registry is the
//    faithful model of that scenario.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[8/12] §2.2 — ingesting with the toggle OFF leaves the table empty...");
try
{
    var root8 = Path.Combine(Path.GetTempPath(), "Linc_storesim_8_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root8);
    var db8 = Path.Combine(root8, "linc.db");
    var reg8 = new DeviceRegistry(root8); // fresh registry: history preference defaults OFF (§2.1)
    var store8 = new LincStore(root8);
    await store8.EnsureSchemaAsync();

    CheckSub(8, 12, !reg8.NotificationHistoryEnabled,
        "a fresh registry has NotificationHistoryEnabled = false (§2.1 default OFF).",
        "a fresh registry did not default history to OFF (§2.1 violated).", "Default is ON (§2.1)");

    // The exact ingestion-point contract — only the SAME `if (enabled) ... insert ...` guard the
    // real NotificationSyncService.OnCompanionMessage uses. Off → no call → empty table.
    var historyEnabled = reg8.NotificationHistoryEnabled;
    if (historyEnabled)
    {
        await store8.InsertNotificationAsync("SER1", DateTimeOffset.UtcNow, "pkg", "t", "body", "k");
    }

    using (var c = new SqliteConnection($"Data Source={db8}"))
    {
        c.Open();
        var rows = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        CheckSub(8, 12, rows == 0,
            $"with the toggle off, the notifications table is EMPTY ({rows} rows). The insert never happened.",
            $"with the toggle off, the table held {rows} rows (§2.2 violated — the insert must NOT happen).",
            "Off did not mean empty table (§2.2)");
    }

    // The negative-proof mirror: flip the toggle on and the SAME ingest path now writes. This is
    // the §2.5 "history starts the moment it is enabled" sanity check, and proof that the guard
    // itself — not the insert method — is what enforces §2.2.
    reg8.SaveNotificationHistoryEnabled(true);
    historyEnabled = reg8.NotificationHistoryEnabled;
    if (historyEnabled)
    {
        await store8.InsertNotificationAsync("SER1", DateTimeOffset.UtcNow, "pkg", "t", "body", "k");
    }
    using (var c = new SqliteConnection($"Data Source={db8}"))
    {
        c.Open();
        var rows = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        CheckSub(8, 12, rows == 1,
            "with the toggle on, the same path writes exactly one row (the guard now opens).",
            $"with the toggle on, the table held {rows} rows; expected 1.", "Toggle on did not insert");
        // Clean up that one probe row so the rest of §8's checks hold against an empty table.
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM notifications;";
        cmd.ExecuteNonQuery();
    }

    // §2.5: turning it OFF does not delete stored rows. The store has no such method through the
    // toggle; flipping the preference back to false and re-ingesting leaves the prior row intact.
    reg8.SaveNotificationHistoryEnabled(true);
    if (reg8.NotificationHistoryEnabled)
    {
        await store8.InsertNotificationAsync("SER1", DateTimeOffset.UtcNow, "pkg", "t2", "body2", "k2");
    }
    reg8.SaveNotificationHistoryEnabled(false); // OFF
    using (var c = new SqliteConnection($"Data Source={db8}"))
    {
        c.Open();
        var rows = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        CheckSub(8, 12, rows == 1,
            "turning the toggle OFF did NOT delete the stored row (§2.5 — Clear is the only deleter).",
            "turning the toggle OFF deleted stored rows (§2.5 violated).", "Toggle off deleted rows (§2.5)");
    }

    // M9c Part 0.1/0.2: CRUDE source-text check that the production guard is still real.
    //
    // §8 above models the `if (historyEnabled) await store.InsertNotificationAsync(...)` guard
    // that NotificationSyncService.OnCompanionMessage is supposed to keep. But the model is just
    // a model — if someone deletes the guard from the production file the harness stays green,
    // which is the hollow negative proof M9b shipped and the M9c task describes in §0.1.
    //
    // This is a deliberately crude text scan in the displaysim (M5c-3) style: it walks the
    // production file, finds the identifier `NotificationHistoryEnabled` on a non-comment line,
    // and verifies that either that same line calls `InsertNotificationAsync` or a line within
    // 5 below it does. The 5-line window is generous on purpose — it lets the real call site be
    // a few lines down (e.g. after the `is { } serial` binding at line 182 of the real file)
    // without false-firing. Crude because it is a tripwire, not a substitute for review; the
    // modelled guard above is the faithful behavior check and stays.
    var syncServicePath = Path.Combine(
        FindRepoRoot(Directory.GetCurrentDirectory()),
        "DESKTOP", "Linc.Desktop", "Services", "NotificationSyncService.cs");
    if (!File.Exists(syncServicePath))
    {
        CheckSub(8, 12, false,
            "found NotificationSyncService.cs.",
            $"could not locate {syncServicePath}; the crude guard check cannot run.",
            "NotificationSyncService.cs not found");
    }
    else
    {
        var syncSource = File.ReadAllText(syncServicePath);
        var syncLines = syncSource.Replace("\r\n", "\n").Split('\n');
        var guardLine = -1;
        for (var i = 0; i < syncLines.Length; i++)
        {
            var trimmed = syncLines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }
            if (syncLines[i].Contains("NotificationHistoryEnabled", StringComparison.Ordinal))
            {
                guardLine = i;
                break;
            }
        }
        if (guardLine < 0)
        {
            CheckSub(8, 12, false,
                "found the NotificationHistoryEnabled identifier in the production file.",
                "the production NotificationSyncService.cs has NO `NotificationHistoryEnabled` identifier — the §2.2 guard is gone.",
                "Production guard identifier missing");
        }
        else
        {
            // The same line must contain `InsertNotificationAsync`, or a line within 5 below it
            // must. The real file has the `is { } serial` destructure on the identifier line and
            // the insert call two lines down; the 5-line window tolerates drift.
            //
            // M9e: the insert call is now `InsertNotificationOnceReadyAsync(...)` — a private
            // wrapper that awaits LincStore.EnsureSchemaAsync() before calling the store's real
            // InsertNotificationAsync (closing the same cold-start IsAvailable race A2.1 fixed for
            // the sync_cache reads; see check 12 below). That identifier does not literally contain
            // `InsertNotificationAsync` as a substring, so it is named explicitly here rather than
            // loosening the scan to something generic like "Insert" — the check stays exactly as
            // tight, it just also recognizes this one known wrapper shape.
            var window = Math.Min(syncLines.Length, guardLine + 6); // guardLine + 5 below, inclusive
            var callNearby = false;
            for (var i = guardLine; i < window; i++)
            {
                if (syncLines[i].Contains("InsertNotificationAsync", StringComparison.Ordinal)
                    || syncLines[i].Contains("InsertNotificationOnceReadyAsync", StringComparison.Ordinal))
                {
                    callNearby = true;
                    break;
                }
            }
            CheckSub(8, 12, callNearby,
                $"the production guard at line {guardLine + 1} pairs `NotificationHistoryEnabled` with an insert call within 5 lines.",
                $"the production file has `NotificationHistoryEnabled` at line {guardLine + 1} but no InsertNotificationAsync/InsertNotificationOnceReadyAsync call within 5 lines — the §2.2 guard is hollow.",
                "Production guard not adjacent to InsertNotificationAsync");
        }
    }

    Check(8, 12, !failures.Any(f => f.StartsWith("Default", StringComparison.Ordinal)
                                    || f.StartsWith("Off did not", StringComparison.Ordinal)
                                    || f.StartsWith("Toggle", StringComparison.Ordinal)
                                    || f.StartsWith("Production guard", StringComparison.Ordinal)
                                    || f.StartsWith("NotificationSyncService.cs", StringComparison.Ordinal)),
        "off-before-ingest means an empty table, on means a row, off-after does NOT delete, and the production guard is real (§§2.2/2.5, Part 0).",
        "a §2.2/§2.5 or Part 0 guard check failed (above).", "§2.2 off-means-empty / production guard broken");

    try { Directory.Delete(root8, recursive: true); } catch (IOException) { }
}
catch (Exception ex)
{
    Console.WriteLine($"    [8/12] FAIL: §2.2 check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"§2.2 check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 9. Prune deletes only rows past the window and keeps the rest (§2.3). Insert rows of varying
//    ages against one serial, then prune to a window and assert exactly the old ones are gone.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[9/12] Prune deletes only rows past the window, keeps the rest...");
try
{
    var root9 = Path.Combine(Path.GetTempPath(), "Linc_storesim_9_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root9);
    var db9 = Path.Combine(root9, "linc.db");
    var store9 = new LincStore(root9);
    await store9.EnsureSchemaAsync();

    var now = DateTimeOffset.UtcNow;
    // Ages relative to `now`: 1d (keep), 10d (keep at 30d), 40d (gone at 30d), 100d (gone).
    await store9.InsertNotificationAsync("SER1", now.AddDays(-1), "a", "k", "body1", "k1");
    await store9.InsertNotificationAsync("SER1", now.AddDays(-10), "a", "k", "body10", "k10");
    await store9.InsertNotificationAsync("SER1", now.AddDays(-40), "a", "k", "body40", "k40");
    await store9.InsertNotificationAsync("SER1", now.AddDays(-100), "a", "k", "body100", "k100");
    // A second serial's row should ALSO be touched by a prune-all (serial=null).
    await store9.InsertNotificationAsync("SER2", now.AddDays(-50), "a", "k", "body50", "k50");

    // Prune to 30 days, ALL serials (this is the startup prune path, serial=null per §3.5).
    var removed = await store9.PruneNotificationsAsync(days: 30, serial: null);
    CheckSub(9, 12, removed == 3,
        $"prune(30, all) removed {removed} rows (the 40d, 50d, 100d ones).",
        $"prune(30, all) removed {removed} rows; expected 3.", "Prune-all removed wrong count");

    using (var c = new SqliteConnection($"Data Source={db9}"))
    {
        c.Open();
        var total = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        var ser1 = ReadScalarInt(c, "SELECT count(*) FROM notifications WHERE serial='SER1';");
        var ser2 = ReadScalarInt(c, "SELECT count(*) FROM notifications WHERE serial='SER2';");
        CheckSub(9, 12, total == 2 && ser1 == 2 && ser2 == 0,
            $"after prune, {total} rows remain (SER1={ser1}, SER2={ser2}); only the ones inside 30d survived.",
            $"after prune, wrong survivors: total={total}, SER1={ser1}, SER2={ser2}.", "Prune-all survivors wrong");

        var leftoverKeys = new HashSet<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT key FROM notifications ORDER BY key;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { leftoverKeys.Add(r.GetString(0)); }
        }
        CheckSub(9, 12, leftoverKeys.SetEquals(new[] { "k1", "k10" }),
            "the rows that survived are exactly the inside-window ones (k1=1d, k10=10d).",
            "the survivors were the wrong rows.", "Prune-all kept the wrong rows");
    }

    // Per-serial prune: insert three rows for SER3 at 1d/40d/100d and prune SER3 only.
    await store9.InsertNotificationAsync("SER3", now.AddDays(-1), "a", "k", "b", "s3k1");
    await store9.InsertNotificationAsync("SER3", now.AddDays(-40), "a", "k", "b", "s3k40");
    await store9.InsertNotificationAsync("SER3", now.AddDays(-100), "a", "k", "b", "s3k100");
    var removedSer3 = await store9.PruneNotificationsAsync(days: 30, serial: "SER3");
    using (var c2 = new SqliteConnection($"Data Source={db9}"))
    {
        c2.Open();
        var ser3 = ReadScalarInt(c2, "SELECT count(*) FROM notifications WHERE serial='SER3';");
        CheckSub(9, 12, removedSer3 == 2 && ser3 == 1,
            $"per-serial prune(SER3, 30) removed {removedSer3} and left {ser3} (only the 1d row).",
            $"per-serial prune left the wrong shape: removed={removedSer3}, ser3={ser3}.", "Per-serial prune shape");
    }

    // Pruning a window so short only the inside-window rows survive. After the 30d prune SER1
    // had k1 (1d) and k10 (10d); pruning SER1 to 7d removes k10 (10d) and keeps k1 (1d, inside 7d).
    var removedAll7 = await store9.PruneNotificationsAsync(days: 7, serial: "SER1");
    using (var c3 = new SqliteConnection($"Data Source={db9}"))
    {
        c3.Open();
        var ser1Now = ReadScalarInt(c3, "SELECT count(*) FROM notifications WHERE serial='SER1';");
        CheckSub(9, 12, removedAll7 == 1 && ser1Now == 1,
            "prune(SER1, 7) removed only the 10d row; the 1d row survived inside the 7d window.",
            $"prune(SER1, 7) removed {removedAll7} and left {ser1Now}; expected removed=1, left=1.",
            "Tight-window prune");
    }

    Check(9, 12, !failures.Any(f => f.StartsWith("Prune", StringComparison.Ordinal)
                                    || f.StartsWith("Per-serial", StringComparison.Ordinal)
                                    || f.StartsWith("Tight-window", StringComparison.Ordinal)),
        "prune is surgical (only past-window rows) and works all-devices or per-serial.",
        "a prune check failed (above).", "Prune not surgical");

    try { Directory.Delete(root9, recursive: true); } catch (IOException) { }
}
catch (Exception ex)
{
    Console.WriteLine($"    [9/12] FAIL: prune check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Prune check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 10. Delete-all returns the right count and empties the table (§2.4 — the Clear action). It
//     works whether the feature is on or off; the Clear button is the only thing that deletes
//     stored rows (§2.5).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[10/12] Delete-all returns the count and empties the table...");
try
{
    var root10 = Path.Combine(Path.GetTempPath(), "Linc_storesim_10_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root10);
    var db10 = Path.Combine(root10, "linc.db");
    var store10 = new LincStore(root10);
    await store10.EnsureSchemaAsync();

    // An empty store deletes nothing and reports 0.
    var emptyRemoved = await store10.DeleteAllNotificationsAsync();
    CheckSub(10, 12, emptyRemoved == 0,
        "DeleteAll on an empty store returns 0 (nothing to clear).",
        $"DeleteAll on an empty store returned {emptyRemoved}.", "DeleteAll empty count");

    var now = DateTimeOffset.UtcNow;
    await store10.InsertNotificationAsync("SER1", now, "a", "t", "b1", "k1");
    await store10.InsertNotificationAsync("SER1", now, "a", "t", "b2", "k2");
    await store10.InsertNotificationAsync("SER2", now, "a", "t", "b3", "k3");

    var removed = await store10.DeleteAllNotificationsAsync();
    CheckSub(10, 12, removed == 3,
        $"DeleteAll removed {removed} rows (expected 3 across two serials).",
        $"DeleteAll removed {removed}; expected 3.", "DeleteAll count wrong");

    using (var c = new SqliteConnection($"Data Source={db10}"))
    {
        c.Open();
        var rows = ReadScalarInt(c, "SELECT count(*) FROM notifications;");
        CheckSub(10, 12, rows == 0,
            "the table is empty after DeleteAll.",
            $"the table held {rows} rows after DeleteAll.", "DeleteAll not empty");
    }

    // A second DeleteAll right after is 0 — proves it is truly empty, not just "deleted some".
    var removedAgain = await store10.DeleteAllNotificationsAsync();
    CheckSub(10, 12, removedAgain == 0,
        "a follow-up DeleteAll returns 0 (the table was genuinely emptied).",
        $"a follow-up DeleteAll returned {removedAgain}; the table was not empty.", "DeleteAll not idempotent-empty");

    Check(10, 12, !failures.Any(f => f.StartsWith("DeleteAll", StringComparison.Ordinal)),
        "DeleteAll returns the exact count and leaves the table empty.",
        "a DeleteAll check failed (above).", "DeleteAll broken");

    try { Directory.Delete(root10, recursive: true); } catch (IOException) { }
}
catch (Exception ex)
{
    Console.WriteLine($"    [10/12] FAIL: delete-all check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Delete-all check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 11. Retention CHANGE to a SHORTER window prunes immediately (§2.3), AND a crude text check
//     that LincStore.cs never writes a notification body to the log (§2.9). The latter is
//     deliberately crude: it greps every line containing a `Log(` call and asserts the substring
//     `body` does not appear on it. That is a weak check by construction (it misses the body
//     captured under a different name, and it false-fires on the word "body" in a comment), so
//     it is commented as crude per house style — it catches the obvious case of passing the
//     `body` parameter into a logger. Per house style comments it as a crude guard.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[11/12] Retention-change prune + crude no-body-in-log check...");
try
{
    var root11 = Path.Combine(Path.GetTempPath(), "Linc_storesim_11_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root11);
    var db11 = Path.Combine(root11, "linc.db");
    var store11 = new LincStore(root11);
    await store11.EnsureSchemaAsync();

    var now = DateTimeOffset.UtcNow;
    await store11.InsertNotificationAsync("SER1", now.AddDays(-5), "a", "t", "b5", "k5");
    await store11.InsertNotificationAsync("SER1", now.AddDays(-20), "a", "t", "b20", "k20");
    await store11.InsertNotificationAsync("SER1", now.AddDays(-80), "a", "t", "b80", "k80");

    // The user owns a 90-day window. Shorten to 30 days and the 80-day row should vanish now
    // (§2.3 — the user shortening it expects old data gone immediately, not in a day).
    var removed = await store11.PruneNotificationsAsync(days: 30, serial: null);
    using (var c11 = new SqliteConnection($"Data Source={db11}"))
    {
        c11.Open();
        var rows = ReadScalarInt(c11, "SELECT count(*) FROM notifications;");
        var leftoverKeys = new HashSet<string>();
        using (var cmd = c11.CreateCommand())
        {
            cmd.CommandText = "SELECT key FROM notifications;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { leftoverKeys.Add(r.GetString(0)); }
        }
        CheckSub(11, 12, removed == 1 && rows == 2 && leftoverKeys.SetEquals(new[] { "k5", "k20" }),
            $"shortening 90→30 pruned the 80d row immediately (removed={removed}, survivors={rows}, keys={string.Join(",", leftoverKeys)}).",
            $"shortening 90→30 did NOT prune immediately (removed={removed}, rows={rows}).", "Retention-shorten prune");

        // Mirror the §2.3 "Shorten to 7 days via the Settings path" which prunes both old rows.
        // In the real SettingsViewModel.SetRetention, widening does nothing and shortening fires
        // a prune — model its decisions directly here so the contract is exercised against the store.
        // oldDays was 30 above (we already shortened); now shorten further to 7.
        var removedTo7 = await store11.PruneNotificationsAsync(days: 7, serial: null);
        var rowsTo7 = ReadScalarInt(c11, "SELECT count(*) FROM notifications;");
        CheckSub(11, 12, removedTo7 == 1 && rowsTo7 == 1,
            $"shortening 30→7 pruned the 20d row immediately (left {rowsTo7}).",
            $"shortening 30→7 left rows={rowsTo7}.{removedTo7}", "Tighten to 7 didn't pru");

        // Widening from 7 back to 30 does NOT prune anything. No row inside 7d is less than 30d old.
        var removedWiden = await store11.PruneNotificationsAsync(days: 30, serial: null);
        var rowsAfterWiden = ReadScalarInt(c11, "SELECT count(*) FROM notifications;");
        CheckSub(11, 12, removedWiden == 0 && rowsAfterWiden == 1,
            "widening 7→30 pruned nothing (a tighten-only field).",
            "widening pruned rows (§2.3 is tighten-only).", "Widen pruned (§2.3)");
    }

    // §2.9 — the crude no-body-in-log check (see comment above):
    //     CRUDE: this string scan only catches an obvious `body`/`Body` passed INTO a Log call.
    //     It is intentionally weak — the real guard is Never logging the body (commented in
    //     LincStore.cs) and exercised by §7's round-trip which left bodies in the table without
    //     any logger touching them. We include this here as a tripwire for the obvious case,
    //     per house style — not as a substitute for review.
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var lincStoreSource = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "LincStore.cs");
    var sourceOk = File.Exists(lincStoreSource);
    if (!sourceOk)
    {
        Console.WriteLine($"        WARN: could not locate {lincStoreSource}; skipping §2.9 crude scan.");
    }
    else
    {
        var source = File.ReadAllText(lincStoreSource);
        var logLines = source.Split('\n')
            .Select(l => l.Trim())
            // CRUDE (continued): skip pure comment lines so a `body` mention in a comment does
            // not fire. We still flag an actual code line that logs AND mentions `body`.
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("*", StringComparison.Ordinal))
            .Where(l => l.Contains("Log(", StringComparison.Ordinal) || l.Contains(".Log(LogLevel", StringComparison.Ordinal))
            .ToList();
        // A `body`/`Body` token on any Log( line means the body parameter was almost certainly
        // interpolated into a message — exactly what §2.9 forbids. We deliberately allow `body`
        // in comments and elsewhere; only the actual logger calls are scanned.
        var bad = logLines.Where(l => l.Contains("body", StringComparison.OrdinalIgnoreCase)).ToList();
        CheckSub(11, 12, bad.Count == 0,
            $"no LincStore.cs Log( call interpolates `body` (scanned {logLines.Count} log calls).",
            $"a Log( call mentions `body`: {string.Join(" | ", bad)}.", "Log call interpolates body");
    }

    Check(11, 12, !failures.Any(f => f.StartsWith("Retention-shorten", StringComparison.Ordinal)
                                     || f.StartsWith("Tighten to", StringComparison.Ordinal)
                                     || f.StartsWith("Widen pruned", StringComparison.Ordinal)
                                     || f.StartsWith("Log call interpolates", StringComparison.Ordinal)),
        "shorten-prunes-now, widen-leaves-it, and no body leaks into a log call (§§2.3/2.9).",
        "a retention/no-body check failed (above).", "Retention/no-body check failed");

    try { Directory.Delete(root11, recursive: true); } catch (IOException) { }
}
catch (Exception ex)
{
    Console.WriteLine($"    [11/12] FAIL: retention/no-body check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Retention/no-body check threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 12. CRUDE source-text check (M9e, §A2.4/A3.5). The task's own hypothesis for "notification
//     history stores nothing" was `registry.PairedSerial` being null at the guard. Measurement
//     this session found that hypothesis FALSE against the real store (13 real rows already
//     present, correctly gated) — but it found a real, narrower, silent-drop bug of the exact
//     same SHAPE as A2.1's actual root cause: LincStore.InsertNotificationAsync also gates on
//     IsAvailable, which is false for a short cold-start window before App.xaml.cs's
//     fire-and-forget EnsureSchemaAsync completes, and a notification arriving in that window is
//     dropped with NO log line (unlike a real SqliteException, which does log). The fix wraps the
//     insert in `InsertNotificationOnceReadyAsync`, which awaits the idempotent
//     EnsureSchemaAsync() first. This check fails if that await is removed — the actual
//     regression this task's own A2.4 hypothesis would NOT have caught, since PairedSerial was
//     never the problem.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[12/12] CRUDE: the notification insert wrapper awaits EnsureSchemaAsync first...");
try
{
    var syncServicePath12 = Path.Combine(
        FindRepoRoot(Directory.GetCurrentDirectory()),
        "DESKTOP", "Linc.Desktop", "Services", "NotificationSyncService.cs");
    if (!File.Exists(syncServicePath12))
    {
        Check(12, 12, false, "found NotificationSyncService.cs.", $"could not locate {syncServicePath12}; the crude check cannot run.", "NotificationSyncService.cs not found (check 12)");
    }
    else
    {
        var source = File.ReadAllText(syncServicePath12).Replace("\r\n", "\n");
        var methodStart = source.IndexOf("private async Task InsertNotificationOnceReadyAsync(", StringComparison.Ordinal);
        CheckSub(12, 12, methodStart >= 0,
            "found the InsertNotificationOnceReadyAsync wrapper method.",
            "InsertNotificationOnceReadyAsync not found in NotificationSyncService.cs — the M9e wrapper may have been renamed or removed.",
            "InsertNotificationOnceReadyAsync not found");

        if (methodStart >= 0)
        {
            var methodClose = source.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
            var methodBody = methodClose > methodStart ? source[methodStart..methodClose] : source[methodStart..];
            var ensureIndex = methodBody.IndexOf("await store.EnsureSchemaAsync();", StringComparison.Ordinal);
            var insertIndex = methodBody.IndexOf("await store.InsertNotificationAsync(", StringComparison.Ordinal);
            CheckSub(12, 12, ensureIndex >= 0 && insertIndex >= 0 && ensureIndex < insertIndex,
                "the wrapper awaits EnsureSchemaAsync() BEFORE calling store.InsertNotificationAsync.",
                "the wrapper does not await EnsureSchemaAsync() before store.InsertNotificationAsync — a notification " +
                "arriving during the cold-start schema race would be silently dropped with no log (the real defect this " +
                "session found under A2.4).",
                "Notification insert missing the EnsureSchemaAsync race guard");
        }
    }

    Check(12, 12, !failures.Any(f => f is "InsertNotificationOnceReadyAsync not found" or "Notification insert missing the EnsureSchemaAsync race guard"),
        "the notification insert path awaits EnsureSchemaAsync before writing, same as the sync_cache reads.",
        "the notification insert path can race LincStore.IsAvailable (see sub-failures above).",
        "Notification insert race guard missing");
}
catch (Exception ex)
{
    Console.WriteLine($"    [12/12] FAIL: notification-insert race-guard check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Notification-insert race-guard check threw: {ex.GetType().Name}: {ex.Message}");
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

static bool opsDegradeNoThrow(LincStore store, string regRoot)
{
    try
    {
        _ = store.ListDevices();
        _ = store.DeviceCount();
        var reg = new DeviceRegistry(regRoot);
        Directory.CreateDirectory(regRoot);
        reg.SavePairedDevice("TEST-DEGRADE", "Pixel");
        store.ImportFromRegistryAsync(reg).GetAwaiter().GetResult();
        return true;
    }
    catch
    {
        return false;
    }
}

static int ReadScalarInt(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var raw = cmd.ExecuteScalar();
    return raw is null || raw == DBNull.Value ? -1 : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
}

static string ReadScalarString(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var raw = cmd.ExecuteScalar();
    return raw is null || raw == DBNull.Value ? "" : (string)raw;
}

static List<string> ListTables(string dbPath)
{
    var tables = new List<string>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        tables.Add(r.GetString(0));
    }
    return tables;
}

static List<string> ListColumns(SqliteConnection conn, string table)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"PRAGMA table_info({table});";
    using var r = cmd.ExecuteReader();
    var cols = new List<string>();
    while (r.Read())
    {
        cols.Add(r.GetString(1));
    }
    return cols;
}
