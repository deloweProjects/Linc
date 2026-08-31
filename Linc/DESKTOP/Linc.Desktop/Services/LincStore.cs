using System.IO;
using Microsoft.Data.Sqlite;

namespace Linc.Desktop.Services;

/// <summary>
/// One persisted store row for the <c>devices</c> table (M9a). Only the foundation fields —
/// later sessions own everything that goes into <c>notifications</c>, <c>outbox</c> and
/// <c>sync_cache</c>.
/// </summary>
public readonly record struct StoredDevice(string Serial, string? Model, DateTimeOffset FirstPairedUtc);

/// <summary>
/// One persisted row in <c>notifications</c> (M9b, D-045). <b>Text only</b> — no icon/art blobs
/// this session (D-045 §2.8); a text-only history under retention cannot grow without bound.
/// <see cref="Body"/> is the consented exception to D-032's off-disk rule and is the only field
/// here that must never appear in a log line (§2.9).
/// </summary>
public readonly record struct StoredNotification(
    long Id,
    string Serial,
    DateTimeOffset PostedUtc,
    string? AppPackage,
    string? Title,
    string? Body,
    string? Key);

/// <summary>
/// One persisted row in <c>outbox</c> (M9c). The offline-action queue — a row is one user action
/// that was issued while the phone was disconnected, waiting to be flushed in <see cref="Id"/>
/// order on the next reconnect. <see cref="Kind"/> is the action wire-type (<c>"sms.send"</c>
/// this session — <see cref="OutboxService"/>'s flush is a <c>switch</c> on this string so a
/// later session can add a second kind without touching LincStore); <see cref="PayloadJson"/>
/// is the serialised argument set, the same consented on-disk category as a notification body —
/// <b>it must never appear in a log line</b> (§2.11). <see cref="Attempts"/> counts flush
/// tries; a row at <see cref="OutboxService.MaxAttempts"/> is dropped at the head of the next
/// flush, not retried (§2.7).
/// </summary>
public readonly record struct StoredOutboxRow(
    long Id,
    string Serial,
    DateTimeOffset QueuedUtc,
    string Kind,
    string PayloadJson,
    int Attempts);

