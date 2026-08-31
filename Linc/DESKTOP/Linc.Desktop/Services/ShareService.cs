using System.IO;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>A file the phone shared and the PC has now saved.</summary>
public sealed record ReceivedShare(string Name, string LocalPath, DateTimeOffset ReceivedAt);

public interface IShareService
{
    void Start();

    /// <summary>Pushes a PC file into the phone's Share inbox and announces it (v13 `share.incoming`).</summary>
    Task SendFileToPhoneAsync(string localPath, CancellationToken ct);

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
    private readonly SemaphoreSlim _drainGate = new(1, 1);

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
                // No outbox yet is the normal case on a phone that has never shared.
                log.Log(LogLevel.Info, $"No shared files waiting on the phone ({ex.Message})");
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
        // PushAsync renames on conflict and returns the path actually written, so the
        // announcement always names the file the phone really has.
        var remote = await files.PushAsync(localPath, PhoneInbox, new Progress<double>(), ct);
        var name = remote[(remote.LastIndexOf('/') + 1)..];
        await connection.SendToPhoneAsync(
            Envelope.Create(MessageType.ShareIncoming, new JsonObject
            {
                ["name"] = name,
                ["path"] = remote,
            }),
            ct);
        log.Log(LogLevel.Info, $"Sent {name} to the phone");
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
