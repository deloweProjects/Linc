using System.IO;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>A file the phone shared and the PC has now saved.</summary>
public sealed record ReceivedShare(string Name, string LocalPath, DateTimeOffset ReceivedAt);

public enum PhoneTransferState { Sending, Done, Failed }

/// <summary>One file going to the phone over Linc (1.0.0.11, D-083). Progress is 0–100.</summary>
public sealed record PhoneTransfer(Guid Id, string Name, string LocalPath, PhoneTransferState State, double Progress, string? Message);

public interface IShareService
{
    void Start();

    /// <summary>Pushes a PC file into the phone's Share inbox and announces it (v13 `share.incoming`).</summary>
    Task SendFileToPhoneAsync(string localPath, CancellationToken ct);

    /// <summary>Send files to the phone over Linc, one after another, each tracked in <see cref="Transfers"/>.</summary>
    Task SendFilesToPhoneAsync(IReadOnlyList<string> paths);

    /// <summary>Files going to the phone this session, newest first.</summary>
    IReadOnlyList<PhoneTransfer> Transfers { get; }

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
    IConnectionManager connection, IConnectionSupervisor supervisor, IFileService files, ILogService log)
    : IShareService
{
    private const string PhoneInbox = "/sdcard/Download/Linc";
    private const string PhoneOutbox = "/sdcard/Download/Linc/outbox";

    private readonly List<ReceivedShare> _received = [];
    private readonly List<PhoneTransfer> _transfers = [];

    public IReadOnlyList<PhoneTransfer> Transfers
    {
        get { lock (_transfers) { return _transfers.ToList(); } }
    }

    public event Action<PhoneTransfer>? TransferChanged;

    private void Publish(PhoneTransfer transfer)
    {
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
            foreach (var entry in staged.Where(e => !e.IsDirectory))
            {
                var remotePath = $"{PhoneOutbox}/{entry.Name}";
                try
                {
                    var local = await files.PullAsync(
                        remotePath, ReceivedFolder, new Progress<double>(), CancellationToken.None);
                    var share = new ReceivedShare(Path.GetFileName(local), local, DateTimeOffset.Now);
                    lock (_received) { _received.Insert(0, share); }
                    log.Log(LogLevel.Info, $"Received {share.Name} from the phone");
                    ShareReceived?.Invoke(share);
                    Toast("File received from your phone", share.Name);

                    try
                    {
                        await files.DeleteAsync(remotePath, CancellationToken.None);
                    }
                    catch (LincException)
                    {
                        // Direct TLS has no delete (D-024). Harmless: the file is already saved,
                        // and the next sweep pulls it again under a conflict-safe name.
                    }
                }
                catch (Exception ex)
                {
                    // Previously this whole method caught only LincException, so anything else
                    // vanished into an unobserved task and the user saw "sent" with no file
                    // and nothing in the log. Never be silent about a share again.
                    log.Log(LogLevel.Warn, $"Couldn't fetch {entry.Name} from the phone: {ex.Message}");
                }
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    public async Task SendFileToPhoneAsync(string localPath, CancellationToken ct)
    {
        var transfer = new PhoneTransfer(Guid.NewGuid(), Path.GetFileName(localPath), localPath, PhoneTransferState.Sending, 0, null);
        Publish(transfer);
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
            log.Log(LogLevel.Info, $"Sent {name} to the phone");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(transfer with
            {
                State = PhoneTransferState.Failed,
                Message = ex is LincException ? ex.Message : "Couldn't send it — check the phone is connected and has storage space.",
            });
            throw;
        }
    }

    private static void Toast(string title, string body)
    {
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(
                new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddText(title).AddText(body).BuildNotification());
        }
        catch (Exception)
        {
            // Toasts unavailable; the log entry still records it.
        }
    }
}
