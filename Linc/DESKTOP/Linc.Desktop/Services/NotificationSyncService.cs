using Linc.Desktop.Protocol;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Linc.Desktop.Services;

public sealed record NotificationAction(int Index, string Title, bool AllowsReply);

public sealed record NotificationItem(
    string Key, string App, string Title, string Text, DateTimeOffset PostedAt,
    string? AppPackage = null,
    string? Category = null,
    string? ConversationTitle = null,
    string? LargeIconId = null,
    IReadOnlyList<NotificationAction>? Actions = null);

public interface INotificationSyncService
{
    /// <summary>Must be called once from the UI thread.</summary>
    void Start();

    /// <summary>Raised on the UI thread whenever the list changes.</summary>
    event Action? Changed;

    IReadOnlyList<NotificationItem> Items { get; }

    /// <summary>Removes locally and cancels the notification on the phone.</summary>
    Task DismissAsync(string key);

    /// <summary>Fires a notification action on the phone (v6). Returns false if it failed.</summary>
    Task<bool> FireActionAsync(string key, int index);

    /// <summary>Sends an inline reply through a notification action (v6). Returns false if it failed.</summary>
    Task<bool> ReplyAsync(string key, int index, string text);

    /// <summary>How many notifications have been relayed/dismissed this process (backlog excluded).</summary>
    int RelayedCount { get; }
    int DismissedCount { get; }

    bool Enabled { get; set; }
}

