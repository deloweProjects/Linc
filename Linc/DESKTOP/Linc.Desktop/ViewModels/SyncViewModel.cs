using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// One SMS in a conversation. <see cref="IsPending"/> (M9d-2, §3.3) is true for a message that
/// went to the outbox instead of the wire — set by <see cref="SyncViewModel.SendReplyAsync"/> when
/// queuing, and flipped back by <see cref="SyncViewModel.OnOutboxRowFlushed"/> once
/// <see cref="OutboxService.RowFlushed"/> reports the matching row actually sent (§3.4).
/// </summary>
public sealed partial class MessageVm(SmsMessage m) : ObservableObject
{
    public string Body => m.Body;
    public bool Incoming => m.Incoming;
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(m.Date).LocalDateTime.ToString("g");

    // Incoming bubbles sit left on the secondary tint; outgoing right on the primary
    // tint (both retinted live by ThemeSyncService).
    public Microsoft.UI.Xaml.HorizontalAlignment Alignment =>
        m.Incoming ? Microsoft.UI.Xaml.HorizontalAlignment.Left : Microsoft.UI.Xaml.HorizontalAlignment.Right;
    public Microsoft.UI.Xaml.Media.Brush Bubble =>
        (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[
            m.Incoming ? "SecondaryContainerBrush" : "PrimaryContainerBrush"];

    /// <summary>True while this message is queued in the outbox rather than actually sent (§3.3).
    /// Default false — every message built from a live/cached load is already-sent by definition;
    /// only <see cref="SyncViewModel.SendReplyAsync"/>'s disconnected branch sets this true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampOpacity))]
    public partial bool IsPending { get; set; }

    /// <summary>Muted further while pending (§3.3 — "do not rely on colour alone" pairs with a
    /// dimmer timestamp alongside the "Queued" label, not instead of it).</summary>
    public double TimestampOpacity => IsPending ? 0.35 : 0.6;
}

/// <summary>One call-log entry (v12). Click dials it back.</summary>
public sealed partial class CallVm(CallEntry entry, IConnectionManager connection)
{
    public string Number => string.IsNullOrEmpty(entry.Number) ? "Unknown" : entry.Number;
    public string Detail => $"{Cap(entry.Type)} · {DateTimeOffset.FromUnixTimeMilliseconds(entry.Date).LocalDateTime:g}"
        + (entry.Duration > 0 ? $" · {entry.Duration / 60}m {entry.Duration % 60}s" : "");
    public string Glyph => entry.Type switch { "missed" => "", "outgoing" => "", "rejected" => "", _ => "" };

