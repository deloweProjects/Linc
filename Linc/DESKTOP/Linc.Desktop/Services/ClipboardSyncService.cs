using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Linc.Desktop.Services;

/// <summary>One clipboard-history item. <see cref="Id"/> is the store row; 0 for an unsaved item.</summary>
public sealed record ClipEntry(long Id, string Text, bool FromPhone, DateTimeOffset At, bool Pinned = false, bool Sent = false);

public interface IClipboardSyncService
{
    bool Enabled { get; set; }

    void Start();

    /// <summary>The active phone's history, pinned first, then newest first.</summary>
    IReadOnlyList<ClipEntry> History { get; }

    event Action? HistoryChanged;

    /// <summary>Raised on the UI thread when a copy from the phone landed on this PC's clipboard.</summary>
    event Action<string>? ReceivedFromPhone;

    /// <summary>Put an item back on this PC's clipboard ("Copy here").</summary>
    void Recopy(ClipEntry entry);

    /// <summary>Put text on the phone's clipboard — like copying it there — and keep it as sent.</summary>
    Task<bool> SendToPhoneAsync(string text);

    /// <summary>Push this PC's clipboard to the phone and ask the phone for its own (v24).</summary>
    Task<bool> SyncNowAsync();

    void SetPinned(ClipEntry entry, bool pinned);

    void Delete(ClipEntry entry);

    /// <summary>Delete the history (pinned items stay).</summary>
    void Clear();

    /// <summary>The history settings changed (keep/size): reload and trim.</summary>
    void Reload();
}