/// <summary>
/// One persisted row in <c>sync_cache</c> (M9d-1, D-032/D-044). <see cref="Kind"/> is
/// <c>"conversation"</c>, <c>"call"</c> or <c>"photo"</c>; <see cref="Key"/> is that item's
/// natural identity (a conversation's address, a call's number+date, a photo's id) and, along
/// with <see cref="PayloadJson"/>, is the consented on-disk category that must never appear in a
/// log line (§2.8) — a key can itself be a phone number. <see cref="UpdatedUtc"/> is when this
/// row was last written, which is what the Home offline banner's "last synced at" time reads.
/// </summary>
public readonly record struct StoredSyncCacheRow(
    string Serial,
    string Kind,
    string Key,
    string PayloadJson,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// The SQLite persistence foundation (M9a, D-044). One file at
/// <c><root>\linc.db</c> beside the other Linc stores, holding known devices, notification
/// history, the offline outbox and cache-first Sync data. <b>This session builds only the
/// schema and the device import — nothing reads from it yet</b>; <c>settings.json</c> stays the
/// source of truth so the schema can land with zero regression risk.
///
/// <para><b>The root is injected, never resolved here</b> — it comes from
/// <see cref="DeviceRegistry.RootPath"/>, exactly as <see cref="AppCatalog"/> and
/// <see cref="AppWindowStore"/> take it (D-057/D-058). A <see cref="Environment.GetFolderPath"/>
/// call in this file would quietly reopen the hole those two decisions closed: a harness pointed
/// at a temp root would write its <c>linc.db</c> into the owner's real store. That rule has been
/// broken twice and cost real user data both times, so every construction of this class takes an
/// explicit root.</para>
///
/// <para><b>A corrupt or unreadable <c>linc.db</c> must never crash the app.</b> On any open or
/// schema failure the store logs in plain language and degrades to "no store" — every method
/// becomes a no-op or returns empty results, and the app runs exactly as it does today. This is
/// the whole reason the store is non-authoritative this session: nothing depends on it, so
/// nothing can break (D-044, 2.6).</para>
///
/// <para><b>Migration is one-way and additive</b> (2.4). <see cref="EnsureSchemaAsync"/> reads
/// <c>schema_version</c>, applies the steps in order, and writes the new version — even though
/// today there is only step 1 — so a future session can extend it without a destructive
/// rewrite. <see cref="ImportFromRegistryAsync"/> is idempotent (uses <c>INSERT OR IGNORE</c>),
/// <b>never</b> deletes or rewrites <c>settings.json</c>, and re-running the import leaves two
/// devices two — never four (2.4).</para>
/// </summary>
public sealed class LincStore
{
    /// <summary>
    /// The schema version this build writes. <see cref="EnsureSchemaAsync"/><c /> applies the
    /// ordered migrations from 0 up to here, then stores this number in <c>schema_version</c>.
    /// A future session bumps it and adds a step below.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private const string DbFileName = "linc.db";

    private readonly string _root;
    private readonly ILogService? _log;
    private readonly object _lock = new();

    /// <summary>
    /// <c>true</c> once the schema was verified and the store is usable; <c>false</c> after a
    /// construction, schema-ensure or import that swallowed a failure. While <c>false</c> every
    /// method is a no-op or returns empty results — the app simply has no store this session,
    /// which is the design (D-044 2.6).
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The <c><root>\linc.db</c> path this instance resolves to.</summary>
    public string DbPath { get; }

    /// <param name="rootPath">
    /// The Linc store directory — pass <see cref="DeviceRegistry.RootPath"/>. Production gets the
    /// owner's real store because the registry resolved it; a harness gets its temp root for the
    /// same reason. <b>Never</b> call <see cref="Environment.GetFolderPath"/> to build this.
    /// </param>
    public LincStore(string rootPath, ILogService? log = null)
    {
        _root = rootPath;
        _log = log;
        DbPath = Path.Combine(rootPath, DbFileName);
    }

    /// <summary>Convenience overload: take the root from the registry, which is the rule (D-058).</summary>
    public LincStore(IDeviceRegistry registry, ILogService? log = null)
        : this(registry.RootPath, log)
    {
    }

    /// <summary>
    /// Reads <c>schema_version</c>, applies the ordered migration steps up to
    /// <see cref="CurrentSchemaVersion"/>, and writes the new version back. Idempotent: the table
    /// shapes use <c>IF NOT EXISTS</c>, so running it twice leaves exactly one set of tables and
    /// the version stays at <see cref="CurrentSchemaVersion"/>. Safe to call at startup.
    /// </summary>
    /// <remarks>
    /// Today only step 1 (the initial schema) exists; the read-version / apply-in-order /
    /// write-version shape is here so a future migration is <i>additive</i>, never a destructive
    /// rewrite. <b>Do not</b> hand-roll a "drop and recreate" shortcut — that becomes data loss
    /// the moment M9b stores real notifications (D-044 2.5).
    /// </remarks>
    public async Task EnsureSchemaAsync()
    {
        lock (_lock)
        {
            if (!TryOpen(out var connection))
            {
                return;
            }
            using (connection)
            {
                try
                {
                    var from = ReadVersion(connection);
                    ApplyMigrations(connection, from);
                    WriteVersion(connection, CurrentSchemaVersion);
                    IsAvailable = true;
                    _log?.Log(LogLevel.Info, $"LincStore: schema ready at version {CurrentSchemaVersion}.");
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Error, $"LincStore: could not ensure the schema; the store is unavailable. ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Imports every <see cref="KnownDevice"/> from the registry into <c>devices</c> when that
    /// table is empty, and is a no-op otherwise. <b>Never deletes or rewrites
    /// <c>settings.json</c></b> — the registry keeps owning it (D-044 2.4). Idempotent even if
    /// the table already has rows: uses <c>INSERT OR IGNORE</c>, so re-running the import leaves
    /// two devices two, never four.
    /// </summary>
    public async Task ImportFromRegistryAsync(IDeviceRegistry registry)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return;
            }
            using (connection)
            {
                try
                {
                    foreach (var device in registry.KnownDevices)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = """
                            INSERT OR IGNORE INTO devices (serial, model, first_paired_utc)
                            VALUES ($serial, $model, $first_paired_utc);
                            """;
                        cmd.Parameters.AddWithValue("$serial", device.Serial);
                        cmd.Parameters.AddWithValue("$model", (object?)device.Model ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$first_paired_utc",
                            device.FirstPairedUtc.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                    var count = CountDevices(connection);
                    _log?.Log(LogLevel.Info, $"LincStore: imported devices from the registry; {count} device(s) now stored.");
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Error, $"LincStore: device import failed; the store is unavailable. ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        await Task.CompletedTask;
    }

    /// <summary>Every stored device, ordered as inserted. Empty if the store is unavailable.</summary>
    public IReadOnlyList<StoredDevice> ListDevices()
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return [];
            }
            using (connection)
            {
                try
                {
                    return ReadDevices(connection);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not read devices; degrading to no store. ({ex.GetType().Name}: {ex.Message})");
                    return [];
                }
            }
        }
    }

    /// <summary>The number of stored devices, or 0 if the store is unavailable.</summary>
    public int DeviceCount()
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    return CountDevices(connection);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not count devices; degrading to no store. ({ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    // ---- notifications (M9b, D-045) ----
    //
    // The privacy posture (§2) is load-bearing here and is the reason these methods are shaped the
    // way they are. Four rules, all enforced in the comments and verified by the harness (§6):
    //   1. **Nothing is written while the feature is off (§2.2).** The insert is guarded at the
    //      *call site* (`NotificationSyncService`), never here — this file knows nothing about the
    //      preference. A row does not get "written then hidden"; the INSERT must not happen.
    //   2. **Text only (§2.8).** No icon/art columns were added to the table and none ever are
    //      this session; blobs are deferred to a hashing/GC design of their own.
    //   3. **Never log the body (§2.9).** Every plain-language log line below names the SERIAL and
    //      the KEY — never the body. A `body` variable as a `Log()` argument here is a bug. The
    //      harness (§6) greps the source for exactly that as a crude guard.
    //   4. **Degrade, never crash (D-044 2.6).** A corrupt/unreadable `linc.db` makes these into
    //      no-ops / empty results, like every other method here.

    /// <summary>
    /// Inserts one notification row. <b>The caller must have already checked
    /// <see cref="IDeviceRegistry.NotificationHistoryEnabled"/></b> — this method never guards
    /// that test itself. Writing a notification body to SQLite is the consented exception to
    /// D-032 (§2.1); the insert is allowed to put <paramref name="body"/> in the row, but no log
    /// line in this method mentions it (§2.9). No-op and returns <c>false</c> if the store is
    /// unavailable, so a quiet failure never blocks the in-memory notification feed.
    /// </summary>
    public async Task<bool> InsertNotificationAsync(
        string serial, DateTimeOffset postedUtc, string? appPackage, string? title, string? body, string? key)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return false;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO notifications (serial, posted_utc, app_package, title, body, key)
                        VALUES ($serial, $posted_utc, $app_package, $title, $body, $key);
                        """;
                    cmd.Parameters.AddWithValue("$serial", serial);
                    cmd.Parameters.AddWithValue("$posted_utc", postedUtc.UtcDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$app_package", (object?)appPackage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
                    // Body is the consented on-disk exception (§2.1). It is never logged (§2.9).
                    cmd.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not insert a notification; degrading to no store. (serial={serial}, key={key}, {ex.GetType().Name}: {ex.Message})");
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Recent stored notifications for one device, newest first, capped at <paramref name="limit"/>
    /// (default 200 — enough for a history surface without pulling the whole table into memory).
    /// Reads with the same degrade-to-empty posture as the device accessors; never throws.
    /// </summary>
    public IReadOnlyList<StoredNotification> ListRecentNotifications(string serial, int limit = 200)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return [];
            }
            using (connection)
            {
                try
                {
                    return ReadNotifications(connection, serial, limit);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not list notifications; degrading to no store. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
                    return [];
                }
            }
        }
    }

    /// <summary>
    /// Deletes every stored row older than <paramref name="days"/> days, for a single serial
    /// when one is given, or for *every* device when <paramref name="serial"/> is null. Returns
    /// the number of rows removed, or 0 if the store is unavailable. Used at startup (§3.5) and
    /// whenever the retention setting is shortened to a tighter window (§2.3). Never throws.
    /// </summary>
    public async Task<int> PruneNotificationsAsync(int days, string? serial = null)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    // `days` is the user's retention window (7/30/90). A row whose posted_utc is
                    // older than now - days is gone. Parameter-bound, never string-interpolated.
                    if (serial is { } s)
                    {
                        cmd.CommandText = """
                            DELETE FROM notifications
                            WHERE serial = $serial AND posted_utc < $cutoff;
                            """;
                        cmd.Parameters.AddWithValue("$serial", s);
                    }
                    else
                    {
                        cmd.CommandText = "DELETE FROM notifications WHERE posted_utc < $cutoff;";
                    }
                    cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("o"));
                    var removed = cmd.ExecuteNonQuery();
                    if (removed > 0)
                    {
                        _log?.Log(LogLevel.Info, $"LincStore: pruned {removed} notification row(s) older than {days} day(s) (serial={(serial ?? "all")}).");
                    }
                    return removed;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not prune notifications; degrading to no store. (serial={(serial ?? "all")}, {ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    /// <summary>
    /// Deletes every stored notification, for all devices, regardless of age. Returns the row
    /// count removed. The "Clear history" action (§2.4) calls this and reports the count back to
    /// the user in plain language. Works whether the feature is on or off — turning it off does
    /// NOT delete stored rows (this method's job), it only stops new ones (§2.5). Never throws.
    /// </summary>
    public async Task<int> DeleteAllNotificationsAsync()
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM notifications;";
                    var removed = cmd.ExecuteNonQuery();
                    _log?.Log(LogLevel.Info, $"LincStore: cleared all stored notification history ({removed} row(s)).");
                    return removed;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not clear notifications; degrading to no store. ({ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    // ---- outbox (M9c) ----
    //
    // The offline-action queue. A row is one user action (this session: a single `sms.send`)
    // that was issued while the phone was disconnected. `OutboxService` flushes them on the next
    // transition into Connected, in `id` ASC order, per serial. The rules in this block mirror
    // the privacy and degrade posture of the notification methods above:
    //   1. **Never log the payload** (§2.11). `payload_json` is the same consented category as a
    //      notification body — it is stored on disk but no log line in this file interpolates it.
    //      The harness greps for that as a crude guard, exactly as it does for `body`.
    //   2. **Degrade, never crash (D-044 2.6).** A corrupt/unreadable `linc.db` makes these into
    //      no-ops / empty results, like every other method here. A phone that reconnects must
    //      never crash the app because the queue is malformed.
    //   3. **Per-serial isolation.** Every list/count query takes an explicit `serial`. A row
    //      queued on device A is invisible to device B's flush (§2.3 — the flush is per the
    //      currently-paired serial only).

    /// <summary>
    /// Inserts one queued action row and returns its new <c>id</c>, or <c>-1</c> if the store is
    /// unavailable. <paramref name="kind"/> is the action wire-type (<c>"sms.send"</c> this
    /// session); <paramref name="payloadJson"/> is the serialised argument set (the SMS address
    /// + body for <c>sms.send</c> — see <see cref="OutboxService"/>). <paramref name="serial"/>
    /// is the paired phone the row is destined for; only rows for the currently-paired serial
    /// are ever flushed (§2.3). <c>queued_utc</c> is now; <c>attempts</c> is 0. Never throws.
    /// </summary>
    public async Task<long> EnqueueOutboxAsync(string serial, string kind, string payloadJson)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return -1;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO outbox (serial, queued_utc, kind, payload_json, attempts)
                        VALUES ($serial, $queued_utc, $kind, $payload_json, 0);
                        SELECT last_insert_rowid();
                        """;
                    cmd.Parameters.AddWithValue("$serial", serial);
                    cmd.Parameters.AddWithValue("$queued_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$kind", kind);
                    // Payload is the consented on-disk exception; never logged (§2.11).
                    cmd.Parameters.AddWithValue("$payload_json", payloadJson);
                    var raw = cmd.ExecuteScalar();
                    var id = raw is null || raw == DBNull.Value
                        ? -1L
                        : Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
                    _log?.Log(LogLevel.Info, $"LincStore: outbox enqueued for serial={serial}, kind={kind}, id={id}.");
                    return id;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not enqueue an outbox row; degrading to no store. (serial={serial}, kind={kind}, {ex.GetType().Name}: {ex.Message})");
                    return -1;
                }
            }
        }
    }

    /// <summary>
    /// Every pending outbox row for one serial, ordered by <c>id</c> ASC — insertion order is
    /// queue order (§2.3). Empty if the store is unavailable. The flush walks this list in
    /// order and stops at the first failure (§2.5 — skipping would reorder the user's messages,
    /// which is worse than delaying them).
    /// </summary>
    public IReadOnlyList<StoredOutboxRow> ListPendingOutbox(string serial)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return [];
            }
            using (connection)
            {
                try
                {
                    return ReadOutbox(connection, serial);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not list outbox rows; degrading to no store. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
                    return [];
                }
            }
        }
    }

    /// <summary>
    /// Increments <c>attempts</c> for one row and returns the new attempts count, or <c>-1</c>
    /// if the store is unavailable (§2.7 — used by the flush to count failed attempts; a row at
    /// <see cref="OutboxService.MaxAttempts"/> is dropped, not retried). Never throws.
    /// </summary>
    public async Task<int> IncrementOutboxAttemptsAsync(long id)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return -1;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        UPDATE outbox SET attempts = attempts + 1 WHERE id = $id;
                        SELECT attempts FROM outbox WHERE id = $id;
                        """;
                    cmd.Parameters.AddWithValue("$id", id);
                    var raw = cmd.ExecuteScalar();
                    return raw is null || raw == DBNull.Value
                        ? -1
                        : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not increment outbox attempts; degrading to no store. (id={id}, {ex.GetType().Name}: {ex.Message})");
                    return -1;
                }
            }
        }
    }

    /// <summary>
    /// Deletes one outbox row by id. Used by the flush after a successful send (§2.6 — a returned
    /// task means the phone accepted the text) and when a row is dropped at
    /// <see cref="OutboxService.MaxAttempts"/> (§2.7). Returns <c>true</c> if a row was removed,
    /// <c>false</c> otherwise (including when the store is unavailable). Never throws.
    /// </summary>
    public async Task<bool> DeleteOutboxRowAsync(long id)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return false;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM outbox WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    var removed = cmd.ExecuteNonQuery();
                    return removed > 0;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not delete an outbox row; degrading to no store. (id={id}, {ex.GetType().Name}: {ex.Message})");
                    return false;
                }
            }
        }
    }

    /// <summary>The number of pending outbox rows for one serial, or <c>0</c> if the store is unavailable. Never throws.</summary>
    public int CountPendingOutbox(string serial)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM outbox WHERE serial = $serial;";
                    cmd.Parameters.AddWithValue("$serial", serial);
                    return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not count outbox rows; degrading to no store. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    // ---- sync_cache (M9d-1, D-032/D-044) ----
    //
    // The offline read-through/write-through store behind Home (D-032: the UI never blanks on
    // disconnect). A row is one item HomeViewModel last saw live — a photo, a conversation (the
    // whole thread under one row, keyed by address), or a call-log entry — keyed
    // (serial, kind, key) so a repeat write REPLACES rather than duplicates (§2.1). The rules
    // mirror the notification/outbox posture above:
    //   1. **Never log the key** (§2.8). A conversation's key is the address and a call's key
    //      embeds the number — both are exactly the "phone numbers" §2.8 forbids in a log line.
    //      Every log line below names the SERIAL and KIND only, matching the row count the
    //      caller already knows, never the key or payload_json.
    //   2. **Degrade, never crash (D-044 2.6).** A corrupt/unreadable `linc.db` makes these
    //      no-ops / empty results, like every other method here — Home simply renders empty,
    //      exactly as it does today with no store at all.
    //   3. **Per-serial isolation**, same as outbox: every query takes an explicit `serial` so
    //      switching device tabs never blends one phone's cache into another's (§2.6).
    //   4. **Retention is the caller's job.** <see cref="PruneSyncCacheAsync"/> is a separate
    //      call so a caller who does not want the 200-row cap (none exist yet) is not forced
    //      into it; HomeViewModel calls it after every write-through batch (§2.9).

    /// <summary>Inserts or replaces one row. Upsert on <c>(serial, kind, key)</c> so a re-load
    /// of the same item replaces its payload/timestamp rather than duplicating the row (§2.1).
    /// Returns <c>false</c> if the store is unavailable. Never throws.</summary>
    public async Task<bool> UpsertSyncCacheRowAsync(string serial, string kind, string key, string payloadJson, DateTimeOffset updatedUtc)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return false;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO sync_cache (serial, kind, key, payload_json, updated_utc)
                        VALUES ($serial, $kind, $key, $payload_json, $updated_utc)
                        ON CONFLICT(serial, kind, key) DO UPDATE SET
                            payload_json = excluded.payload_json,
                            updated_utc = excluded.updated_utc;
                        """;
                    cmd.Parameters.AddWithValue("$serial", serial);
                    cmd.Parameters.AddWithValue("$kind", kind);
                    // The key (a conversation address / call number+date / photo id) is the
                    // consented on-disk category — never logged (§2.8), same as payload_json.
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$payload_json", payloadJson);
                    cmd.Parameters.AddWithValue("$updated_utc", updatedUtc.UtcDateTime.ToString("o"));
                    cmd.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not upsert a sync_cache row; degrading to no store. (serial={serial}, kind={kind}, {ex.GetType().Name}: {ex.Message})");
                    return false;
                }
            }
        }
    }

    /// <summary>Rows for one (serial, kind), newest <paramref name="limit"/> first by
    /// <c>updated_utc</c> (default 200 — the same cap <see cref="PruneSyncCacheAsync"/> enforces).
    /// Empty if the store is unavailable or nothing is cached. Never throws.</summary>
    public IReadOnlyList<StoredSyncCacheRow> ListSyncCacheRows(string serial, string kind, int limit = 200)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return [];
            }
            using (connection)
            {
                try
                {
                    return ReadSyncCache(connection, serial, kind, limit);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not read sync_cache; degrading to no store. (serial={serial}, kind={kind}, {ex.GetType().Name}: {ex.Message})");
                    return [];
                }
            }
        }
    }

    /// <summary>Deletes every row for (serial, kind) past the newest <paramref name="keep"/> by
    /// <c>updated_utc</c> (default 200, §2.9 — this table has no user-facing retention control,
    /// so the cap is unconditional). Returns the number of rows removed, or 0 if the store is
    /// unavailable. Never throws.</summary>
    public async Task<int> PruneSyncCacheAsync(string serial, string kind, int keep = 200)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM sync_cache
                        WHERE serial = $serial AND kind = $kind
                        AND key NOT IN (
                            SELECT key FROM sync_cache
                            WHERE serial = $serial AND kind = $kind
                            ORDER BY updated_utc DESC
                            LIMIT $keep
                        );
                        """;
                    cmd.Parameters.AddWithValue("$serial", serial);
                    cmd.Parameters.AddWithValue("$kind", kind);
                    cmd.Parameters.AddWithValue("$keep", keep);
                    var removed = cmd.ExecuteNonQuery();
                    if (removed > 0)
                    {
                        _log?.Log(LogLevel.Info, $"LincStore: pruned {removed} sync_cache row(s) for serial={serial}, kind={kind} (kept newest {keep}).");
                    }
                    return removed;
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not prune sync_cache; degrading to no store. (serial={serial}, kind={kind}, {ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    /// <summary>The number of cached rows for one serial across every kind, or every row in the
    /// table when <paramref name="serial"/> is null (the launch-check report reads it this way).
    /// 0 if the store is unavailable. Never throws.</summary>
    public int CountSyncCache(string? serial = null)
    {
        lock (_lock)
        {
            if (!IsAvailable || !TryOpen(out var connection))
            {
                return 0;
            }
            using (connection)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    if (serial is { } s)
                    {
                        cmd.CommandText = "SELECT count(*) FROM sync_cache WHERE serial = $serial;";
                        cmd.Parameters.AddWithValue("$serial", s);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT count(*) FROM sync_cache;";
                    }
                    return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    IsAvailable = false;
                    _log?.Log(LogLevel.Warn, $"LincStore: could not count sync_cache; degrading to no store. (serial={(serial ?? "all")}, {ex.GetType().Name}: {ex.Message})");
                    return 0;
                }
            }
        }
    }

    // ---- internals ----

    /// <summary>
    /// Opens the connection and creates the directory the DB sits in. Never throws — a failure
    /// logs in plain language and <paramref name="connection"/> comes back null, which makes every
    /// caller degrade to "no store" (D-044 2.6). The file is created by SQLite itself on first
    /// real use; we only ensure the directory exists.
    /// </summary>
    private bool TryOpen([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SqliteConnection? connection)
    {
        connection = null;
        try
        {
            Directory.CreateDirectory(_root);
            connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable linc.db degrades — never a crash. The app runs exactly as
            // it does today (D-044 2.6).
            IsAvailable = false;
            _log?.Log(LogLevel.Error, $"LincStore: could not open the store; degrading to no store. ({ex.GetType().Name}: {ex.Message})");
            try { connection?.Dispose(); } catch { }
            connection = null;
            return false;
        }
    }

    /// <summary>
    /// The current <c>schema_version</c> row, or 0 if there is no such table or row yet (the
    /// pre-migration state). Never throws — an unreadable DB is the corrupt-DB case.
    /// </summary>
    private static int ReadVersion(SqliteConnection connection)
    {
        try
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_version';";
            if (Convert.ToInt32(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                return 0;
            }
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM schema_version LIMIT 1;";
            var raw = read.ExecuteScalar();
            return raw is null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Applies every migration step from <paramref name="from"/> up to <see cref="CurrentSchemaVersion"/> in order.</summary>
    private static void ApplyMigrations(SqliteConnection connection, int from)
    {
        // Each step is guarded so re-running EnsureSchemaAsync is a no-op (CREATE … IF NOT EXISTS).
        // Today only step 1 exists; future sessions add `else if (from < 2)` etc. The shape is
        // additive on purpose: there is deliberately no "drop and recreate" path.
        if (from < 1)
        {
            ApplyV1(connection);
        }
    }

    /// <summary>Schema step 1: the five tables and the two indexes, in one transaction.</summary>
    private static void ApplyV1(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                // A single row holding the schema version. Created empty here, set by WriteVersion.
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_version (
                        value INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS devices (
                        serial TEXT PRIMARY KEY,
                        model TEXT,
                        first_paired_utc TEXT
                    );
                    CREATE TABLE IF NOT EXISTS notifications (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        serial TEXT,
                        posted_utc TEXT,
                        app_package TEXT,
                        title TEXT,
                        body TEXT,
                        key TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_notifications_serial_posted
                        ON notifications (serial, posted_utc);
                    CREATE TABLE IF NOT EXISTS outbox (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        serial TEXT,
                        queued_utc TEXT,
                        kind TEXT,
                        payload_json TEXT,
                        attempts INTEGER DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_outbox_serial_id
                        ON outbox (serial, id);
                    CREATE TABLE IF NOT EXISTS sync_cache (
                        serial TEXT,
                        kind TEXT,
                        key TEXT,
                        payload_json TEXT,
                        updated_utc TEXT,
                        PRIMARY KEY (serial, kind, key)
                    );
                    """;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void WriteVersion(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        // The schema_version table holds exactly one row. Ensure it exists, then upsert it.
        cmd.CommandText = """
            INSERT INTO schema_version (value) SELECT $v
                WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            UPDATE schema_version SET value = $v;
            """;
        cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }

    private static int CountDevices(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM devices;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<StoredDevice> ReadDevices(SqliteConnection connection)
    {
        var list = new List<StoredDevice>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT serial, model, first_paired_utc FROM devices ORDER BY rowid;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var serial = reader.GetString(0);
            var model = reader.IsDBNull(1) ? null : reader.GetString(1);
            var pairedRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            var firstPaired = DateTimeOffset.TryParse(
                pairedRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            list.Add(new StoredDevice(serial, model, firstPaired));
        }
        return list;
    }

    /// <summary>
    /// Reads the newest <paramref name="limit"/> notification rows for one serial, newest first.
    /// The index <c>idx_notifications_serial_posted</c> makes this a cheap indexed lookup. The
    /// <see cref="StoredNotification.Body"/> field is read into the result type but is never
    /// passed to any logger from here or anywhere else in this file (§2.9).
    /// </summary>
    private static IReadOnlyList<StoredNotification> ReadNotifications(SqliteConnection connection, string serial, int limit)
    {
        var list = new List<StoredNotification>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, serial, posted_utc, app_package, title, body, key
            FROM notifications
            WHERE serial = $serial
            ORDER BY posted_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$serial", serial);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var rowSerial = reader.GetString(1);
            var postedRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            var posted = DateTimeOffset.TryParse(
                postedRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            var appPackage = reader.IsDBNull(3) ? null : reader.GetString(3);
            var title = reader.IsDBNull(4) ? null : reader.GetString(4);
            var body = reader.IsDBNull(5) ? null : reader.GetString(5);
            var key = reader.IsDBNull(6) ? null : reader.GetString(6);
            list.Add(new StoredNotification(id, rowSerial, posted, appPackage, title, body, key));
        }
        return list;
    }

    /// <summary>
    /// Reads the pending outbox rows for one serial in <c>id ASC</c> order. The index
    /// <c>idx_outbox_serial_id</c> makes this an indexed lookup. <see cref="StoredOutboxRow.PayloadJson"/>
    /// is read into the result type but is never passed to a logger from here or anywhere else
    /// in this file (§2.11).
    /// </summary>
    private static IReadOnlyList<StoredOutboxRow> ReadOutbox(SqliteConnection connection, string serial)
    {
        var list = new List<StoredOutboxRow>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, serial, queued_utc, kind, payload_json, attempts
            FROM outbox
            WHERE serial = $serial
            ORDER BY id ASC;
            """;
        cmd.Parameters.AddWithValue("$serial", serial);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var rowSerial = reader.GetString(1);
            var queuedRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            var queued = DateTimeOffset.TryParse(
                queuedRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            var kind = reader.IsDBNull(3) ? "" : reader.GetString(3);
            // Payload is the consented on-disk exception; never logged (§2.11).
            var payload = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var attempts = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            list.Add(new StoredOutboxRow(id, rowSerial, queued, kind, payload, attempts));
        }
        return list;
    }

    /// <summary>
    /// Reads the newest <paramref name="limit"/> sync_cache rows for one (serial, kind), newest
    /// <c>updated_utc</c> first. The table's own primary key on (serial, kind, key) makes the
    /// (serial, kind) filter an indexed lookup. <see cref="StoredSyncCacheRow.Key"/> and
    /// <see cref="StoredSyncCacheRow.PayloadJson"/> are read into the result type but never
    /// passed to a logger from here or anywhere else in this file (§2.8).
    /// </summary>
    private static IReadOnlyList<StoredSyncCacheRow> ReadSyncCache(SqliteConnection connection, string serial, string kind, int limit)
    {
        var list = new List<StoredSyncCacheRow>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT serial, kind, key, payload_json, updated_utc
            FROM sync_cache
            WHERE serial = $serial AND kind = $kind
            ORDER BY updated_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$serial", serial);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowSerial = reader.GetString(0);
            var rowKind = reader.GetString(1);
            // Key is the consented on-disk exception (e.g. an address or a number); never logged (§2.8).
            var key = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var payload = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var updatedRaw = reader.IsDBNull(4) ? null : reader.GetString(4);
            var updated = DateTimeOffset.TryParse(
                updatedRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            list.Add(new StoredSyncCacheRow(rowSerial, rowKind, key, payload, updated));
        }
        return list;
    }
}
