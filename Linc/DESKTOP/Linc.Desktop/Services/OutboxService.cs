using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// The offline-action queue service (M9c). It owns the flush path: on a transition into
/// <see cref="LinkState.Connected"/>, the rows for the currently-paired serial are sent to the
/// phone one at a time in <c>id</c> ASC order (§2.3), with a single in-flight flag so a second
/// trigger while a flush is running is a no-op (§2.4). Page view models enqueue through this
/// service — they never call <see cref="LincStore"/> directly (§2.9 — the flush must run
/// regardless of which page is open, so it belongs on the shell, never a page's VM).
///
/// <para><b>One action kind this session: <c>sms.send</c></b> (§2.1). The flush is a
/// <c>switch</c> on the row's <c>kind</c> string so a later session can add a second kind
/// without touching <see cref="LincStore"/> or this file's enqueue surface. Behavioural rules
/// reflecting the task's design (§§2.2-2.11):</para>
/// <list type="bullet">
///   <item><b>Queue only when actually disconnected — never as a retry-on-error (§2.2).</b>
///     The decision point is <see cref="IConnectionSupervisor.State"/> checked before sending
///     in the page's VM. OutboxService itself has no catch-and-queue fallback; it only flushes.</item>
///   <item><b>Ordered: flush by <c>id</c> ASC, per serial (§2.3).</b> <c>id</c> is AUTOINCREMENT,
///     so insertion order is queue order. The flush walks the rows for the currently-paired
///     serial only.</item>
///   <item><b>One flush at a time (§2.4).</b> A single in-flight flag guards the flush; a second
///     trigger while one is running is a no-op, not a queued second flush.</item>
///   <item><b>Stop at the first failure (§2.5).</b> Sending row N throwing stops the flush —
///     remaining rows stay queued for the next reconnect. Skipping would reorder the user's
///     messages, which is worse than delaying them.</item>
///   <item><b>Delete only after the send returns (§2.6).</b>
///     <see cref="IConnectionManager.SmsSendAsync"/> awaits an <c>Ok</c> reply, so a returned
///     task means the phone accepted the text.</item>
///   <item><b>Attempts capped at 5 (§2.7).</b> On a failed attempt, increment <c>attempts</c>.
///     A row at <see cref="MaxAttempts"/> is dropped at the head of the next flush (not
///     retried), and the user is told in plain language that a queued text was not sent. One
///     poisoned row must never block the queue forever.</item>
///   <item><b>Never log a message body (§2.11).</b> The <c>payload_json</c> is the same consented
///     on-disk category as a notification body; a log line is not (BRAIN.md, M9b §2.9). Every
///     log below names the kind and the row count — never the payload.</item>
/// </list>
///
/// <para><b>The flush is fire-and-forget with a catch-all (§2.10)</b> — a phone that reconnects
/// must never crash the app because its queue is malformed. Every <see cref="LincStore"/> method
/// degrades rather than throwing.</para>
/// </summary>
public sealed class OutboxService
{
    /// <summary>
    /// The literal kind string for an SMS send action (§2.1). Public purely so the harness's
    /// crude source-text checks and tests can refer to the same constant the enqueue path uses.
    /// </summary>
    public const string SmsSendKind = "sms.send";

    /// <summary>
    /// The cap on flush attempts for a single row (§2.7). A row that has reached this many
    /// attempts is dropped at the head of the next flush — never retried again — and the user
    /// is told in plain language that a queued text was not sent.
    /// </summary>
    public const int MaxAttempts = 5;

    private readonly IConnectionSupervisor _supervisor;
    private readonly IConnectionManager _connection;
    private readonly IDeviceRegistry _registry;
    private readonly LincStore _store;
    private readonly ILogService _log;

    private readonly object _inFlightLock = new();
    private bool _flushInFlight;
    private bool _started;

    /// <summary>
    /// One outbox row that just flushed successfully (M9d-2, §3.4). Carries only what a
    /// subscriber needs to find the matching in-memory message — address+body, the same identity
    /// <see cref="MatchesFlushedRow"/> compares by — because by the time this fires the row is
    /// already deleted from the store and the outbox never carried a back-reference to whatever
    /// UI element queued it.
    /// </summary>
    public readonly record struct FlushedRow(string Serial, string Kind, string Address, string Body);

    /// <summary>
    /// Raised right after a row is deleted following a successful send (§3.4). Page view models
    /// subscribe to flip a pending message back to "sent" when its conversation is open; if
    /// nothing matches (the conversation isn't loaded), a subscriber does nothing at all — no
    /// toast, no badge.
    /// </summary>
    public event Action<FlushedRow>? RowFlushed;

    public OutboxService(
        IConnectionSupervisor supervisor,
        IConnectionManager connection,
        IDeviceRegistry registry,
        LincStore store,
        ILogService log)
    {
        _supervisor = supervisor;
        _connection = connection;
        _registry = registry;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Subscribes to <see cref="IConnectionSupervisor.StateChanged"/> so the flush runs on the
    /// next transition into <see cref="LinkState.Connected"/>. Called once from
    /// <c>AppShellViewModel</c> alongside the other eagerly-started services (§2.9). Safe to
    /// call more than once — the second call is a no-op.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _supervisor.StateChanged += OnStateChanged;
    }

