using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public sealed record CompanionHello(string App, string AppVersion, int V);

public sealed record PhotoItem(string Id, string Path, long TakenAt);

public sealed record SmsMessage(string Address, string Body, long Date, bool Incoming);

public sealed record CallEntry(string Number, string Type, long Date, long Duration);

public sealed record DeviceStatus(
    int Battery,
    bool Charging,
    long StorageFreeBytes,
    long StorageTotalBytes,
    double? CpuLoad1m = null,
    long? RamUsedBytes = null,
    long? RamTotalBytes = null,
    long UptimeMillis = 0,
    int? WifiSignalLevel = null,
    int? WifiLinkSpeedMbps = null,
    IReadOnlyDictionary<string, string>? ThemeLight = null,
    IReadOnlyDictionary<string, string>? ThemeDark = null,
    bool? DndEnabled = null,
    string? SoundMode = null,
    string? WallpaperId = null,
    // v15 (D-055): display-control status. All three are nullable so a v14 phone simply
    // omits them and the desktop shows "unknown" rather than claiming the phone is in
    // portrait at 50%. The phone sends these only when the negotiated version >= 15 AND
    // it actually read a value; reads of Settings.System never fail, so in practice they
    // are present-or-absent on every v15 link.
    string? RotationMode = null,
    bool? BrightnessAuto = null,
    int? BrightnessLevel = null);

