using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>A file the phone shared and the PC has now saved.</summary>
public sealed record ReceivedShare(string Name, string LocalPath, DateTimeOffset ReceivedAt);

public enum PhoneTransferState { Sending, Done, Failed, Cancelled }

/// <summary>
/// One file moving between the PC and the phone over Linc (1.0.0.11, D-083), either way since
/// 2026-11-01 (<see cref="ToPhone"/>). Progress is 0–100. <see cref="LocalPath"/> is the file sent,
/// or the folder a received one goes to.
/// </summary>
public sealed record PhoneTransfer(Guid Id, string Name, string LocalPath, PhoneTransferState State, double Progress, string? Message, bool ToPhone = true);

public interface IShareService
{
    void Start();

    /// <summary>Pushes a PC file into the phone's Share inbox and announces it (v13 `share.incoming`).</summary>
    Task SendFileToPhoneAsync(string localPath, CancellationToken ct);

    /// <summary>Send files to the phone over Linc, one after another, each tracked in <see cref="Transfers"/>.</summary>
    Task SendFilesToPhoneAsync(IReadOnlyList<string> paths);

    /// <summary>Files moving between the PC and the phone this session, either way, newest first.</summary>
    IReadOnlyList<PhoneTransfer> Transfers { get; }

    /// <summary>Stop a file that is moving (the Share page, the island, a notification, or the phone's Cancel).</summary>
    void Cancel(Guid id) { }

    event Action<PhoneTransfer>? TransferChanged;

    /// <summary>Files received from the phone this session, newest first.</summary>
    IReadOnlyList<ReceivedShare> Received { get; }

    /// <summary>Raised on the receiving thread when <see cref="Received"/> gains an item.</summary>
    event Action<ReceivedShare>? ShareReceived;

    /// <summary>Where received files land, so the UI can offer to open the folder.</summary>
    string ReceivedFolder { get; }
}