    [RelayCommand]
    private async Task DialAsync()
    {
        if (!string.IsNullOrEmpty(entry.Number))
        {
            try { await connection.CallDialAsync(entry.Number, CancellationToken.None); }
            catch (LincException) { }
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}

/// <summary>A conversation (all messages with one address), newest first in the list.</summary>
public sealed partial class ConversationVm(string address) : ObservableObject
{
    public string Address => address;
    public ObservableCollection<MessageVm> Messages { get; } = [];

    [ObservableProperty]
    public partial string Snippet { get; set; } = "";
}

/// <summary>
/// The Sync page (docs/ROADMAP.md M18, D-025). M18a: lane toggles (sync.config) and a
/// working Messages (SMS) lane — conversation list + reply. Folders/Photos/Calls are
/// toggles now, engines in M18b/c.
/// </summary>
public partial class SyncViewModel : ObservableObject
{
    private readonly IConnectionManager _connection;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly ISyncEngine _engine;
    private readonly ILogService _log;
    private readonly OutboxService _outbox;
    private readonly LincStore _store;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ConversationVm> Conversations { get; } = [];
    public ObservableCollection<CallVm> Calls { get; } = [];

    /// <summary>sync_cache's retention cap for this page's write-through (M9d-2, §3.1) — the same
    /// 200-row cap M9d-1 set for Home; both pages write the same table so they must agree.</summary>
    private const int MaxCachedRowsPerKind = 200;

    public SyncViewModel(
        IConnectionManager connection, IConnectionSupervisor supervisor,
        IDeviceRegistry registry, ISyncEngine engine, ILogService log,
        OutboxService outbox, LincStore store)
    {
        _connection = connection;
        _supervisor = supervisor;
        _registry = registry;
        _engine = engine;
        _log = log;
        _outbox = outbox;
        _store = store;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ReplyText = "";
        LaneMessage = "";
        DialNumber = "";
        IncomingText = "";
        FolderPcPath = registry.FolderSyncPcPath ?? "";
        FolderPhonePath = registry.FolderSyncPhonePath;
        SyncStatus = engine.StatusText;

        // Assigning the properties runs their change-handlers, which persist the same
        // value back (harmless) and no-op PushConfig while disconnected.
        FoldersOn = registry.SyncLane("folders");
        PhotosOn = registry.SyncLane("photos");
        MessagesOn = registry.SyncLane("messages");
        CallsOn = registry.SyncLane("calls");

        _engine.StatusChanged += () => _dispatcher.TryEnqueue(() => SyncStatus = _engine.StatusText);

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(MessagesPlaceholder));
            OnPropertyChanged(nameof(CallsPlaceholder));
            OnPropertyChanged(nameof(ShowMessagesPlaceholder));
            OnPropertyChanged(nameof(ShowCallsPlaceholder));
            if (IsConnected)
            {
                PushConfig();
                if (MessagesOn) { _ = LoadMessagesAsync(); }
                if (CallsOn) { _ = LoadCallsAsync(); }
            }
            else
            {
                // M9d-2 §3.1/D-032: a transition OUT of Connected reads the offline cache instead
                // of leaving Conversations/Calls as whatever they last were — the exact cure
                // M9d-1 built for Home (sync_cache + HomeCacheFormat), reused rather than
                // reimplemented. Never Conversations.Clear()/Calls.Clear() here — that would
                // reintroduce the blank-on-disconnect bug this task exists to fix.
                _ = LoadCachedLaneDataAsync();
            }
            RefreshOfflineBanner();
        });
        _connection.CompanionMessageReceived += env => _dispatcher.TryEnqueue(() => OnCompanionMessage(env));
        _outbox.RowFlushed += flushed => _dispatcher.TryEnqueue(() => OnOutboxRowFlushed(flushed));

        if (IsConnected)
        {
            if (MessagesOn) { _ = LoadMessagesAsync(); }
            if (CallsOn) { _ = LoadCallsAsync(); }
        }
        else
        {
            _ = LoadCachedLaneDataAsync();
        }
    }

    public bool IsConnected => _supervisor.State == LinkState.Connected;

    // ---- sync_cache offline banner (M9d-2, §3.5 — reuses M9d-1's Home banner exactly) ----

    /// <summary>Every <c>updated_utc</c> LoadCachedLaneDataAsync last restored, across both kinds
    /// (conversation/call), for the active serial. Empty until a read-through has actually run.</summary>
    private IReadOnlyList<DateTimeOffset> _restoredCacheTimestamps = [];

    /// <summary>"Showing what was last synced at {time}.", or null if nothing was restored.
    /// Formatting is <see cref="HomeOfflineBanner"/>, not a second copy (§3.5). The banner states
    /// cache age only — the link's own state is stated once elsewhere (M15b D2).</summary>
    public string? OfflineBannerText => HomeOfflineBanner.FormatText(_registry.PairedSerial, _restoredCacheTimestamps);

    public bool ShowOfflineBanner => !IsConnected && OfflineBannerText is not null;

    private void RefreshOfflineBanner()
    {
        OnPropertyChanged(nameof(OfflineBannerText));
        OnPropertyChanged(nameof(ShowOfflineBanner));
    }

    // ---- Lane toggles ----

    [ObservableProperty] public partial bool FoldersOn { get; set; }
    [ObservableProperty] public partial bool PhotosOn { get; set; }
    [ObservableProperty] public partial bool MessagesOn { get; set; }
    [ObservableProperty] public partial bool CallsOn { get; set; }

    [ObservableProperty] public partial string LaneMessage { get; set; }