/// <summary>
/// Clipboard sync and the clipboard history (PROTOCOL.md v1; history, Send and Sync now in
/// 1.0.0.11, D-083).
///
/// <para><b>History.</b> Up to 500 items per phone by default (100 / 500 / 2000 in Settings), in
/// LincStore's <c>clipboard</c> table keyed by the phone's <see cref="ConnectedDevice.CacheKey"/>,
/// so it survives restarts and two phones never share one. "Keep history" off means sync only.</para>
///
/// <para><b>Privacy.</b> A Windows clip that asks to stay out of clipboard monitors or history —
/// password managers set <c>ExcludeClipboardContentFromMonitorProcessing</c> or
/// <c>CanIncludeInClipboardHistory = 0</c> — is never sent to the phone and never saved. Clipboard
/// text is never logged.</para>
/// </summary>
public sealed class ClipboardSyncService(
    IConnectionManager connection,
    IConnectionSupervisor supervisor,
    IDeviceRegistry registry,
    LincStore store,
    ILogService log,
    OutboxService outbox) : IClipboardSyncService
{
    private const int GetVersion = 24;

    private DispatcherQueue? _dispatcher;
    private string? _lastReceived;
    private string? _lastSent;
    // The newest copy made on this PC and when, so a phone's replayed copy (sent again after a
    // drop) can be weighed against it: the newer one wins on both sides.
    private string? _localCopy;
    private DateTimeOffset _localCopyAtUtc;
    private IReadOnlyList<ClipEntry> _history = [];

    public bool Enabled
    {
        get => registry.ClipboardSyncEnabled;
        set => registry.SaveClipboardSyncEnabled(value);
    }

    public IReadOnlyList<ClipEntry> History => _history;

    public event Action? HistoryChanged;
    public event Action<string>? ReceivedFromPhone;

    /// <summary>Whose history: the connected phone, else the active tab's phone.</summary>
    private string? Key => connection.Current?.CacheKey ?? registry.PairedSerial;

    public void Start()
    {
        if (_dispatcher is not null)
        {
            return;
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Clipboard.ContentChanged += (_, _) => _ = OnDesktopClipboardChangedAsync(force: false);
        connection.CompanionMessageReceived += envelope =>
            _dispatcher.TryEnqueue(() => OnCompanionMessage(envelope));
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(Reload);
        registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(Reload);
        _ = Task.Run(async () =>
        {
            await store.EnsureSchemaAsync();
            _dispatcher.TryEnqueue(Reload);
        });
    }

    public void Reload()
    {
        var key = Key;
        if (!registry.ClipboardHistoryEnabled || key is null)
        {
            _history = [];
        }
        else
        {
            store.PruneClips(key, registry.ClipboardHistorySize);
            _history = store.ListClips(key, registry.ClipboardHistorySize)
                .Select(c => new ClipEntry(c.Id, c.Text, c.FromPhone, c.AtUtc.ToLocalTime(), c.Pinned, c.Sent))
                .ToList();
        }
        HistoryChanged?.Invoke();
    }

    private void AddHistory(string text, bool fromPhone, bool sent = false)
    {
        if (!registry.ClipboardHistoryEnabled || Key is not { } key || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        store.AddClip(key, text, fromPhone, sent, DateTimeOffset.UtcNow);
        Reload();
    }

    public void Recopy(ClipEntry entry)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(entry.Text);
            _lastReceived = entry.Text; // it came from the history, not a new copy: don't echo
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Info, $"Couldn't put that on the clipboard — another app has it open ({ex.GetType().Name}).");
        }
    }

    public async Task<bool> SendToPhoneAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || supervisor.State != LinkState.Connected)
        {
            return false;
        }
        try
        {
            _lastSent = text;
            await connection.SendClipboardAsync(text, CancellationToken.None);
            log.Log(LogLevel.Info, "Sent text to the phone's clipboard");
            AddHistory(text, fromPhone: false, sent: true);
            return true;
        }
        catch (LincException)
        {
            return false; // the link is dying; the supervisor notices and reconnects
        }
    }

    public async Task<bool> SyncNowAsync()
    {
        if (supervisor.State != LinkState.Connected)
        {
            return false;
        }
        await OnDesktopClipboardChangedAsync(force: true);
        if (connection.Current is { Companion.V: >= GetVersion })
        {
            try
            {
                await connection.SendToPhoneAsync(Envelope.Create(MessageType.ClipboardGet, new JsonObject()), CancellationToken.None);
            }
            catch (LincException)
            {
                // Link dying; nothing to ask.
            }
        }
        return true;
    }

    public void SetPinned(ClipEntry entry, bool pinned)
    {
        if (store.SetClipPinned(entry.Id, pinned))
        {
            Reload();
        }
    }

    public void Delete(ClipEntry entry)
    {
        if (store.DeleteClip(entry.Id))
        {
            Reload();
        }
    }

    public void Clear()
    {
        if (Key is { } key && store.ClearClips(key))
        {
            Reload();
        }
    }

    private async Task OnDesktopClipboardChangedAsync(bool force)
    {
        if (!Enabled)
        {
            return;
        }
        if (supervisor.State != LinkState.Connected)
        {
            // Offline delivery: the newest copy waits for the phone (only the newest is kept), and
            // reaches its clipboard on reconnect. Same exclusions as a live sync.
            if (registry.PairedSerial is not null && await ReadShareableTextAsync() is { } pending && pending != _lastReceived)
            {
                _lastSent = pending;
                NoteLocalCopy(pending);
                await outbox.EnqueueTextAsync(OutboxService.ClipKind, pending);
            }
            return;
        }
        // The phone must speak v1 for clipboard messages to mean anything.
        if (connection.Current is not { Companion.V: >= 1 })
        {
            return;
        }
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text) || await IsExcludedAsync(content))
            {
                return;
            }
            text = await content.GetTextAsync();
        }
        catch (Exception)
        {
            return; // clipboard owned by a picky app; skip this change
        }
        if (string.IsNullOrEmpty(text) || (!force && (text == _lastSent || text == _lastReceived)))
        {
            return;
        }
        _lastSent = text;
        if (text != _lastReceived)
        {
            NoteLocalCopy(text);
        }
        await SendOrQueueAsync(text);
    }

    private void NoteLocalCopy(string text)
    {
        _localCopy = text;
        _localCopyAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Sends a clip to the phone, or queues it for the reconnect when the link turns out dead
    /// (owner, 2026-11-01: "clipboard sync broken both ways" around Wi-Fi drops: during the 12 s
    /// the session is kept, or on a stale link, the send failed and the copy was dropped).
    /// </summary>
    private async Task SendOrQueueAsync(string text)
    {
        try
        {
            await connection.SendClipboardAsync(text, CancellationToken.None);
            log.Log(LogLevel.Info, "Clipboard synced to phone");
            AddHistory(text, fromPhone: false);
        }
        catch (Exception ex) when (ex is LincException or IOException or ObjectDisposedException or TimeoutException)
        {
            await outbox.EnqueueTextAsync(OutboxService.ClipKind, text);
            log.Log(LogLevel.Info, "Clipboard: the phone didn't take the copy (the link is down); it goes when the phone is back");
        }
    }

    /// <summary>The clipboard's text, unless it's empty, not text, or excluded (password managers).</summary>
    private static async Task<string?> ReadShareableTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text) || await IsExcludedAsync(content))
            {
                return null;
            }
            var text = await content.GetTextAsync();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the app that copied asked to be left out of clipboard monitors or history — the
    /// formats password managers set (Windows' clipboard-history exclusion rules).
    /// </summary>
    private static async Task<bool> IsExcludedAsync(DataPackageView content)
    {
        if (content.Contains("ExcludeClipboardContentFromMonitorProcessing") || content.Contains("Clipboard Viewer Ignore"))
        {
            return true;
        }
        foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!content.Contains(format))
            {
                continue;
            }
            try
            {
                if (await content.GetDataAsync(format) is IRandomAccessStream stream)
                {
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    reader.ByteOrder = ByteOrder.LittleEndian;
                    if (await reader.LoadAsync(4) == 4 && reader.ReadUInt32() == 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return true; // can't tell: err on the side of not sending
            }
        }
        return false;
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (envelope.Type == MessageType.ClipboardGet)
        {
            _ = OnDesktopClipboardChangedAsync(force: true); // the phone's Sync now (v24)
            return;
        }
        if (envelope.Type != MessageType.ClipboardChanged || !Enabled)
        {
            return;
        }
        var text = (string?)envelope.Payload["text"];
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        // A copy the phone sends again after a drop (replay: true, at = when it was copied, v32+
        // phones): if this PC copied something newer meanwhile, the PC's wins on both sides.
        var replay = Replay(envelope.Payload);
        if (replay is { } atMs &&
            _localCopy is { } mine && mine != text &&
            DateTimeOffset.FromUnixTimeMilliseconds(atMs) < _localCopyAtUtc)
        {
            KeepLocalCopy(mine);
            return;
        }
        var phoneWins = replay is not null && _localCopy is { } older && older != text;
        var repeat = text == _lastReceived;
        _lastReceived = text;
        try
        {
            if (!repeat)
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                log.Log(LogLevel.Info, "Clipboard synced from phone");
                ReceivedFromPhone?.Invoke(text);
            }
            AddHistory(text, fromPhone: true);
            if (phoneWins)
            {
                // The PC may have handed the phone its older copy on reconnect: give the phone
                // its own newer one back so both end on the same clip.
                _lastSent = text;
                _ = SendOrQueueAsync(text);
            }
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; the next change will win.
        }
    }

    /// <summary>A replayed phone copy lost to a newer one made here: keep (or put back) this PC's and send it to the phone.</summary>
    private void KeepLocalCopy(string mine)
    {
        _lastSent = mine;
        try
        {
            var package = new DataPackage();
            package.SetText(mine);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; the phone still gets this PC's copy.
        }
        log.Log(LogLevel.Info, "Clipboard: this PC's copy is newer than the one the phone sent again; kept it on both");
        _ = SendOrQueueAsync(mine);
    }

    /// <summary>When a replayed phone copy was made (unix ms), or null for an ordinary live copy.</summary>
    private static long? Replay(JsonObject payload)
    {
        try
        {
            return payload["replay"] is JsonValue r && r.TryGetValue<bool>(out var yes) && yes &&
                payload["at"] is JsonValue a && a.TryGetValue<long>(out var at) ? at : null;
        }
        catch (Exception)
        {
            return null; // malformed: treat as a live copy
        }
    }
}