/// <summary>
/// Two-way file sharing behind the phone's Share tab (M19, D-028). Phone → PC arrives as
/// `share.item kind:file` and is pulled over the files channel into Downloads\Linc.
/// PC → phone pushes into the phone's inbox folder and announces it with `share.incoming`.
/// </summary>
public sealed class ShareService(
    IConnectionManager connection, IConnectionSupervisor supervisor, IFileService files, ILogService log,
    IDeviceRegistry registry)
    : IShareService
{
    private const string PhoneInbox = "/sdcard/Download/Linc";
    private const string PhoneOutbox = "/sdcard/Download/Linc/outbox";

    /// <summary>Pulls of one staged file before the PC stops trying and says why.</summary>
    internal const int MaxPullAttempts = 3;

    // What the outbox sweep has already done with each staged file, saved across restarts. Over
    // Direct TLS the PC can't delete a file it pulled (D-024), so without this every reconnect
    // pulled the same file again under a new name, and a file that failed to pull was retried on
    // every connect for ever, silently. Keyed by name, size and modified time, so the same file
    // shared again from the phone (a fresh copy, a new time) is a new delivery.
    private readonly object _ledgerLock = new();
    private Dictionary<string, LedgerEntry>? _ledger;

    private sealed class LedgerEntry
    {
        public bool Delivered { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset Seen { get; set; }
    }

    internal static string LedgerKey(RemoteEntry entry) =>
        $"{entry.Name}|{entry.Size}|{entry.Modified.ToUnixTimeSeconds()}";

    private string LedgerPath => Path.Combine(registry.RootPath, "share-ledger.json");

    private Dictionary<string, LedgerEntry> Ledger()
    {
        if (_ledger is not null)
        {
            return _ledger;
        }
        try
        {
            _ledger = File.Exists(LedgerPath)
                ? JsonSerializer.Deserialize<Dictionary<string, LedgerEntry>>(File.ReadAllText(LedgerPath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Share: couldn't read the list of received files, starting afresh ({ex.Message})");
            _ledger = [];
        }
        return _ledger;
    }

    private void SaveLedger()
    {
        try
        {
            var ledger = Ledger();
            // Keep it small: entries for files no longer on the phone stop mattering after a month.
            var cutoff = DateTimeOffset.Now.AddDays(-30);
            foreach (var key in ledger.Where(kv => kv.Value.Seen < cutoff).Select(kv => kv.Key).ToList())
            {
                ledger.Remove(key);
            }
            Directory.CreateDirectory(registry.RootPath);
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(ledger));
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Share: couldn't save the list of received files ({ex.Message})");
        }
    }

    private readonly List<ReceivedShare> _received = [];
    private readonly List<PhoneTransfer> _transfers = [];

    public IReadOnlyList<PhoneTransfer> Transfers
    {
        get { lock (_transfers) { return _transfers.ToList(); } }
    }

    public event Action<PhoneTransfer>? TransferChanged;

    private readonly Dictionary<Guid, CancellationTokenSource> _cancels = [];
    private readonly Dictionary<Guid, (int Percent, DateTime At, PhoneTransferState State)> _reported = [];

    public void Cancel(Guid id)
    {
        CancellationTokenSource? cts;
        lock (_cancels)
        {
            _cancels.TryGetValue(id, out cts);
        }
        if (cts is null)
        {
            return;
        }
        log.Log(LogLevel.Info, $"Share: cancelling {Transfers.FirstOrDefault(t => t.Id == id)?.Name ?? "a transfer"}.");
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished in the meantime: nothing left to stop.
        }
    }

    private CancellationTokenSource Track(Guid id, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_cancels)
        {
            _cancels[id] = cts;
        }
        return cts;
    }

    private void Untrack(Guid id)
    {
        lock (_cancels)
        {
            if (_cancels.Remove(id, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Tell the phone how a file is getting on (`share.progress`): the PC does the copying both
    /// ways, so this is the only way the phone can show progress and offer Cancel. Each state
    /// change, and while moving every 3 % or second at most.
    /// </summary>
    private void ReportToPhone(PhoneTransfer t)
    {
        var percent = (int)Math.Clamp(t.Progress, 0, 100);
        lock (_reported)
        {
            if (_reported.TryGetValue(t.Id, out var last) && last.State == t.State && t.State == PhoneTransferState.Sending &&
                (percent - last.Percent < 3 || DateTime.UtcNow - last.At < TimeSpan.FromMilliseconds(300)) && percent < 100)
            {
                return;
            }
            _reported[t.Id] = (percent, DateTime.UtcNow, t.State);
            if (t.State != PhoneTransferState.Sending)
            {
                _reported.Remove(t.Id);
            }
        }
        if (connection.Current is null)
        {
            return;
        }
        var payload = new JsonObject
        {
            ["id"] = t.Id.ToString("N"),
            ["name"] = t.Name,
            ["toPhone"] = t.ToPhone,
            ["percent"] = percent,
            ["state"] = t.State switch
            {
                PhoneTransferState.Done => "done",
                PhoneTransferState.Failed => "failed",
                PhoneTransferState.Cancelled => "cancelled",
                _ => "moving",
            },
        };
        if (t.State == PhoneTransferState.Failed && t.Message is { } reason)
        {
            payload["reason"] = reason;
        }
        _ = SendQuietlyAsync(Envelope.Create(MessageType.ShareProgress, payload));
    }

    private async Task SendQuietlyAsync(Envelope envelope)
    {
        try
        {
            await connection.SendToPhoneAsync(envelope, CancellationToken.None);
        }
        catch (Exception)
        {
            // The phone went away mid-transfer; the transfer itself reports that.
        }
    }

    private void Publish(PhoneTransfer transfer)
    {
        ReportToPhone(transfer);
        lock (_transfers)
        {
            var index = _transfers.FindIndex(t => t.Id == transfer.Id);
            if (index >= 0)
            {
                _transfers[index] = transfer;
            }
            else
            {
                _transfers.Insert(0, transfer);
                if (_transfers.Count > 50)
                {
                    _transfers.RemoveAt(_transfers.Count - 1);
                }
            }
        }
        TransferChanged?.Invoke(transfer);
    }

    public async Task SendFilesToPhoneAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await SendFileToPhoneAsync(path, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the owner: recorded as such; the rest still go.
            }
            catch (Exception ex)
            {
                // Already recorded as a failed transfer with a plain reason; keep going with the rest.
                log.Log(LogLevel.Warn, $"Couldn't send {Path.GetFileName(path)} to the phone: {ex.Message}");
            }
        }
    }
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private bool _saidNoOutbox;

    public IReadOnlyList<ReceivedShare> Received
    {
        get { lock (_received) { return _received.ToList(); } }
    }

    public event Action<ReceivedShare>? ShareReceived;

    /// <summary>Beside the Photos lane's folder, so shared media sits with synced media.</summary>
    public string ReceivedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc", "Shared");

    public void Start()
    {
        connection.CompanionMessageReceived += OnCompanionMessage;
        // The announcement is only a nudge. The phone stages every shared file in its outbox,
        // so sweeping that folder on connect delivers anything whose `share.item` was missed —
        // shared while disconnected, sent during a reconnect, or lost to a failed pull.
        supervisor.StateChanged += () =>
        {
            if (supervisor.State == LinkState.Connected)
            {
                _ = DrainOutboxAsync();
            }
        };
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (envelope.Type == MessageType.ShareCancel)
        {
            if (Guid.TryParse((string?)envelope.Payload["id"], out var id))
            {
                Cancel(id);
            }
            return;
        }
        if (envelope.Type != MessageType.ShareItem || (string?)envelope.Payload["kind"] != "file")
        {
            return; // text/url share is handled by the Home view model
        }
        _ = DrainOutboxAsync();
    }

    /// <summary>
    /// Pulls everything staged in the phone's outbox, then removes each one it saved so the
    /// same file is never delivered twice.
    /// </summary>
    private async Task DrainOutboxAsync()
    {
        if (!await _drainGate.WaitAsync(0))
        {
            return; // already draining; the sweep will see anything newly staged
        }
        try
        {
            IReadOnlyList<RemoteEntry> staged;
            try
            {
                staged = await files.ListAsync(PhoneOutbox, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // No outbox yet is the normal case on a phone that has never shared — said once a
                // session, not on every connect (it was logged on every single one).
                if (!_saidNoOutbox)
                {
                    _saidNoOutbox = true;
                    log.Log(LogLevel.Info, $"No shared files waiting on the phone ({ex.Message})");
                }
                return;
            }

            Directory.CreateDirectory(ReceivedFolder);
            var waiting = staged.Where(e => !e.IsDirectory).ToList();
            int skippedDone = 0, skippedGivenUp = 0;
            foreach (var entry in waiting)
            {
                var remotePath = $"{PhoneOutbox}/{entry.Name}";
                LedgerEntry record;
                lock (_ledgerLock)
                {
                    var ledger = Ledger();
                    var key = LedgerKey(entry);
                    if (!ledger.TryGetValue(key, out var known))
                    {
                        ledger[key] = known = new LedgerEntry();
                    }
                    record = known;
                    record.Seen = DateTimeOffset.Now;
                }
                if (record.Delivered)
                {
                    // Already saved on this PC; only the (TLS-impossible) delete is left to try.
                    skippedDone++;
                    await TryDeleteStagedAsync(remotePath);
                    continue;
                }
                if (record.Attempts >= MaxPullAttempts)
                {
                    skippedGivenUp++;
                    continue;
                }
                var incoming = new PhoneTransfer(Guid.NewGuid(), entry.Name, ReceivedFolder, PhoneTransferState.Sending, 0, null, ToPhone: false);
                Publish(incoming);
                var notifier = ShareNotifier.Start(entry.Name, toPhone: false, incoming.Id);
                using var cts = Track(incoming.Id, CancellationToken.None);
                var lastReported = -10.0;
                var pullProgress = new Progress<double>(p =>
                {
                    notifier.Progress(p);
                    if (p - lastReported >= 2 || p >= 100)
                    {
                        lastReported = p;
                        Publish(incoming = incoming with { Progress = Math.Clamp(p, 0, 100) });
                    }
                });
                try
                {
                    var local = await files.PullAsync(remotePath, ReceivedFolder, pullProgress, cts.Token);
                    var share = new ReceivedShare(Path.GetFileName(local), local, DateTimeOffset.Now);
                    lock (_ledgerLock)
                    {
                        record.Delivered = true;
                        record.LastError = null;
                        SaveLedger();
                    }
                    lock (_received) { _received.Insert(0, share); }
                    log.Log(LogLevel.Info, $"Received {share.Name} from the phone");
                    ShareReceived?.Invoke(share);
                    Publish(incoming with { Name = share.Name, LocalPath = local, State = PhoneTransferState.Done, Progress = 100, Message = $"Saved in {ReceivedFolder}" });
                    notifier.Finish("Received from your phone", $"Saved in {ReceivedFolder}");
                    await TryDeleteStagedAsync(remotePath);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled by the owner (here or on the phone): not fetched again until it is
                    // shared again; the phone drops it from its outbox when it hears "cancelled".
                    lock (_ledgerLock)
                    {
                        record.Attempts = MaxPullAttempts;
                        record.LastError = "Cancelled";
                        SaveLedger();
                    }
                    log.Log(LogLevel.Info, $"Stopped receiving {entry.Name} from the phone (cancelled).");
                    Publish(incoming with { State = PhoneTransferState.Cancelled, Message = "Cancelled" });
                    notifier.Finish("Stopped receiving from your phone");
                }
                catch (Exception ex)
                {
                    // Never be silent about a share, and never retry it for ever: a file that
                    // fails MaxPullAttempts times is left alone until it is shared again.
                    int attempts;
                    lock (_ledgerLock)
                    {
                        attempts = ++record.Attempts;
                        record.LastError = ex.Message;
                        SaveLedger();
                    }
                    Publish(incoming with { State = PhoneTransferState.Failed, Message = ex.Message });
                    if (attempts >= MaxPullAttempts)
                    {
                        log.Log(LogLevel.Warn, $"Gave up receiving {entry.Name} from the phone after {attempts} tries: {ex.Message}. Share it again from the phone to retry.");
                        notifier.Finish("Couldn't receive from your phone", $"{ex.Message} Linc stopped retrying; share it again from your phone.");
                    }
                    else
                    {
                        log.Log(LogLevel.Warn, $"Couldn't fetch {entry.Name} from the phone (try {attempts} of {MaxPullAttempts}): {ex.Message}");
                        notifier.Finish("Couldn't receive from your phone", $"{ex.Message} Linc tries again on the next connect ({attempts} of {MaxPullAttempts}).");
                    }
                }
                finally
                {
                    Untrack(incoming.Id);
                }
            }
            if (skippedDone > 0 || skippedGivenUp > 0)
            {
                log.Log(LogLevel.Info, $"Share sweep: {waiting.Count} file(s) in the phone's outbox; {skippedDone} already received, {skippedGivenUp} given up on (not fetched again).");
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    /// <summary>Removes a pulled file from the phone's outbox. Direct TLS has no delete (D-024);
    /// there the ledger is what keeps the file from being delivered again.</summary>
    private async Task TryDeleteStagedAsync(string remotePath)
    {
        try
        {
            await files.DeleteAsync(remotePath, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    public async Task SendFileToPhoneAsync(string localPath, CancellationToken ct)
    {
        var transfer = new PhoneTransfer(Guid.NewGuid(), Path.GetFileName(localPath), localPath, PhoneTransferState.Sending, 0, null);
        Publish(transfer);
        var notifier = ShareNotifier.Start(transfer.Name, toPhone: true, transfer.Id);
        using var cts = Track(transfer.Id, ct);
        var outer = ct;
        ct = cts.Token;
        try
        {
            if (supervisor.State != LinkState.Connected)
            {
                throw new LincException("Your phone isn't connected.");
            }
            var lastReported = -10.0;
            var progress = new Progress<double>(p =>
            {
                if (p - lastReported >= 2 || p >= 100)
                {
                    lastReported = p;
                    Publish(transfer = transfer with { Progress = Math.Clamp(p, 0, 100) });
                    notifier.Progress(p);
                }
            });
            // PushAsync renames on conflict and returns the path actually written, so the
            // announcement always names the file the phone really has.
            var remote = await files.PushAsync(localPath, PhoneInbox, progress, ct);
            var name = remote[(remote.LastIndexOf('/') + 1)..];
            await connection.SendToPhoneAsync(
                Envelope.Create(MessageType.ShareIncoming, new JsonObject
                {
                    ["name"] = name,
                    ["path"] = remote,
                }),
                ct);
            Publish(transfer with { State = PhoneTransferState.Done, Progress = 100, Message = "Sent to your phone (Download/Linc)" });
            notifier.Finish("Sent to your phone", "In Download/Linc on the phone");
            log.Log(LogLevel.Info, $"Sent {name} to the phone");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is LincException ? ex.Message : "Couldn't send it — check the phone is connected and has storage space.";
            Publish(transfer with { State = PhoneTransferState.Failed, Message = reason });
            notifier.Finish("Couldn't send to your phone", reason);
            throw;
        }
        catch (OperationCanceledException)
        {
            log.Log(LogLevel.Info, $"Stopped sending {transfer.Name} to the phone (cancelled).");
            Publish(transfer with { State = PhoneTransferState.Cancelled, Message = "Cancelled" });
            notifier.Finish("Stopped sending to your phone");
            if (outer.IsCancellationRequested)
            {
                throw;
            }
            throw new OperationCanceledException("Cancelled by you");
        }
        finally
        {
            Untrack(transfer.Id);
        }
    }
}