/// <summary>
/// Protocol client for the phone's companion socket, reached through an ADB-forwarded
/// local TCP port. A single receive loop routes replies to their pending requests and
/// surfaces unsolicited messages (clipboard etc.) via <see cref="MessageReceived"/>.
/// </summary>
public sealed class CompanionClient : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private Stream? _stream;
    private IDisposable? _owner;

    /// <summary>The v5 session token from the phone's hello reply, or null on a pre-v5 link.</summary>
    public string? SessionToken { get; private set; }

    /// <summary>
    /// How this transport opens a typed channel connection: given (channel, sessionToken),
    /// returns a stream positioned right after the channel header. ADB dials the forwarded
    /// port itself; Direct TLS asks the phone to dial back (v9 `channel.open`).
    /// </summary>
    public Func<int, string, CancellationToken, Task<Stream>>? ChannelOpener { get; set; }

    /// <summary>Unsolicited phone → desktop messages. Fires on the receive-loop thread.</summary>
    public event Action<Envelope>? MessageReceived;

    /// <summary>Convenience for the ADB transport: dial the forwarded loopback port.</summary>
    public async Task<CompanionHello> ConnectAsync(int localPort, string appVersion, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, ct);
        }
        catch (SocketException ex)
        {
            // Expected and routine: wrap the raw socket exception into the plain-language
            // message and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            tcp.Dispose();
            throw new LincException(CompanionUnreachable, ex);
        }
        ChannelOpener = (channel, token, openCt) => OpenAdbChannelAsync(localPort, channel, token, openCt);
        return await ConnectAsync(tcp.GetStream(), appVersion, ct, owner: tcp);
    }

    /// <summary>Runs the handshake over any established transport stream (ADB or Direct TLS).</summary>
    public async Task<CompanionHello> ConnectAsync(
        Stream stream, string appVersion, CancellationToken ct, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
        _ = ReceiveLoopAsync();

        var hello = Envelope.Create(MessageType.Hello, new JsonObject
        {
            ["app"] = "linc-desktop",
            ["appVersion"] = appVersion,
            ["minV"] = ProtocolConstants.MinVersion,
            ["maxV"] = ProtocolConstants.Version,
        });
        var reply = await RequestAsync(hello, MessageType.Hello, ct);
        SessionToken = (string?)reply.Payload["sessionToken"];
        return new CompanionHello(
            App: (string?)reply.Payload["app"] ?? "linc-android",
            AppVersion: (string?)reply.Payload["appVersion"] ?? "?",
            V: (int?)reply.Payload["v"] ?? ProtocolConstants.MinVersion);
    }

    /// <summary>v9: asks the phone to dial back a channel connection (used by the TLS opener).</summary>
    public Task RequestChannelOpenAsync(int channel, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.ChannelOpen, new JsonObject { ["channel"] = channel }),
            MessageType.Ok,
            ct);

    private static async Task<Stream> OpenAdbChannelAsync(int localPort, int channel, string token, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, ct);
            var stream = tcp.GetStream();
            var header = new JsonObject { ["channel"] = channel, ["sessionToken"] = token };
            await Framing.WriteAsync(stream, header.ToJsonString(), ct);
            return stream;
        }
        catch
        {
            // Expected and routine: clean up the half-open socket before propagating the
            // failure — the caller logs and surfaces it.
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a bulk channel (3), requests one resource by id, and returns its bytes
    /// (null when unavailable or on a pre-v5 link). One request per connection.
    /// </summary>
    /// <summary>Opens a typed channel and returns its stream, positioned after the header (v9/v10).</summary>
    public Task<Stream> OpenChannelAsync(int channel, CancellationToken ct)
    {
        if (SessionToken is null || ChannelOpener is null)
        {
            throw new LincException("The phone's connection can't carry that yet.");
        }
        return ChannelOpener(channel, SessionToken, ct);
    }

    public async Task<IReadOnlyList<PhotoItem>> GetRecentPhotosAsync(int limit, CancellationToken ct)
    {
        var reply = await RequestAsync(
            Envelope.Create(MessageType.PhotosRecent, new JsonObject { ["limit"] = limit }),
            MessageType.PhotosRecent, ct);
        var list = new List<PhotoItem>();
        if (reply.Payload["photos"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonObject obj && (string?)obj["id"] is { } id && (string?)obj["path"] is { } path)
                {
                    list.Add(new PhotoItem(id, path, (long?)obj["takenAt"] ?? 0));
                }
            }
        }
        return list;
    }

    /// <summary>
    /// The v16 installed-app inventory (D-058). Callers must gate on
    /// <see cref="AppsPayload.IsSupported"/> first — a v≤15 phone ignores the request entirely
    /// and this would time out waiting for a reply that will never come. Parsing lives in
    /// <see cref="AppsPayload.Parse"/> so the harness can exercise it without a socket.
    /// </summary>
    public async Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(
            Envelope.Create(MessageType.AppsGet, AppsPayload.RequestPayload()),
            MessageType.Apps, ct);
        return AppsPayload.Parse(reply.Payload);
    }

    public Task SyncConfigAsync(JsonObject config, CancellationToken ct) =>
        RequestAsync(Envelope.Create(MessageType.SyncConfig, config), MessageType.Ok, ct);

    public async Task<IReadOnlyList<SmsMessage>> SmsListAsync(int limit, CancellationToken ct)
    {
        var reply = await RequestAsync(
            Envelope.Create(MessageType.SmsList, new JsonObject { ["limit"] = limit }),
            MessageType.SmsList, ct);
        var list = new List<SmsMessage>();
        if (reply.Payload["messages"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonObject o && (string?)o["address"] is { } address)
                {
                    list.Add(new SmsMessage(address, (string?)o["body"] ?? "", (long?)o["date"] ?? 0, (bool?)o["incoming"] ?? false));
                }
            }
        }
        return list;
    }

    public Task SmsSendAsync(string address, string body, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.SmsSend, new JsonObject { ["address"] = address, ["body"] = body }),
            MessageType.Ok, ct);

    public async Task<IReadOnlyList<CallEntry>> CallLogAsync(int limit, CancellationToken ct)
    {
        var reply = await RequestAsync(
            Envelope.Create(MessageType.CallLog, new JsonObject { ["limit"] = limit }),
            MessageType.CallLog, ct);
        var list = new List<CallEntry>();
        if (reply.Payload["calls"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonObject o)
                {
                    list.Add(new CallEntry((string?)o["number"] ?? "", (string?)o["type"] ?? "other", (long?)o["date"] ?? 0, (long?)o["duration"] ?? 0));
                }
            }
        }
        return list;
    }

    public Task CallDialAsync(string number, CancellationToken ct) =>
        RequestAsync(Envelope.Create(MessageType.CallDial, new JsonObject { ["number"] = number }), MessageType.Ok, ct);

    public Task CallDeclineAsync(CancellationToken ct) =>
        RequestAsync(Envelope.Create(MessageType.CallDecline), MessageType.Ok, ct);

    public Task ContinueUrlAsync(string url, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.ContinueUrl, new JsonObject { ["url"] = url }),
            MessageType.Ok, ct);

    public async Task<byte[]?> FetchBulkAsync(string kind, string id, CancellationToken ct)
    {
        if (SessionToken is null || ChannelOpener is null)
        {
            return null; // pre-v5 phone never opened the session layer
        }
        try
        {
            using var stream = await ChannelOpener(3, SessionToken, ct);
            var request = new JsonObject { ["kind"] = kind, ["id"] = id };
            await Framing.WriteAsync(stream, request.ToJsonString(), ct);
            var bytes = await Framing.ReadBytesAsync(stream, ct);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // Expected and routine: wrap the raw socket/IO exception into the plain-language
            // message and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            throw new LincException(CompanionUnreachable, ex);
        }
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(Envelope.Create(MessageType.StatusGet), MessageType.Status, ct);
        var (rotation, auto, level) = DisplayPayload.ParseStatus(reply.Payload);
        return new DeviceStatus(
            Battery: (int?)reply.Payload["battery"] ?? 0,
            Charging: (bool?)reply.Payload["charging"] ?? false,
            StorageFreeBytes: (long?)reply.Payload["storageFreeBytes"] ?? 0,
            StorageTotalBytes: (long?)reply.Payload["storageTotalBytes"] ?? 0,
            CpuLoad1m: (double?)reply.Payload["cpuLoad1m"],
            RamUsedBytes: (long?)reply.Payload["ramUsedBytes"],
            RamTotalBytes: (long?)reply.Payload["ramTotalBytes"],
            UptimeMillis: (long?)reply.Payload["uptimeMillis"] ?? 0,
            WifiSignalLevel: (int?)reply.Payload["wifiSignalLevel"],
            WifiLinkSpeedMbps: (int?)reply.Payload["wifiLinkSpeedMbps"],
            ThemeLight: ParseTheme(reply.Payload["themeLight"]),
            ThemeDark: ParseTheme(reply.Payload["themeDark"]),
            DndEnabled: (bool?)reply.Payload["dndEnabled"],
            SoundMode: (string?)reply.Payload["soundMode"],
            WallpaperId: (string?)reply.Payload["wallpaperId"],
            RotationMode: rotation,
            BrightnessAuto: auto,
            BrightnessLevel: level);
    }

    private static IReadOnlyDictionary<string, string>? ParseTheme(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }
        var colors = new Dictionary<string, string>();
        foreach (var (role, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var hex))
            {
                colors[role] = hex;
            }
        }
        return colors.Count > 0 ? colors : null;
    }

    public Task SetClipboardAsync(string text, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.ClipboardSet, new JsonObject { ["text"] = text }),
            MessageType.Ok,
            ct);

    public Task DismissNotificationAsync(string key, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.NotificationDismiss, new JsonObject { ["key"] = key }),
            MessageType.Ok,
            ct);

    public Task SubscribeAsync(string topic, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.Subscribe, new JsonObject { ["topic"] = topic }),
            MessageType.Ok,
            ct);

    public Task FireNotificationActionAsync(string key, int index, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.NotificationAction,
                new JsonObject { ["key"] = key, ["index"] = index }),
            MessageType.Ok,
            ct);

    public Task SendNotificationReplyAsync(string key, int index, string text, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.NotificationReply,
                new JsonObject { ["key"] = key, ["index"] = index, ["text"] = text }),
            MessageType.Ok,
            ct);

    public Task LocateDeviceAsync(CancellationToken ct) =>
        RequestAsync(Envelope.Create(MessageType.DeviceLocate), MessageType.Ok, ct);

    public Task SetSoundModeAsync(string mode, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.SoundSet, new JsonObject { ["mode"] = mode }),
            MessageType.Ok,
            ct);

    public Task SetDndAsync(bool enabled, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.DndSet, new JsonObject { ["enabled"] = enabled }),
            MessageType.Ok,
            ct);

    /// <summary>
    /// v15: tell the phone to apply a screen rotation. [mode] is one of
    /// <c>"auto"</c>, <c>"portrait"</c>, <c>"landscape"</c>; the wire vocabulary is
    /// fixed by PROTOCOL.md v15 and the phone answers <c>ok</c> or
    /// <c>error not-granted</c> (the WRITE_SETTINGS appop — D-055).
    /// </summary>
    public Task SetDisplayRotationAsync(string mode, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.DisplayRotationSet, DisplayPayload.RotationPayload(mode)),
            MessageType.Ok,
            ct);

    /// <summary>
    /// v15: tell the phone to apply a brightness setting. When [auto] is true the phone
    /// switches to its adaptive-brightness mode and [level] is ignored. When [auto] is
    /// false, [level] must be a 0–100 percentage (caller-validated) and the phone scales
    /// it to its own panel range. Throws <see cref="ArgumentException"/> for a null or
    /// out-of-range level when auto is false (rather than sending a frame the phone
    /// would reject with an <c>internal</c> error), and <see cref="LincException"/>
    /// carrying the phone's plain-language refusal when WRITE_SETTINGS is not granted.
    /// </summary>
    public Task SetDisplayBrightnessAsync(bool auto, int? level, CancellationToken ct) =>
        RequestAsync(
            Envelope.Create(MessageType.DisplayBrightnessSet, DisplayPayload.BrightnessPayload(auto, level)),
            MessageType.Ok,
            ct);

    public Task SendMediaControlAsync(string action, long? positionMs, CancellationToken ct)
    {
        var payload = new JsonObject { ["action"] = action };
        if (positionMs is { } position)
        {
            payload["positionMs"] = position;
        }
        return RequestAsync(Envelope.Create(MessageType.MediaControl, payload), MessageType.Ok, ct);
    }

    /// <summary>
    /// Writes one frame without awaiting a reply — the v13 PC → phone pushes
    /// (`pc.media.state`, `share.incoming`), which the phone consumes unsolicited.
    /// </summary>
    public async Task SendAsync(Envelope envelope, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        await _writeLock.WaitAsync(ct);
        try
        {
            await Framing.WriteAsync(stream, envelope.ToJson(), ct);
        }
        catch (IOException ex)
        {
            // Expected and routine: wrap the raw IO exception into the plain-language message
            // and rethrow — callers already handle a dead link (e.g. ConnectionManager's fire-
            // and-forget push swallows this by design; the health loop handles reconnect).
            throw new LincException(CompanionUnreachable, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var json = await Framing.ReadAsync(_stream!, CancellationToken.None);
                if (json is null)
                {
                    break; // clean close
                }
                Envelope envelope;
                try
                {
                    envelope = Envelope.Parse(json);
                }
                catch (JsonException)
                {
                    continue; // garbled messages are ignored per spec
                }
                if (envelope.ReplyTo is not null && _pending.TryRemove(envelope.ReplyTo, out var pending))
                {
                    pending.TrySetResult(envelope);
                }
                else
                {
                    MessageReceived?.Invoke(envelope);
                }
            }
        }
        catch (Exception)
        {
            // Socket died; fall through to fail the waiters.
        }
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var waiter))
            {
                waiter.TrySetException(new LincException(CompanionUnreachable));
            }
        }
    }

    private async Task<Envelope> RequestAsync(Envelope request, string expectedReplyType, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var pending = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = pending;
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await Framing.WriteAsync(stream, request.ToJson(), ct);
            }
            finally
            {
                _writeLock.Release();
            }

            // A live link answers quickly; a zombie link answers never.
            var completed = await Task.WhenAny(pending.Task, Task.Delay(ReplyTimeout, ct));
            if (completed != pending.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new LincException(CompanionUnreachable);
            }
            var reply = await pending.Task;
            if (reply.Type == MessageType.Error)
            {
                var code = (string?)reply.Payload["code"];
                // The phone's error messages are plain language by contract
                // (CONTRIBUTING.md); surface them directly when present.
                var message = (string?)reply.Payload["message"];
                throw new LincException(code switch
                {
                    "version-mismatch" =>
                        "Your phone's Linc app and this PC's Linc app don't speak the same version. " +
                        "Update both apps and try again.",
                    _ when !string.IsNullOrWhiteSpace(message) => message,
                    _ => "The phone couldn't do that — it may already be gone on the phone.",
                });
            }
            if (reply.Type != expectedReplyType)
            {
                throw new LincException(CompanionUnreachable);
            }
            return reply;
        }
        catch (IOException ex)
        {
            // Expected and routine: wrap the raw IO exception into the plain-language message
            // and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            throw new LincException(CompanionUnreachable, ex);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public async Task<string?> ExchangeTlsAsync(string certBase64, int tlsPort, int reversePort, CancellationToken ct)
    {
        var reply = await RequestAsync(
            Envelope.Create(MessageType.TlsExchange, new JsonObject
            {
                ["cert"] = certBase64,
                ["tlsPort"] = tlsPort,
                ["reversePort"] = reversePort,
            }),
            MessageType.TlsExchange,
            ct);
        return (string?)reply.Payload["cert"];
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _owner?.Dispose();
    }

    private const string CompanionUnreachable =
        "Linc can't reach the companion app on your phone. Open Linc on the phone and " +
        "tap “Start companion service”, then try again.";
}