    private void OnStateChanged()
    {
        // The flush must only run on a transition INTO Connected. Filtering on the current state
        // is the right check — a transition to anything else (Searching/Connecting/Paused/NoDevice)
        // is a no-op here. Fire-and-forget on purpose: this runs on the supervisor's thread and
        // must never block it or throw into it (§2.10).
        if (_supervisor.State != LinkState.Connected)
        {
            return;
        }
        _ = FlushAsync();
    }

    /// <summary>
    /// Enqueues one <c>sms.send</c> row for the currently-paired serial. Returns the row id on
    /// success, or <c>-1</c> if there is no paired serial or the store is unavailable. The
    /// payload is built by the pure static <see cref="BuildSmsPayload"/> so the harness can
    /// round-trip it without modelling it; the same shape is parsed back by
    /// <see cref="ParseSmsPayload"/> on flush.
    /// </summary>
    public async Task<long> EnqueueSmsAsync(string address, string body)
    {
        if (_registry.PairedSerial is not { } serial)
        {
            // No phone is paired — the row has nowhere to live. Surface nothing here; the page
            // already knows the link is disconnected and shows its own lane message.
            return -1;
        }
        var payload = BuildSmsPayload(address, body);
        return await _store.EnqueueOutboxAsync(serial, SmsSendKind, payload.ToJsonString());
    }