/// <summary>
/// Desktop side of the notification bridge (docs/PROTOCOL.md v2): keeps the mirrored
/// notification list, shows Windows toasts for new arrivals (reconnect backlogs are
/// flagged `existing` and listed silently), and forwards dismissals to the phone.
/// </summary>
public sealed class NotificationSyncService(
    IConnectionManager connection,
    IConnectionSupervisor supervisor,
    IDeviceRegistry registry,
    ILogService log,
    LincStore store) : INotificationSyncService
{
    private readonly List<NotificationItem> _items = [];
    private DispatcherQueue? _dispatcher;

    public event Action? Changed;

    public IReadOnlyList<NotificationItem> Items => _items;
    public int RelayedCount { get; private set; }
    public int DismissedCount { get; private set; }

    public bool Enabled
    {
        get => registry.NotificationSyncEnabled;
        set => registry.SaveNotificationSyncEnabled(value);
    }

    public void Start()
    {
        if (_dispatcher is not null)
        {
            return;
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        connection.CompanionMessageReceived += envelope =>
            _dispatcher.TryEnqueue(() => OnCompanionMessage(envelope));
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            if (supervisor.State != LinkState.Connected && _items.Count > 0)
            {
                _items.Clear();
                Changed?.Invoke();
            }
        });
    }

    public async Task<bool> FireActionAsync(string key, int index)
    {
        try
        {
            await connection.SendNotificationActionAsync(key, index, CancellationToken.None);
            log.Log(LogLevel.Info, "Notification action fired on the phone");
            return true;
        }
        catch (LincException ex)
        {
            log.Log(LogLevel.Warn, $"Notification action failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ReplyAsync(string key, int index, string text)
    {
        try
        {
            await connection.SendNotificationReplyAsync(key, index, text, CancellationToken.None);
            log.Log(LogLevel.Info, "Replied to a notification from the PC");
            return true;
        }
        catch (LincException ex)
        {
            log.Log(LogLevel.Warn, $"Notification reply failed: {ex.Message}");
            return false;
        }
    }

    public async Task DismissAsync(string key)
    {
        if (_items.RemoveAll(item => item.Key == key) > 0)
        {
            Changed?.Invoke();
        }
        try
        {
            await connection.SendNotificationDismissAsync(key, CancellationToken.None);
            DismissedCount++;
            log.Log(LogLevel.Info, "Notification dismissed on both devices");
        }
        catch (LincException)
        {
            // Link is dying; the supervisor will notice. The phone-side notification stays.
        }
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (!Enabled)
        {
            return;
        }
        switch (envelope.Type)
        {
            case MessageType.NotificationPosted:
                var postedKey = (string?)envelope.Payload["key"];
                if (postedKey is null)
                {
                    return; // malformed; ignore per envelope rules
                }
                var item = new NotificationItem(
                    Key: postedKey,
                    App: (string?)envelope.Payload["app"] ?? "App",
                    Title: (string?)envelope.Payload["title"] ?? "",
                    Text: (string?)envelope.Payload["text"] ?? "",
                    PostedAt: DateTimeOffset.FromUnixTimeMilliseconds(
                        (long?)envelope.Payload["postedAt"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    AppPackage: (string?)envelope.Payload["appPackage"],
                    Category: (string?)envelope.Payload["category"],
                    ConversationTitle: (string?)envelope.Payload["conversationTitle"],
                    LargeIconId: (string?)envelope.Payload["largeIconId"],
                    Actions: ParseActions(envelope.Payload["actions"]));
                _items.RemoveAll(existing => existing.Key == item.Key);
                _items.Insert(0, item);
                Changed?.Invoke();
                var isBacklog = (bool?)envelope.Payload["existing"] ?? false;
                if (!isBacklog)
                {
                    RelayedCount++;
                    ShowToast(item);
                    log.Log(LogLevel.Info, $"Notification relayed from {item.App}");

                    // M9b (D-045): the single guarded persist point. The check happens HERE, at
                    // the ingestion point, before any row is built — so with the toggle OFF no
                    // INSERT is ever issued (§2.2). Body text is the consented on-disk exception;
                    // it goes into the row but no log line in this code path mentions it (§2.9 —
                    // only `item.App` is named above and below). Backlog re-streams on reconnect
                    // are skipped: history starts from the moment the feature was enabled (§2.5),
                    // never backfilled from the past. Fire-and-forget on a background thread so a
                    // flaky DB never blocks the in-memory feed or the toast.
                    if (registry.NotificationHistoryEnabled && registry.PairedSerial is { } serial)
                    {
                        _ = InsertNotificationOnceReadyAsync(serial, item);
                    }
                }
                break;

            case MessageType.NotificationRemoved:
                var key = (string?)envelope.Payload["key"];
                if (key is not null && _items.RemoveAll(existing => existing.Key == key) > 0)
                {
                    Changed?.Invoke();
                }
                break;
        }
    }

    /// <summary>
    /// M9e: closes the same cold-start race as <see cref="HomeViewModel.LoadCachedLaneWidgetsAsync"/>
    /// — <see cref="LincStore.InsertNotificationAsync"/> silently no-ops while
    /// <see cref="LincStore.IsAvailable"/> is still false during App.xaml.cs's fire-and-forget
    /// <see cref="LincStore.EnsureSchemaAsync"/>, so a notification landing in that narrow window
    /// would be dropped with no log line at all. <see cref="LincStore.EnsureSchemaAsync"/> is
    /// idempotent, so awaiting it here before every insert is safe and cheap once the store is warm.
    /// </summary>
    private async Task InsertNotificationOnceReadyAsync(string serial, NotificationItem item)
    {
        await store.EnsureSchemaAsync();
        await store.InsertNotificationAsync(serial, item.PostedAt, item.AppPackage, item.Title, item.Text, item.Key);
    }

    private static IReadOnlyList<NotificationAction>? ParseActions(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is not System.Text.Json.Nodes.JsonArray array || array.Count == 0)
        {
            return null;
        }
        var actions = new List<NotificationAction>();
        foreach (var entry in array)
        {
            if (entry is System.Text.Json.Nodes.JsonObject obj && (int?)obj["index"] is { } index)
            {
                actions.Add(new NotificationAction(
                    Index: index,
                    Title: (string?)obj["title"] ?? "",
                    AllowsReply: (bool?)obj["allowsReply"] ?? false));
            }
        }
        return actions.Count > 0 ? actions : null;
    }

    private static void ShowToast(NotificationItem item)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(item.Title.Length > 0 ? item.Title : item.App)
                .AddText(item.Text)
                .AddText(item.App);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception)
        {
            // Toast infrastructure unavailable (e.g. notifications disabled); the
            // notification center still lists the item.
        }
    }
}