    partial void OnFoldersOnChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFolderConfig));
        OnPropertyChanged(nameof(ShowSyncBar));
        SetLane("folders", value);
        _engine.Reconfigure();
    }

    partial void OnPhotosOnChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPhotosInfo));
        OnPropertyChanged(nameof(ShowSyncBar));
        SetLane("photos", value);
        _engine.Reconfigure();
    }
    partial void OnCallsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(CallsOff));
        OnPropertyChanged(nameof(NeitherLane));
        OnPropertyChanged(nameof(ShowCallsPanel));
        SetLane("calls", value);
        if (value && IsConnected) { _ = LoadCallsAsync(); }
        else { Calls.Clear(); }
        OnPropertyChanged(nameof(ShowCallsPlaceholder));
    }
    public bool MessagesOff => !MessagesOn;
    public bool NeitherLane => !MessagesOn && !CallsOn;

    // M07 polish: both lanes now show at once, side by side, instead of Messages
    // hiding Calls. Each card's visibility binds to its own lane; SyncPage's code-behind
    // spans the sole visible card across both columns when only one lane is on.
    public bool ShowMessagesPanel => MessagesOn;
    public bool ShowCallsPanel => CallsOn;

    partial void OnMessagesOnChanged(bool value)
    {
        OnPropertyChanged(nameof(MessagesOff));
        OnPropertyChanged(nameof(NeitherLane));
        OnPropertyChanged(nameof(ShowMessagesPanel));
        SetLane("messages", value);
        if (value && IsConnected) { _ = LoadMessagesAsync(); }
        else { Conversations.Clear(); Selected = null; }
        OnPropertyChanged(nameof(ShowMessagesPlaceholder));
    }

    private void SetLane(string lane, bool value)
    {
        _registry.SaveSyncLane(lane, value);
        PushConfig();
    }

    private async void PushConfig()
    {
        if (!IsConnected)
        {
            return;
        }
        try
        {
            await _connection.SyncConfigAsync(new JsonObject
            {
                ["folders"] = FoldersOn,
                ["photos"] = PhotosOn,
                ["messages"] = MessagesOn,
                ["calls"] = CallsOn,
            }, CancellationToken.None);
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message;
        }
    }

    // ---- Folders & Photos lanes (M18c, D-027) ----

    public bool ShowFolderConfig => FoldersOn;
    public bool ShowPhotosInfo => PhotosOn;
    public bool ShowSyncBar => FoldersOn || PhotosOn;

    public string PhotosInfo => $"New camera photos land in {PhotosDestination} (pull-only). Needs All-files access on the phone.";

    /// <summary>The PC folder synced by the Folders lane; empty until the user picks one.</summary>
    [ObservableProperty] public partial string FolderPcPath { get; set; }

    /// <summary>The phone path synced by the Folders lane (default /sdcard/Download).</summary>
    [ObservableProperty] public partial string FolderPhonePath { get; set; }

    /// <summary>Last reconcile result from the engine, shown under the lane toggles.</summary>
    [ObservableProperty] public partial string SyncStatus { get; set; }

    /// <summary>Where the Photos lane drops new camera shots (fixed, pull-only).</summary>
    public string PhotosDestination => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc", "Photos");

    /// <summary>Called by the page's folder picker (UI concern stays in code-behind).</summary>
    public void SetFolderPcPath(string path)
    {
        FolderPcPath = path;
        SaveFolderPaths();
    }

    partial void OnFolderPhonePathChanged(string value) => SaveFolderPaths();

    private void SaveFolderPaths()
    {
        _registry.SaveFolderSyncPaths(
            string.IsNullOrWhiteSpace(FolderPcPath) ? null : FolderPcPath,
            string.IsNullOrWhiteSpace(FolderPhonePath) ? "/sdcard/Download" : FolderPhonePath);
        _engine.Reconfigure();
    }

    [RelayCommand]
    private Task SyncNowAsync() => _engine.ReconcileNowAsync(CancellationToken.None);

    // ---- Messages lane ----

    [ObservableProperty] public partial ConversationVm? Selected { get; set; }
    [ObservableProperty] public partial string ReplyText { get; set; }

    public bool HasConversations => Conversations.Count > 0;
    public bool CanReply => Selected is not null;

    // Empty / disconnected states for the Messages card (M07 polish).
    public bool ShowMessagesPlaceholder => Conversations.Count == 0;
    public string MessagesPlaceholder =>
        IsConnected ? "No conversations yet." : "Connect your phone to see messages.";

    partial void OnSelectedChanged(ConversationVm? value) => OnPropertyChanged(nameof(CanReply));

    private async Task LoadMessagesAsync()
    {
        try
        {
            var messages = await _connection.SmsListAsync(100, CancellationToken.None);
            BuildConversations(messages);
            LaneMessage = "";

            // §3.1 write-through: live loads keep write-through as they do on Home, so Sync's own
            // cache stays fresh even if the owner never opens Home. The whole thread lives under
            // one row keyed by the address (same shape/key as HomeViewModel.LoadMessagesAsync) so
            // the two pages upsert the same rows rather than fighting over the table.
            if (_supervisor.Device?.Serial is { } serial)
            {
                var cacheRows = messages.GroupBy(m => m.Address).Select(group =>
                {
                    var ordered = group.OrderBy(m => m.Date).ToList();
                    var cached = new CachedConversation(group.Key,
                        [.. ordered.Select(m => new CachedMessage(m.Body, m.Incoming, m.Date))]);
                    return (group.Key, JsonSerializer.Serialize(cached));
                }).ToList();
                _ = WriteThroughCacheAsync(serial, HomeCacheKinds.Conversation, cacheRows);
            }
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message; // e.g. SMS permission not granted on the phone
        }
    }

    private void BuildConversations(IReadOnlyList<SmsMessage> messages)
    {
        var previouslySelected = Selected?.Address;
        Conversations.Clear();
        // messages come newest-first; group by address, keep chronological within a thread.
        foreach (var group in messages.GroupBy(m => m.Address)
                     .OrderByDescending(g => g.Max(m => m.Date)))
        {
            var convo = new ConversationVm(group.Key);
            foreach (var m in group.OrderBy(m => m.Date))
            {
                convo.Messages.Add(new MessageVm(m));
            }
            convo.Snippet = group.OrderByDescending(m => m.Date).First().Body;
            Conversations.Add(convo);
        }
        Selected = Conversations.FirstOrDefault(c => c.Address == previouslySelected) ?? Conversations.FirstOrDefault();
        OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(ShowMessagesPlaceholder));
    }

    /// <summary>
    /// §3.1's read-through: repopulates Conversations/Calls from sync_cache for the active serial
    /// instead of leaving them as whatever they last were. Runs on construction and on every
    /// transition out of Connected (both wired in the constructor). Fire-and-forget with a
    /// catch-all: sync_cache reads never throw by contract, but nothing here may reach the UI
    /// thread's exception handler either way (mirrors HomeViewModel.LoadCachedLaneWidgetsAsync).
    /// <para><b>M9e:</b> same store-readiness race as Home (see
    /// <see cref="HomeViewModel.LoadCachedLaneWidgetsAsync"/>'s remarks) — await the idempotent
    /// <see cref="LincStore.EnsureSchemaAsync"/> before reading so a cold-launch construction call
    /// cannot land before <see cref="LincStore.IsAvailable"/> flips true.</para>
    /// </summary>
    private async Task LoadCachedLaneDataAsync()
    {
        var serial = _registry.PairedSerial;
        if (serial is null)
        {
            Conversations.Clear();
            Selected = null;
            Calls.Clear();
            _restoredCacheTimestamps = [];
            OnPropertyChanged(nameof(HasConversations));
            OnPropertyChanged(nameof(ShowMessagesPlaceholder));
            OnPropertyChanged(nameof(HasCalls));
            OnPropertyChanged(nameof(ShowCallsPlaceholder));
            RefreshOfflineBanner();
            return;
        }

        await _store.EnsureSchemaAsync();

        try
        {
            var convoRows = _store.ListSyncCacheRows(serial, HomeCacheKinds.Conversation, MaxCachedRowsPerKind);
            var callRows = _store.ListSyncCacheRows(serial, HomeCacheKinds.Call, MaxCachedRowsPerKind);

            var previousSelected = Selected?.Address;
            Conversations.Clear();
            foreach (var row in convoRows)
            {
                if (JsonSerializer.Deserialize<CachedConversation>(row.PayloadJson) is not { } cached)
                {
                    continue;
                }
                var convo = new ConversationVm(cached.Address);
                foreach (var m in cached.Messages)
                {
                    convo.Messages.Add(new MessageVm(new SmsMessage(cached.Address, m.Body, m.Date, m.Incoming)));
                }
                convo.Snippet = cached.Messages.Count > 0 ? cached.Messages[^1].Body : "";
                Conversations.Add(convo);
            }
            Selected = Conversations.FirstOrDefault(c => c.Address == previousSelected) ?? Conversations.FirstOrDefault();

            Calls.Clear();
            foreach (var row in callRows)
            {
                if (JsonSerializer.Deserialize<CachedCall>(row.PayloadJson) is not { } cached)
                {
                    continue;
                }
                Calls.Add(new CallVm(new CallEntry(cached.Number, cached.Type, cached.Date, cached.Duration), _connection));
            }

            _restoredCacheTimestamps = [.. convoRows.Select(r => r.UpdatedUtc), .. callRows.Select(r => r.UpdatedUtc)];

            OnPropertyChanged(nameof(HasConversations));
            OnPropertyChanged(nameof(ShowMessagesPlaceholder));
            OnPropertyChanged(nameof(HasCalls));
            OnPropertyChanged(nameof(ShowCallsPlaceholder));
        }
        catch (Exception ex)
        {
            // Never let a bad row reach the UI as a crash (same posture as Home's read-through).
            _log.Log(LogLevel.Warn, $"Sync: could not read the offline cache; showing empty. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
            Conversations.Clear();
            Selected = null;
            Calls.Clear();
            _restoredCacheTimestamps = [];
        }

        RefreshOfflineBanner();
    }

    /// <summary>
    /// §3.1 write-through: upserts one row per item into sync_cache, then prunes to the 200-row
    /// cap. Fire-and-forget from each Load*Async with its own catch-all — a cache write must
    /// never be able to break the live load it rides along with. Logs the kind and row count
    /// only, never a key or payload (§3.6 — a conversation's key is an address, a call's embeds a
    /// number). Identical in shape to HomeViewModel.WriteThroughCacheAsync — kept as its own copy
    /// rather than a shared helper because HomeViewModel is not a dependency either page should
    /// take on the other; both call the same LincStore methods underneath.
    /// </summary>
    private async Task WriteThroughCacheAsync(string serial, string kind, IReadOnlyList<(string Key, string PayloadJson)> rows)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, payload) in rows)
            {
                await _store.UpsertSyncCacheRowAsync(serial, kind, key, payload, now);
            }
            await _store.PruneSyncCacheAsync(serial, kind, MaxCachedRowsPerKind);
            _log.Log(LogLevel.Info, $"Sync: cached {rows.Count} {kind} row(s) for serial={serial}.");
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"Sync: sync_cache write-through failed for kind={kind}; cache stays stale. (serial={serial}, {ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// §3.4: a queued message becomes normal once <see cref="OutboxService.RowFlushed"/> reports
    /// its row actually sent. Matching is by address+body via the pure static
    /// <see cref="OutboxService.MatchesFlushedRow"/> — not by outbox row id, because the row is
    /// already deleted from the store by the time this fires. Searches every loaded conversation,
    /// not only <see cref="Selected"/>: "open" here means "loaded in this page's Conversations
    /// list" (the same posture <see cref="OnCompanionMessage"/> already takes for an incoming
    /// SMS), not specifically the one showing in the reply pane. If nothing matches — the
    /// conversation isn't loaded (a different device tab, or Messages toggled off) — this does
    /// nothing at all: no toast, no badge, per §3.4.
    /// </summary>
    private void OnOutboxRowFlushed(OutboxService.FlushedRow flushed)
    {
        if (flushed.Kind != OutboxService.SmsSendKind)
        {
            return;
        }
        foreach (var convo in Conversations)
        {
            var match = convo.Messages.FirstOrDefault(msg =>
                OutboxService.MatchesFlushedRow(convo.Address, msg.Body, msg.IsPending, flushed.Address, flushed.Body));
            if (match is not null)
            {
                match.IsPending = false;
                return;
            }
        }
    }

    [RelayCommand]
    private async Task SendReplyAsync()
    {
        var body = ReplyText?.Trim() ?? "";
        if (Selected is null || body.Length == 0)
        {
            return;
        }

        // M9c §2.2 — the queue decision point: queue only when actually disconnected
        // (supervisor.State != Connected), checked BEFORE attempting the send. A send that
        // fails while the link is up has a real reason (no SIM, refused permission, malformed
        // address) and silently retrying it forever would hide a bug from the user and could
        // double-send — so a Connected-state throw stays a failure, surfaced the way the code
        // did before (no catch-and-queue fallback, here or in OutboxService).
        if (_supervisor.State != LinkState.Connected)
        {
            var id = await _outbox.EnqueueSmsAsync(Selected.Address, body);
            // §2.8 — keep appending the message to the conversation as the code does today so the
            // user sees what they typed. The lane message is plain language, never a raw error.
            // §3.3 — IsPending=true is what makes this render as "Queued" rather than lying that
            // it sent; §3.4's OnOutboxRowFlushed flips it back once the outbox actually delivers it.
            Selected.Messages.Add(new MessageVm(new SmsMessage(Selected.Address, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false)) { IsPending = true });
            Selected.Snippet = body;
            ReplyText = "";
            LaneMessage = "Not connected — this text will send when your phone reconnects.";
            _log.Log(LogLevel.Info, $"SyncViewModel: queued a text while disconnected (outbox id={id}).");
            return;
        }

        try
        {
            await _connection.SmsSendAsync(Selected.Address, body, CancellationToken.None);
            Selected.Messages.Add(new MessageVm(new SmsMessage(Selected.Address, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false)));
            Selected.Snippet = body;
            ReplyText = "";
            _log.Log(LogLevel.Info, "Sent a text from the PC");
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message;
        }
    }

    // ---- Calls lane (v12) ----

    public bool CallsOff => !CallsOn;
    public bool HasCalls => Calls.Count > 0;

    // Empty / disconnected states for the Calls card (M07 polish).
    public bool ShowCallsPlaceholder => Calls.Count == 0;
    public string CallsPlaceholder =>
        IsConnected ? "No recent calls." : "Connect your phone to see recent calls.";

    [ObservableProperty] public partial string DialNumber { get; set; }

    // Incoming-call banner.
    [ObservableProperty] public partial bool IncomingRinging { get; set; }
    [ObservableProperty] public partial string IncomingText { get; set; }

    private async Task LoadCallsAsync()
    {
        try
        {
            var log = await _connection.CallLogAsync(60, CancellationToken.None);
            Calls.Clear();
            var cacheRows = new List<(string Key, string PayloadJson)>();
            foreach (var c in log)
            {
                Calls.Add(new CallVm(c, _connection));
                // §3.1 write-through — CallEntry carries no id, so Number+Date is the natural
                // per-event identity (same key shape as HomeViewModel.LoadCallsAsync).
                var key = $"{c.Number}|{c.Date}";
                cacheRows.Add((key, JsonSerializer.Serialize(new CachedCall(c.Number, c.Type, c.Date, c.Duration))));
            }
            OnPropertyChanged(nameof(HasCalls));
            OnPropertyChanged(nameof(ShowCallsPlaceholder));
            LaneMessage = "";

            if (_supervisor.Device?.Serial is { } serial)
            {
                _ = WriteThroughCacheAsync(serial, HomeCacheKinds.Call, cacheRows);
            }
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DialAsync()
    {
        var number = DialNumber?.Trim() ?? "";
        if (number.Length == 0)
        {
            return;
        }
        try
        {
            await _connection.CallDialAsync(number, CancellationToken.None);
            DialNumber = "";
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        try
        {
            await _connection.CallDeclineAsync(CancellationToken.None);
            IncomingRinging = false;
        }
        catch (LincException ex)
        {
            LaneMessage = ex.Message;
        }
    }

    private void OnCompanionMessage(Protocol.Envelope envelope)
    {
        if (envelope.Type == Protocol.MessageType.CallIncoming && CallsOn)
        {
            var ringing = (bool?)envelope.Payload["ringing"] ?? false;
            var number = (string?)envelope.Payload["number"];
            IncomingRinging = ringing;
            IncomingText = string.IsNullOrEmpty(number) ? "Incoming call" : $"Incoming call from {number}";
            if (ringing)
            {
                try
                {
                    Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(
                        new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                            .AddText(IncomingText).AddText("On your phone").BuildNotification());
                }
                catch (Exception) { }
            }
            else
            {
                // Refresh the log so the just-ended call appears.
                if (IsConnected) { _ = LoadCallsAsync(); }
            }
            return;
        }
        if (envelope.Type != Protocol.MessageType.SmsReceived || !MessagesOn)
        {
            return;
        }
        var address = (string?)envelope.Payload["address"];
        var body = (string?)envelope.Payload["body"];
        if (string.IsNullOrEmpty(address) || body is null)
        {
            return;
        }
        var message = new SmsMessage(address, body, (long?)envelope.Payload["date"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
        var convo = Conversations.FirstOrDefault(c => c.Address == address);
        if (convo is null)
        {
            convo = new ConversationVm(address);
            Conversations.Insert(0, convo);
            OnPropertyChanged(nameof(HasConversations));
            OnPropertyChanged(nameof(ShowMessagesPlaceholder));
        }
        convo.Messages.Add(new MessageVm(message));
        convo.Snippet = body;
    }
}
