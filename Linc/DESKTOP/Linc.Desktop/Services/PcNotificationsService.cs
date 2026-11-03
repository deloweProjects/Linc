using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Linc.Desktop.Services;

/// <summary>
/// The PC's own notifications on the phone's Home › Control (2026-11-03; the owner: "a section
/// for notifications from the PC (not the ones from the phone)"). Windows' notification centre is
/// read with <see cref="UserNotificationListener"/>, which asks the user once, on the PC.
///
/// <para>Sent only while the phone watches (<c>pc.notifs.watch</c>) and PC Settings › "Send this
/// PC's notifications to the phone" is on. Linc's own toasts — the phone's notifications shown on
/// the PC — are never sent back. The list is polled every two seconds rather than taken from
/// <c>NotificationChanged</c>, which Windows raises only for packaged apps; Linc is unpackaged.</para>
/// </summary>
public sealed class PcNotificationsService(IConnectionManager connection, IDeviceRegistry registry, ILogService log)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private bool _attached;
    private bool _watching;
    private CancellationTokenSource? _loop;
    private readonly Dictionary<uint, JsonObject> _sent = [];
    private readonly object _lock = new();

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        connection.CompanionMessageReceived += OnMessage;
    }

    /// <summary>PC Settings' switch changed: start or stop, and tell a watching phone.</summary>
    public void SettingChanged()
    {
        if (_watching) _ = Task.Run(StartAsync);
    }

    /// <summary>Settings' "Allow" button: Windows' prompt, shown on the PC's UI thread.</summary>
    public async Task<string> AskAsync()
    {
        string access = await RequestAsync();
        if (_watching) _ = Task.Run(StartAsync);
        return access;
    }

    private void OnMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.PcNotifsWatch:
                bool on = (bool?)envelope.Payload["on"] ?? false;
                bool ask = (bool?)envelope.Payload["ask"] ?? false;
                _watching = on;
                if (!on) { Stop(); return; }
                _ = Task.Run(async () =>
                {
                    if (ask && Access() == UserNotificationListenerAccessStatus.Unspecified) await RequestAsync();
                    await StartAsync();
                });
                break;
            case MessageType.PcNotifDismiss:
                var payload = envelope.Payload.DeepClone().AsObject();
                _ = Task.Run(() => Dismiss(payload));
                break;
        }
    }

    private static UserNotificationListenerAccessStatus? Access()
    {
        try { return UserNotificationListener.Current.GetAccessStatus(); }
        catch { return null; } // this Windows can't share them; StartAsync says why
    }

    /// <summary>Windows' own prompt; it must come from the UI thread.</summary>
    private Task<string> RequestAsync()
    {
        var done = new TaskCompletionSource<string>();
        if (App.MainWindowInstance?.DispatcherQueue is not { } queue
            || !queue.TryEnqueue(async () =>
            {
                try { done.TrySetResult(Name(await UserNotificationListener.Current.RequestAccessAsync())); }
                catch (Exception ex)
                {
                    log.Log(LogLevel.Warn, $"PC notifications: Windows wouldn't ask ({ex.Message})");
                    done.TrySetResult("unavailable");
                }
            }))
        {
            done.TrySetResult("unavailable");
        }
        return done.Task;
    }

    private static string Name(UserNotificationListenerAccessStatus? status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => "allowed",
        UserNotificationListenerAccessStatus.Denied => "denied",
        UserNotificationListenerAccessStatus.Unspecified => "ask",
        _ => "unavailable",
    };

    /// <summary>Sends the whole list (or why there is none), then keeps the phone up to date.</summary>
    private async Task StartAsync()
    {
        Stop();
        var status = Access();
        bool enabled = registry.PcNotifsToPhone;
        var snapshot = new JsonObject { ["enabled"] = enabled, ["access"] = Name(status), ["items"] = new JsonArray() };
        if (status is null) snapshot["error"] = "Windows didn't let Linc read its notifications.";
        if (enabled && status == UserNotificationListenerAccessStatus.Allowed)
        {
            Dictionary<uint, JsonObject> now;
            try { now = await ReadAsync(); }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"PC notifications: couldn't read them ({ex.Message})");
                snapshot["access"] = "unavailable";
                snapshot["error"] = "Windows didn't let Linc read its notifications.";
                await SendAsync(MessageType.PcNotifs, snapshot);
                return;
            }
            lock (_lock)
            {
                _sent.Clear();
                foreach (var (id, note) in now) _sent[id] = note;
            }
            snapshot["items"] = new JsonArray([.. now.Values.OrderByDescending(n => (long?)n["time"] ?? 0).Select(n => (JsonNode)n.DeepClone())]);
            var cts = new CancellationTokenSource();
            _loop = cts;
            _ = Task.Run(() => LoopAsync(cts.Token));
        }
        await SendAsync(MessageType.PcNotifs, snapshot);
    }

    private void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) { return; }
            // The phone went away, or the owner turned it off on the PC: stop until asked again.
            if (connection.Current is null) { _watching = false; Stop(); return; }
            if (!registry.PcNotifsToPhone) { await StartAsync(); return; }
            Dictionary<uint, JsonObject> now;
            try { now = await ReadAsync(); }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"PC notifications: couldn't read them ({ex.Message})");
                continue;
            }
            List<JsonObject> posted;
            List<uint> removed;
            lock (_lock)
            {
                posted = [.. now.Where(n => !_sent.ContainsKey(n.Key)).Select(n => n.Value)];
                removed = [.. _sent.Keys.Where(id => !now.ContainsKey(id))];
                foreach (var note in posted) _sent[uint.Parse((string)note["id"]!)] = note;
                foreach (var id in removed) _sent.Remove(id);
            }
            foreach (var note in posted.OrderBy(n => (long?)n["time"] ?? 0)) await SendAsync(MessageType.PcNotifPosted, (JsonObject)note.DeepClone());
            foreach (var id in removed) await SendAsync(MessageType.PcNotifRemoved, new JsonObject { ["id"] = id.ToString() });
        }
    }

    /// <summary>Windows' toasts now, minus Linc's own, as the phone's notes.</summary>
    private static async Task<Dictionary<uint, JsonObject>> ReadAsync()
    {
        var result = new Dictionary<uint, JsonObject>();
        var list = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var n in list)
        {
            string app = "";
            string aumid = "";
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; aumid = n.AppInfo?.AppUserModelId ?? ""; }
            catch { /* some system toasts carry no app info */ }
            if (IsLinc(app, aumid)) continue;
            var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (texts.Count == 0) continue;
            result[n.Id] = new JsonObject
            {
                ["id"] = n.Id.ToString(),
                ["app"] = app,
                ["title"] = Cut(texts[0], 200),
                ["text"] = Cut(string.Join("\n", texts.Skip(1)), 1000),
                ["time"] = n.CreationTime.ToUnixTimeMilliseconds(),
            };
        }
        return result;
    }

    /// <summary>Linc and Linc Test show the phone's notifications as toasts; those aren't the PC's.</summary>
    public static bool IsLinc(string app, string aumid) =>
        app is "Linc" or "Linc Test" || aumid.Contains("Linc", StringComparison.OrdinalIgnoreCase);

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private async Task Dismiss(JsonObject p)
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if ((bool?)p["all"] == true)
            {
                List<uint> ids;
                lock (_lock) ids = [.. _sent.Keys];
                foreach (var id in ids) listener.RemoveNotification(id);
                log.Log(LogLevel.Info, $"PC notifications: cleared {ids.Count} from the phone");
            }
            else if (uint.TryParse((string?)p["id"], out var id))
            {
                // Only one Linc sent: the phone can't reach anything else in the notification centre.
                bool known;
                lock (_lock) known = _sent.ContainsKey(id);
                if (known) listener.RemoveNotification(id);
            }
        }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"PC notifications: couldn't dismiss ({ex.Message})"); }
        // The loop sends the removals.
        await Task.CompletedTask;
    }

    private async Task SendAsync(string type, JsonObject payload)
    {
        try { await connection.SendToPhoneAsync(Envelope.Create(type, payload), CancellationToken.None); }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"PC notifications: couldn't send to the phone ({ex.Message})"); }
    }
}
