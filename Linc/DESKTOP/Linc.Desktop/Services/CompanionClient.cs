using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <param name="Resumed">v32: this hello resumed the PC's existing session on a new transport
/// (nothing was lost; subscriptions and requests carry on), rather than starting a new one.</param>
public sealed record CompanionHello(string App, string AppVersion, int V, bool Resumed = false);

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
    int? BrightnessLevel = null,
    // Optional (any version): false when the phone hasn't given Linc Do Not Disturb access, which
    // Ring / Vibrate / Silent and DND need. Absent on older phones: treated as allowed.
    bool? DndAccess = null);

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
    private volatile bool _disposed;

    // ---- v32 LincLink session (Vibe/agent/design/linclink.md §4) ------------------------------
    // A session outlives its transport: when the stream dies, requests stay pending and frames
    // stay numbered and kept, and the next transport's hello resumes it (TakeTransportAsync).
    // All null below v32, where everything works exactly as before.
    private readonly object _linkLock = new();
    // Guards which stream is the session's transport (_stream, _owner, _transportGen, the
    // keepalive's echo clock): the swap to a resumed transport and a read loop's end are decided
    // under it, so an old route closing during a resume can never take the new one down with it.
    private readonly object _transportLock = new();
    private LinkSession? _link;
    private int _transportGen;
    private CompanionClient? _sink;
    private int _sinkGen;
    private readonly object _sinkLock = new(); // this client's read loop ending vs. handing it to a session
    private bool _loopEnded;
    // Set by ConnectionManager.Install: until then a dying transport fails its requests at once, as
    // below v32, instead of holding the not-yet-live session (and the supervisor's gate) open.
    private bool _adopted;
    private string? _helloId;
    private TaskCompletionSource? _helloDecided;
    private long _lastHeardMs;
    private long? _echoSentMs;
    private CancellationTokenSource? _keepalive;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long NowMs => Clock.ElapsedMilliseconds;

    /// <summary>The highest version offered in hello. 31 keeps LincLink off (Settings › Connection).</summary>
    public int MaxVersion { get; set; } = ProtocolConstants.Version;

    /// <summary>True while this client holds a v32 session, which a new transport can resume.</summary>
    public bool HasLinkSession => _link is not null;

    /// <summary>True when the session has no transport right now and is waiting for a resume.</summary>
    public bool IsDetached
    {
        get
        {
            lock (_transportLock)
            {
                return _link is not null && _stream is null;
            }
        }
    }

    /// <summary>Set on a client whose hello resumed another client's session: use that one.</summary>
    public CompanionClient? ResumedInto { get; private set; }

    /// <summary>v32: the phone's LincLink id (its certificate hash) and the link-secret generation it holds.</summary>
    public string? PhoneLinkId { get; private set; }
    public int PhoneSecretGen { get; private set; }

    /// <summary>v32: where the phone says it can be dialled (<c>link.where</c>), newest last.</summary>
    public event Action<IReadOnlyList<string>, int>? PhoneWhere;

    /// <summary>The last <c>link.where</c>, kept for a subscriber that attaches after it arrived (Install).</summary>
    public (IReadOnlyList<string> Addresses, int Port)? LastWhere { get; private set; }

    /// <summary>
    /// v32: the transport under the session died. The session waits for another (nothing pending
    /// is failed); the supervisor finds one. Fires on the dying transport's read loop.
    /// </summary>
    public event Action? TransportLost;

    /// <summary>The v5 session token from the phone's hello reply, or null on a pre-v5 link.</summary>
    public string? SessionToken { get; private set; }

    /// <summary>This PC's id for the phone (<see cref="ITlsTransportService.PcId"/>), sent in hello.</summary>
    public string? PcId { get; set; }

    /// <summary>
    /// How this transport opens a typed channel connection: given (channel, sessionToken),
    /// returns a stream positioned right after the channel header. ADB dials the forwarded
    /// port itself; Direct TLS asks the phone to dial back (v9 `channel.open`).
    /// </summary>
    public Func<int, string, CancellationToken, Task<Stream>>? ChannelOpener { get; set; }

    /// <summary>Unsolicited phone → desktop messages. Fires on the receive-loop thread.</summary>
    public event Action<Envelope>? MessageReceived;

    /// <summary>Convenience for the ADB transport: dial the forwarded loopback port.</summary>
    public async Task<CompanionHello> ConnectAsync(int localPort, string appVersion, CancellationToken ct, CompanionClient? resume = null)
    {
        var tcp = new TcpClient();
        try
        {
            tcp.NoDelay = true; // control and video are latency-bound; Nagle only delays them
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, ct);
        }
        catch (SocketException ex)
        {
            // Expected and routine: wrap the raw socket exception into the plain-language
            // message and rethrow — the caller (ConnectionSupervisor) logs the LincException.
            tcp.Dispose();
            throw new LincException(CompanionUnreachable, ex);
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose(); // the attempt was called off mid-connect: nothing of it stays open
            throw;
        }
        ChannelOpener = (channel, token, openCt) => OpenAdbChannelAsync(localPort, channel, token, openCt);
        return await ConnectAsync(tcp.GetStream(), appVersion, ct, owner: tcp, resume);
    }

    /// <summary>Runs the handshake over any established transport stream (ADB or Direct TLS).</summary>
    /// <param name="resume">v32: the live or detached session this transport should carry on, if
    /// the phone still holds it; otherwise the phone starts a fresh one, exactly as before.</param>
    public async Task<CompanionHello> ConnectAsync(
        Stream stream, string appVersion, CancellationToken ct, IDisposable? owner = null, CompanionClient? resume = null)
    {
        int gen;
        lock (_transportLock)
        {
            _stream = stream;
            _owner = owner;
            gen = ++_transportGen;
        }

        var hello = Envelope.Create(MessageType.Hello, new JsonObject
        {
            ["app"] = "linc-desktop",
            ["appVersion"] = appVersion,
            // 1.0.0.11: the PC's name for the phone's Home and Connection sheet. An optional field:
            // older phones ignore unknown fields (PROTOCOL.md envelope rules).
            ["host"] = Environment.MachineName,
            // D-088: which PC this is, so a phone that knows several can tell them apart and keep
            // to one at a time. Optional: older phones ignore it.
            ["pcId"] = PcId,
            // Informational only (Provenance.cs); optional, older phones ignore it.
            ["author"] = Provenance.Author,
            ["minV"] = ProtocolConstants.MinVersion,
            ["maxV"] = MaxVersion,
        });
        if (MaxVersion >= LinkProtocol.Version)
        {
            // v32: offer the session to resume (an older phone ignores the field).
            hello.Payload["link"] = resume?.ResumeOffer() ?? new JsonObject { ["recv"] = 0 };
        }
        // The read loop waits after the hello reply until this side has decided whether the phone
        // resumed a session: frames the phone replays right after its reply belong to that session.
        _helloId = hello.Id;
        _helloDecided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ReceiveLoopAsync(stream, gen);
        try
        {
            var reply = await RequestAsync(hello, MessageType.Hello, ct);
            SessionToken = (string?)reply.Payload["sessionToken"];
            var result = new CompanionHello(
                App: (string?)reply.Payload["app"] ?? "linc-android",
                AppVersion: (string?)reply.Payload["appVersion"] ?? "?",
                V: (int?)reply.Payload["v"] ?? ProtocolConstants.MinVersion);
            if (result.V >= LinkProtocol.Version && reply.Payload["link"] is JsonObject link)
            {
                PhoneLinkId = (string?)link["id"];
                PhoneSecretGen = (int?)link["gen"] ?? 0;
                var session = (string?)link["session"];
                if ((bool?)link["resumed"] == true && resume is not null && session is not null)
                {
                    if (PhoneLinkId is null || PhoneLinkId != resume.PhoneLinkId)
                    {
                        // Only the phone the session belongs to may carry it on (its id is the hash of
                        // the certificate it answered with); another one claiming it gets nothing.
                        throw new LincException(CompanionUnreachable);
                    }
                    if (!await resume.TakeTransportAsync(this, session, (long?)link["recv"] ?? 0))
                    {
                        // The phone resumed, but this side can't replay what it lacks (it shouldn't
                        // happen: the phone checks `keep`). Drop the session so the next transport
                        // starts fresh instead of two sides counting differently.
                        resume.AbandonSession();
                        throw new LincException(CompanionUnreachable);
                    }
                    ResumedInto = resume;
                    return result with { Resumed = true };
                }
                if (!string.IsNullOrEmpty(session))
                {
                    lock (_linkLock)
                    {
                        _link = new LinkSession(session);
                    }
                    _lastHeardMs = NowMs;
                    StartKeepalive();
                }
            }
            return result;
        }
        finally
        {
            _helloDecided.TrySetResult();
        }
    }

    /// <summary>What this session's next hello offers: its id, what it has received, and the oldest frame it can replay.</summary>
    private JsonObject ResumeOffer()
    {
        lock (_linkLock)
        {
            return _link is { } link
                ? new JsonObject { ["session"] = link.Id, ["recv"] = link.Received, ["keep"] = link.FirstKept }
                : new JsonObject { ["recv"] = 0 };
        }
    }

    private void AbandonSession()
    {
        lock (_linkLock)
        {
            _link = null;
        }
    }

    /// <summary>
    /// v32: the phone resumed this session on <paramref name="shell"/>'s new transport. Move that
    /// transport here, replay every frame the phone hasn't acknowledged, and close the old one.
    /// The shell's read loop keeps reading the new stream and hands its frames to this client.
    /// </summary>
    private async Task<bool> TakeTransportAsync(CompanionClient shell, string session, long peerReceived)
    {
        Stream? oldStream = null;
        IDisposable? oldOwner = null;
        var deadOnArrival = false;
        int gen;
        await _writeLock.WaitAsync(); // nothing goes out on either transport while they swap
        try
        {
            IReadOnlyList<string>? replay;
            lock (_linkLock)
            {
                replay = _link is { } link && link.Id == session ? link.ReplayAfter(peerReceived) : null;
            }
            Stream stream;
            lock (_transportLock)
            {
                if (replay is null || shell._stream is not { } fresh || _disposed)
                {
                    return false;
                }
                stream = fresh;
                oldStream = _stream;
                oldOwner = _owner;
                gen = ++_transportGen; // before the new stream is published: the old loop's end is now stale
                _stream = stream;
                _owner = shell._owner;
                shell._stream = null;
                shell._owner = null;
                _lastHeardMs = NowMs;
                _echoSentMs = null;
            }
            // Hand the shell's read loop over to this session, unless that loop already ended (the new
            // transport died between the hello and here): then it is this session's loss to report.
            lock (shell._sinkLock)
            {
                if (shell._loopEnded)
                {
                    deadOnArrival = true;
                }
                else
                {
                    shell._sinkGen = gen;
                    shell._sink = this;
                }
            }
            if (shell.ChannelOpener is { } opener)
            {
                ChannelOpener = opener; // the new transport's way to open channels (ADB: its forward port)
            }
            if (!deadOnArrival)
            {
                try
                {
                    foreach (var json in replay)
                    {
                        await Framing.WriteAsync(stream, json, CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The new transport died already; its read loop reports it, and the frames are still kept.
                }
            }
        }
        finally
        {
            _writeLock.Release();
            oldStream?.Dispose();
            oldOwner?.Dispose();
        }
        if (deadOnArrival)
        {
            OnTransportEnded(gen);
        }
        return true;
    }

    /// <summary>
    /// ConnectionManager installed this client as the live link: from now on a dying transport
    /// leaves the session waiting for a resume. True when it has no transport already (it died
    /// between the hello and now), so the caller reports the loss at once.
    /// </summary>
    public bool MarkAdopted()
    {
        lock (_transportLock)
        {
            _adopted = true;
            return _link is not null && _stream is null;
        }
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
            tcp.NoDelay = true; // control and video are latency-bound; Nagle only delays them
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
            BrightnessLevel: level,
            DndAccess: (bool?)reply.Payload["dndAccess"]);
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
        try
        {
            await WriteAsync(envelope, ct);
        }
        catch (IOException ex)
        {
            // Expected and routine: wrap the raw IO exception into the plain-language message
            // and rethrow — callers already handle a dead link (e.g. ConnectionManager's fire-
            // and-forget push swallows this by design; the health loop handles reconnect).
            throw new LincException(CompanionUnreachable, ex);
        }
    }

    /// <summary>
    /// The one way a frame goes out. On a v32 session it is numbered, carries the ack, and is kept
    /// until the phone acknowledges it; with no transport (between routes) it is only kept, and the
    /// next transport replays it. Below v32 it is written exactly as before.
    /// </summary>
    /// <returns>The frame's seq on a v32 session (so a caller that gives up can neutralise it), else null.</returns>
    private async Task<long?> WriteAsync(Envelope envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            string json;
            bool sequenced;
            long? stamped = null;
            lock (_linkLock)
            {
                sequenced = _link is not null && envelope.Type != MessageType.LinkAck;
                if (_link is { } link && sequenced)
                {
                    var seq = link.Stamp();
                    envelope = envelope with { Seq = seq, Ack = link.Received };
                    json = envelope.ToJson();
                    link.Keep(seq, json);
                    link.AckSent(link.Received);
                    stamped = seq;
                }
                else
                {
                    json = envelope.ToJson();
                }
            }
            Stream? stream;
            bool adopted;
            lock (_transportLock)
            {
                stream = _stream;
                adopted = _adopted;
            }
            if (stream is null)
            {
                if (sequenced && adopted)
                {
                    return stamped; // between transports: kept, and replayed when the session resumes
                }
                if (sequenced)
                {
                    throw new LincException(CompanionUnreachable); // not live yet: fail the attempt now
                }
                throw new InvalidOperationException("not connected");
            }
            try
            {
                await Framing.WriteAsync(stream, json, ct);
            }
            catch (Exception ex) when (sequenced && ex is IOException or ObjectDisposedException)
            {
                // The transport is dying under a session: the frame is kept, the read loop reports the loss.
            }
            return stamped;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// A request this side gave up on (timed out, cancelled): if its frame is still kept for a
    /// replay, swap it for a numbered no-op, so a resume can never run what was reported as failed
    /// (an SMS the user then sends again would go out twice). If the phone already acknowledged it,
    /// it ran, just as a lost reply always could.
    /// </summary>
    private void Neutralize(long? seq)
    {
        if (seq is not { } s)
        {
            return;
        }
        var placeholder = (Envelope.Create(MessageType.LinkNoop) with { Seq = s, Ack = 0 }).ToJson();
        lock (_linkLock)
        {
            _link?.Neutralize(s, placeholder);
        }
    }

    private async Task SendLinkAckAsync(bool echo)
    {
        long ack;
        lock (_linkLock)
        {
            if (_link is not { } link)
            {
                return;
            }
            ack = link.Received;
            link.AckSent(ack);
        }
        try
        {
            await WriteAsync(Envelope.Create(MessageType.LinkAck, new JsonObject { ["ack"] = ack, ["echo"] = echo }), CancellationToken.None);
        }
        catch (Exception)
        {
            // A dead transport: the keepalive's timeout or the read loop reports it.
        }
    }

    /// <summary>
    /// v32 keepalive: after <see cref="LinkBrain.KeepaliveIdle"/> with nothing heard, ask the phone
    /// to echo; no echo within <see cref="LinkBrain.KeepaliveTimeout"/> means the transport is
    /// half-open (Wi-Fi gone without a FIN), so close it and let the session find another. Also
    /// sends a bare ack when one is due and nothing outgoing carried it.
    /// </summary>
    private void StartKeepalive()
    {
        _keepalive?.Cancel();
        _keepalive = new CancellationTokenSource();
        var ct = _keepalive.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return; // the client was disposed
                }
                Stream stream;
                var now = NowMs;
                var halfOpen = false;
                lock (_transportLock)
                {
                    if (_stream is not { } live)
                    {
                        continue; // between transports: nothing to keep alive
                    }
                    stream = live;
                    if (_echoSentMs is { } sent && now - sent > (long)LinkBrain.KeepaliveTimeout.TotalMilliseconds)
                    {
                        _echoSentMs = null;
                        halfOpen = true;
                    }
                }
                if (halfOpen)
                {
                    stream.Dispose(); // ends that transport's read loop, which reports the loss
                    continue;
                }
                bool due;
                lock (_linkLock)
                {
                    due = _link?.AckDue(now) == true;
                }
                if (_echoSentMs is null && now - _lastHeardMs > (long)LinkBrain.KeepaliveIdle.TotalMilliseconds)
                {
                    _echoSentMs = now;
                    await SendLinkAckAsync(echo: true);
                }
                else if (due)
                {
                    await SendLinkAckAsync(echo: false);
                }
            }
        }, ct);
    }

    private async Task ReceiveLoopAsync(Stream stream, int gen)
    {
        try
        {
            while (true)
            {
                var json = await Framing.ReadAsync(stream, CancellationToken.None);
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
                (Volatile.Read(ref _sink) ?? this).Handle(envelope);
                if (_helloDecided is { } decided && envelope.ReplyTo is { } replyTo && replyTo == _helloId)
                {
                    await decided.Task; // see ConnectAsync: what follows may belong to a resumed session
                }
            }
        }
        catch (Exception)
        {
            // Socket died; fall through.
        }
        CompanionClient? sink;
        int sinkGen;
        lock (_sinkLock)
        {
            _loopEnded = true;
            sink = _sink;
            sinkGen = _sinkGen;
        }
        if (sink is not null)
        {
            sink.OnTransportEnded(sinkGen);
        }
        else
        {
            OnTransportEnded(gen);
        }
    }

    private void Handle(Envelope envelope)
    {
        _lastHeardMs = NowMs;
        _echoSentMs = null;
        if (envelope.Type == MessageType.LinkAck)
        {
            lock (_linkLock)
            {
                if ((long?)envelope.Payload["ack"] is { } ack)
                {
                    _link?.OnAck(ack);
                }
            }
            if ((bool?)envelope.Payload["echo"] == true)
            {
                _ = SendLinkAckAsync(echo: false);
            }
            return;
        }
        lock (_linkLock)
        {
            if (_link is { } link)
            {
                if (envelope.Ack is { } ack)
                {
                    link.OnAck(ack);
                }
                if (envelope.Seq is { } seq && link.Accept(seq, NowMs) == FrameArrival.Duplicate)
                {
                    return; // a replay this side already handled: never twice
                }
            }
        }
        if (envelope.Type == MessageType.LinkNoop)
        {
            return; // it only takes up its number
        }
        if (envelope.Type == MessageType.LinkWhere && envelope.Payload["addrs"] is JsonArray addrs)
        {
            var list = addrs.Select(a => (string?)a).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList();
            var port = (int?)envelope.Payload["port"] ?? LinkProtocol.PhonePort;
            LastWhere = (list, port); // it usually arrives before Install subscribes (see LastWhere)
            PhoneWhere?.Invoke(list, port);
            return;
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

    /// <summary>A transport's read loop ended. A v32 session waits for another; anything else fails its waiters as before.</summary>
    private void OnTransportEnded(int gen)
    {
        Stream? stream;
        IDisposable? owner;
        bool lingers;
        lock (_transportLock)
        {
            if (gen != _transportGen)
            {
                return; // an old transport the session already moved off
            }
            if (_link is null || _disposed)
            {
                stream = null;
                owner = null;
                lingers = false; // below v32 (or disposed): exactly as before, the stream stays as it was
            }
            else
            {
                stream = _stream;
                owner = _owner;
                _stream = null;
                _owner = null;
                lingers = _adopted;
            }
        }
        stream?.Dispose();
        owner?.Dispose();
        if (lingers)
        {
            TransportLost?.Invoke();
            return;
        }
        FailPending();
    }

    private void FailPending()
    {
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
        var pending = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = pending;
        long? seq = null;
        try
        {
            seq = await WriteAsync(request, ct);

            // A live link answers quickly; a zombie link answers never.
            var completed = await Task.WhenAny(pending.Task, Task.Delay(ReplyTimeout, ct));
            if (completed != pending.Task)
            {
                Neutralize(seq); // reported as failed below: a resume must not run it after all
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
                // D-088: the phone is with another PC, or set to connect to another PC only.
                if (code == "busy")
                    throw new PhoneBusyException(message ?? "Your phone is connected to another PC right now.");
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

    /// <summary>v32: give the phone this pair's link secret (it keys the UDP beacon).</summary>
    public async Task ShareLinkSecretAsync(string secretBase64, int gen, CancellationToken ct)
    {
        await RequestAsync(
            Envelope.Create(MessageType.LinkSecret, new JsonObject { ["secret"] = secretBase64, ["gen"] = gen }),
            MessageType.Ok,
            ct);
        PhoneSecretGen = gen;
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
        _disposed = true;
        _keepalive?.Cancel();
        _stream?.Dispose();
        _owner?.Dispose();
        FailPending(); // a detached session has no read loop left to do it
    }

    private const string CompanionUnreachable =
        "Linc can't reach the Linc app on your phone. Open Linc on the phone (if it says it's " +
        "stopped, tap the PC card, then Start Linc) and try again.";
}

/// <summary>
/// The phone turned this PC away because it belongs to another PC right now (D-088). Not a fault:
/// the companion is running and answering, so nothing should try to restart it.
/// </summary>
public sealed class PhoneBusyException(string message) : LincException(message);

/// <summary>
/// ADB reaches the phone but its Linc app hasn't answered yet, even after Linc started it (v32
/// engine). Not a dead end: the supervisor shows the phone as connected while it keeps repairing.
/// </summary>
public sealed class CompanionNotAnsweringException() : LincException(
    "Linc is getting ready on your phone. It starts by itself over the cable; this takes a few seconds.");