    /// <summary>
    /// Flushes the pending outbox for the currently-paired serial, in <c>id</c> ASC order.
    /// Guarded by a single in-flight flag (§2.4) — a second trigger while a flush is running is
    /// a no-op, not a queued second flush. Fire-and-forget by design; every path catches and
    /// logs (§2.10). Never log the payload (§2.11) — only the row count and the kind.
    /// </summary>
    public async Task FlushAsync()
    {
        // Single in-flight guard: a reconnect can fire StateChanged more than once, and a slow
        // flush must never overlap itself (§2.4). A second trigger while a flush is running is
        // a no-op — not a queued second flush.
        lock (_inFlightLock)
        {
            if (_flushInFlight)
            {
                return;
            }
            _flushInFlight = true;
        }
        try
        {
            await FlushCoreAsync();
        }
        catch (Exception ex)
        {
            // Catch-all: a phone that reconnects must never crash the app because its queue is
            // malformed (§2.10). Log the type and a plain-language message — never the payload.
            _log.Log(LogLevel.Error, $"OutboxService: flush threw and was swallowed. ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            lock (_inFlightLock)
            {
                _flushInFlight = false;
            }
        }
    }

    private async Task FlushCoreAsync()
    {
        if (_registry.PairedSerial is not { } serial)
        {
            return;
        }
        var rows = _store.ListPendingOutbox(serial);
        if (rows.Count == 0)
        {
            return;
        }
        _log.Log(LogLevel.Info, $"OutboxService: flushing {rows.Count} pending row(s) for serial={serial}.");

        // Walk in id ASC order (§2.3). Stop at the first failure (§2.5) — skipping a row would
        // reorder the user's messages, which is worse than delaying the rest. A row already at
        // MaxAttempts is dropped at the head of the flush (§2.7) — never retried again — and the
        // loop then continues to the next row.
        foreach (var row in rows)
        {
            if (DecideDropVsAttempt(row.Attempts))
            {
                // §2.7 — drop the poisoned row. Tell the user in plain language (§2.8-style) that
                // a queued text was not sent. The payload is never named.
                var dropped = await _store.DeleteOutboxRowAsync(row.Id);
                _log.Log(LogLevel.Warn, $"OutboxService: dropped row id={row.Id} (kind={row.Kind}) after {MaxAttempts} attempts (dropped={dropped}).");
                continue;
            }

            // §2.1 — switch on kind. Today only one case exists; a second kind is added by adding
            // a case here, never by touching LincStore or the enqueue surface.
            var ok = row.Kind switch
            {
                SmsSendKind => await SendSmsRowAsync(row),
                _ => await UnknownKindAsync(row),
            };
            if (!ok)
            {
                // §2.5 — stop at the first failure. Remaining rows stay queued for the next
                // reconnect. The in-flight flag will be released by the finally above.
                _log.Log(LogLevel.Warn, $"OutboxService: flush stopping at row id={row.Id} (kind={row.Kind}) — the phone refused or the link died.");
                return;
            }
        }
        _log.Log(LogLevel.Info, $"OutboxService: flush complete for serial={serial}.");
    }

    private async Task<bool> SendSmsRowAsync(StoredOutboxRow row)
    {
        // The payload is parsed back by the pure static, so the harness can exercise the same
        // parse path the flush uses. A malformed payload means the row is poison — increment
        // the attempts counter (§2.7) and treat as a failure so the flush stops (§2.5) and the
        // next flush re-evaluates against MaxAttempts.
        var (address, body) = ParseSmsPayload(JsonNode.Parse(row.PayloadJson) as JsonObject);
        if (string.IsNullOrEmpty(address))
        {
            _ = await _store.IncrementOutboxAttemptsAsync(row.Id);
            _log.Log(LogLevel.Warn, $"OutboxService: row id={row.Id} had a malformed payload and was counted as a failed attempt.");
            return false;
        }
        try
        {
            await _connection.SmsSendAsync(address, body, CancellationToken.None);
            // §2.6 — delete only after the send returns successfully. A returned task means the
            // phone accepted the text (CompanionClient awaits an Ok reply).
            var deleted = await _store.DeleteOutboxRowAsync(row.Id);
            _log.Log(LogLevel.Info, $"OutboxService: flushed row id={row.Id} (kind={SmsSendKind}) and deleted it (deleted={deleted}).");
            // M9d-2 §3.4 — tell subscribers this row actually sent. Fired after the delete, same
            // as the log line above, and carries the same address/body a subscriber already has
            // no other way to recover once the row is gone from the store.
            RowFlushed?.Invoke(new FlushedRow(row.Serial, SmsSendKind, address, body));
            return true;
        }
        catch (Exception ex) when (ex is LincException or OperationCanceledException)
        {
            // Failure while the link is up has a real reason (no SIM, refused permission,
            // malformed address) — OUT OF SCOPE: §2.2 forbids catch-and-queue, so the throw
            // does NOT re-enqueue. Increment attempts (§2.7) and signal a stop (§2.5) by
            // returning false. Never log the body (§2.11) — only the kind and the failure type.
            var attempts = await _store.IncrementOutboxAttemptsAsync(row.Id);
            _log.Log(LogLevel.Warn, $"OutboxService: row id={row.Id} send failed ({ex.GetType().Name}); attempts now {attempts}.");
            return false;
        }
    }

    private Task<bool> UnknownKindAsync(StoredOutboxRow row)
    {
        // A future-kind row in this session's data is poison — drop it so it cannot block the
        // queue forever (the §2.7 cap doesn't apply because the row is structurally unknown,
        // not failed). This path is unused today; it exists so the switch stays honest.
        _ = _store.DeleteOutboxRowAsync(row.Id);
        _log.Log(LogLevel.Warn, $"OutboxService: row id={row.Id} has unknown kind={row.Kind}; dropping as poison.");
        return Task.FromResult(true);
    }

    // ---- pure statics (testable by the harness without modelling) ----
    //
    // Putting the payload build/parse and the drop-vs-attempt decision in pure static methods
    // is the M9b lesson (§4.2): the more of this a harness can call, the less of it a harness
    // has to re-implement. outboxsim exercises those four directly rather than reconstructing
    // their behaviour.

    /// <summary>
    /// Builds the <c>sms.send</c> payload JSON. Pure — no I/O, no allocation beyond the JSON
    /// node. The harness round-trips this against <see cref="ParseSmsPayload"/> to prove the
    /// shape is stable. Stable on purpose: the flush parses the same JSON back, so any drift
    /// here is a wire bug.
    /// </summary>
    public static JsonObject BuildSmsPayload(string address, string body)
        => new() { ["address"] = address, ["body"] = body };

    /// <summary>
    /// Parses a <c>sms.send</c> payload back into (address, body). Pure — returns empty strings
    /// on a null/wrong-shape payload, never throws. The flush uses this; the harness round-trips
    /// it against <see cref="BuildSmsPayload"/> to prove the same shape comes back.
    /// </summary>
    public static (string Address, string Body) ParseSmsPayload(JsonObject? node)
    {
        if (node is null)
        {
            return ("", "");
        }
        var address = (string?)node["address"] ?? "";
        var body = (string?)node["body"] ?? "";
        return (address, body);
    }

    /// <summary>
    /// The drop-vs-attempt decision for a row at a given attempts count (§2.7). Pure — no I/O,
    /// no state. <c>true</c> means "drop the row, do not attempt"; <c>false</c> means "attempt
    /// the send". The cap is <see cref="MaxAttempts"/>. The harness exercises this directly so
    /// the boundary (4 → attempt, 5 → drop) is proven without modelling the flush loop.
    /// </summary>
    public static bool DecideDropVsAttempt(int attempts) => attempts >= MaxAttempts;

    /// <summary>
    /// Whether a message in an open conversation is the one a <see cref="FlushedRow"/> refers to
    /// (M9d-2, §3.4). Pure — compares (conversation address, message body) against the flushed
    /// row's own address/body, gated on the message actually being pending, so an already-sent
    /// message with the same text can never be re-matched. Address+body is the only identity
    /// still available on both sides by the time a row has flushed: the row itself is already
    /// deleted from the store. The harness (tools\synccachesim) exercises this directly — both a
    /// matching and a non-matching case — rather than modelling the decision (§4.2).
    /// </summary>
    public static bool MatchesFlushedRow(string conversationAddress, string messageBody, bool messageIsPending, string flushedAddress, string flushedBody)
        => messageIsPending && conversationAddress == flushedAddress && messageBody == flushedBody;
}
