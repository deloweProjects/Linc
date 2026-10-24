using System.Text.Json.Nodes;
using System.Threading.Channels;
using Linc.Desktop.Protocol;
using Linc.Desktop.Services;

/// <summary>
/// The PC's real <see cref="CompanionClient"/> against a fake v32 phone, over in-memory links
/// that can be cut mid-stream. The fake phone follows the phone's side of PROTOCOL.md §v32
/// exactly as SocketServer.kt does (sessions kept after a transport dies, resume on a hello that
/// names them, replay, dedupe, echo), so this is also its executable spec.
/// </summary>
static class ClientSim
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var phone = new FakeLinkPhone();

        // A fresh v32 session.
        var (pcEnd, phoneEnd) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEnd);
        var client = new CompanionClient { PcId = "pc1" };
        var lost = 0;
        client.TransportLost += () => Interlocked.Increment(ref lost);
        var events = new List<string>();
        client.MessageReceived += e =>
        {
            if (e.Type == MessageType.ClipboardChanged) lock (events) events.Add((string?)e.Payload["text"] ?? "");
        };
        var hello = await client.ConnectAsync(pcEnd, "test", Timeout(5));
        check(hello.V == 32 && client.HasLinkSession && !hello.Resumed, $"a v32 hello starts a session (v{hello.V})");
        client.MarkAdopted(); // as ConnectionManager.Install does: from here a lost transport lingers

        // Requests and events in flight when the cable comes out.
        var requests = Enumerable.Range(0, 40).Select(i => client.SmsSendAsync("555", $"msg {i}", Timeout(20))).ToList();
        for (var i = 0; i < 20; i++) phone.SendEvent($"clip {i}");
        await Task.Delay(30);
        phoneEnd.Cut(); // both directions, frames in flight lost
        await WaitFor(() => Volatile.Read(ref lost) == 1, 3000);
        check(lost == 1 && client.IsDetached, "the PC notices the transport died and keeps the session (nothing failed)");
        for (var i = 20; i < 30; i++) phone.SendEvent($"clip {i}"); // the phone keeps talking while detached
        var later = Enumerable.Range(40, 10).Select(i => client.SmsSendAsync("555", $"msg {i}", Timeout(20))).ToList();

        // A new transport resumes it.
        var (pcEnd2, phoneEnd2) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEnd2);
        var shell = new CompanionClient { PcId = "pc1" };
        var hello2 = await shell.ConnectAsync(pcEnd2, "test", Timeout(5), resume: client);
        check(hello2.Resumed && shell.ResumedInto == client, "the next transport's hello resumes the same session");
        var all = requests.Concat(later).ToList();
        var done = await Task.WhenAll(all.Select(t => t.ContinueWith(x => x.Status == TaskStatus.RanToCompletion)));
        check(done.All(d => d), $"all {all.Count} requests answered across the cut");
        check(phone.Handled("sms.send").Count == all.Count && phone.Handled("sms.send").Distinct().Count() == all.Count,
            $"the phone handled each request exactly once ({phone.Handled("sms.send").Count} sms.send for {all.Count} asked)");
        await WaitFor(() => { lock (events) return events.Count >= 30; }, 3000);
        lock (events)
        {
            check(events.SequenceEqual(Enumerable.Range(0, 30).Select(i => $"clip {i}")),
                $"every event from the phone arrived once, in order ({events.Count}/30)");
        }

        // Make-before-break: a better route (the cable) resumes the session while the current one
        // is still up. The phone closes the old route as it resumes, so the PC may hear that loss
        // first; the supervisor ignores it when the session is attached again (IsSessionDetached).
        var (pcEndM, phoneEndM) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEndM);
        var shellM = new CompanionClient { PcId = "pc1" };
        var helloM = await shellM.ConnectAsync(pcEndM, "test", Timeout(5), resume: client);
        await Task.Delay(100);
        check(helloM.Resumed && !client.IsDetached,
            "a route taking over while the old one is still up leaves the session attached (a loss heard on the way is stale)");
        var afterHandoff = client.SmsSendAsync("555", "after handoff", Timeout(5));
        check(await Task.WhenAny(afterHandoff, Task.Delay(5000)) == afterHandoff && afterHandoff.IsCompletedSuccessfully,
            "...and requests carry on over the new route");
        all.Add(afterHandoff);

        // A request given up on while the session waits must not run when it resumes (a retried SMS
        // would go out twice): its kept frame becomes a numbered no-op.
        var (pcEndG, phoneEndG) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEndG);
        var shellG = new CompanionClient { PcId = "pc1" };
        await shellG.ConnectAsync(pcEndG, "test", Timeout(5), resume: client);
        var lostBeforeG = Volatile.Read(ref lost);
        phoneEndG.Cut();
        await WaitFor(() => Volatile.Read(ref lost) > lostBeforeG, 3000);
        var givenUp = client.SmsSendAsync("555", "given up", Timeout(1));
        var gaveUp = await Task.WhenAny(givenUp, Task.Delay(5000)) == givenUp && !givenUp.IsCompletedSuccessfully;
        var kept = client.SmsSendAsync("555", "kept", Timeout(20));
        var (pcEndH, phoneEndH) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEndH);
        var shellH = new CompanionClient { PcId = "pc1" };
        await shellH.ConnectAsync(pcEndH, "test", Timeout(5), resume: client);
        var keptDone = await Task.WhenAny(kept, Task.Delay(5000)) == kept && kept.IsCompletedSuccessfully;
        check(gaveUp && keptDone && !phone.Handled("sms.send").Contains("given up") && phone.Handled("sms.send").Contains("kept"),
            "a request that timed out between transports never runs after the resume; the next one does");

        // Only the phone the session belongs to may resume it.
        phone.PhoneId = "someone-else";
        var (pcEndI, phoneEndI) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEndI);
        var imposter = new CompanionClient { PcId = "pc1" };
        var refused = false;
        try
        {
            await imposter.ConnectAsync(pcEndI, "test", Timeout(5), resume: client);
        }
        catch (LincException)
        {
            refused = true;
        }
        phone.PhoneId = "phone1";
        check(refused && imposter.ResumedInto is null, "a phone with another id can't take the session over");
        all.Add(kept);

        // Half-open: the phone goes silent without closing (Wi-Fi out of range).
        var (pcEnd3, phoneEnd3) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEnd3);
        var shell3 = new CompanionClient { PcId = "pc1" };
        await shell3.ConnectAsync(pcEnd3, "test", Timeout(5), resume: client);
        phone.Mute = true;
        var before = Volatile.Read(ref lost);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await WaitFor(() => Volatile.Read(ref lost) > before, 12000);
        check(Volatile.Read(ref lost) > before && clock.Elapsed < LinkBrain.LingerGrace,
            $"a silent half-open link is called dead in {clock.Elapsed.TotalSeconds:0.0} s (keepalive), inside the {LinkBrain.LingerGrace.TotalSeconds:0} s linger");
        phone.Mute = false;

        // One source of truth (test build B): the phone shows exactly the last link.state the PC
        // sent, in any order of changes, across a cut and resume, and nothing before the first.
        var (pcEndL, phoneEndL) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEndL);
        var shellL = new CompanionClient { PcId = "pc1" };
        await shellL.ConnectAsync(pcEndL, "test", Timeout(5), resume: client);
        check(phone.Shown is null, "the phone shows no PC link state before the PC sends one");
        var states = new (string Route, bool Adb, string? Via)[]
        {
            ("usb", true, "cable"), ("direct", false, null), ("direct", true, "wireless"), ("wifi", true, "wireless"),
        };
        var orders = 0;
        var allMatch = true;
        foreach (var order in Permutations(states))
        {
            orders++;
            foreach (var st in order)
            {
                await client.SendLinkStateAsync(st.Route, st.Adb, st.Via, Timeout(5));
            }
            await WaitFor(() => phone.Shown == order[^1], 2000);
            allMatch &= phone.Shown == order[^1];
        }
        check(allMatch, $"in all {orders} orders of route/ADB changes the phone ends on what the PC sent last");
        var (pcEndL2, phoneEndL2) = Duplex.Pair();
        phoneEndL.Cut();
        await client.SendLinkStateAsync("usb", false, null, Timeout(5)); // sent while no transport: kept, replayed
        _ = phone.ServeAsync(phoneEndL2);
        var shellL2 = new CompanionClient { PcId = "pc1" };
        await shellL2.ConnectAsync(pcEndL2, "test", Timeout(5), resume: client);
        await WaitFor(() => phone.Shown == ("usb", false, null), 3000);
        check(phone.Shown == ("usb", false, null), "a change sent while the route was down reaches the phone on the resume");

        // The phone rebooted: it no longer holds the session, so the resume is refused cleanly.
        phone.Reboot();
        var (pcEnd4, phoneEnd4) = Duplex.Pair();
        _ = phone.ServeAsync(phoneEnd4);
        var fresh = new CompanionClient { PcId = "pc1" };
        var hello4 = await fresh.ConnectAsync(pcEnd4, "test", Timeout(5), resume: client);
        fresh.MarkAdopted();
        check(!hello4.Resumed && fresh.HasLinkSession && fresh.ResumedInto is null,
            "after a phone reboot the resume is refused and a fresh session starts (pages see a reconnect)");
        await fresh.SmsSendAsync("555", "after reboot", Timeout(5));
        check(phone.Handled("sms.send").Count == all.Count + 1, "...and it works");
        fresh.Dispose();
        client.Dispose();

        // A v32 transport that dies before the connection is installed fails its requests at once
        // (as below v32) instead of waiting for a resume nobody will attempt.
        var (pcEnd7, phoneEnd7) = Duplex.Pair();
        var quiet = new FakeLinkPhone();
        _ = quiet.ServeAsync(phoneEnd7);
        var early = new CompanionClient { PcId = "pc1" };
        var earlyLost = 0;
        early.TransportLost += () => earlyLost++;
        await early.ConnectAsync(pcEnd7, "test", Timeout(5));
        quiet.Mute = true;
        var stranded = early.SmsSendAsync("555", "early", Timeout(20));
        await Task.Delay(50);
        phoneEnd7.Cut();
        var strandedFast = await Task.WhenAny(stranded, Task.Delay(3000)) == stranded && stranded.IsFaulted;
        check(strandedFast && earlyLost == 0, "a v32 link lost before it was installed fails its requests at once");
        early.Dispose();

        // An old v31 phone: exactly the old behaviour.
        var old = new FakeLinkPhone { Version = 31 };
        var (pcEnd5, phoneEnd5) = Duplex.Pair();
        _ = old.ServeAsync(phoneEnd5);
        var oldClient = new CompanionClient { PcId = "pc1" };
        var oldLost = 0;
        oldClient.TransportLost += () => oldLost++;
        var hello5 = await oldClient.ConnectAsync(pcEnd5, "test", Timeout(5));
        check(hello5.V == 31 && !oldClient.HasLinkSession, "a v31 phone negotiates 31: no session, no numbering");
        var pending = oldClient.SmsSendAsync("555", "x", Timeout(20));
        old.Mute = true;
        await Task.Delay(50);
        phoneEnd5.Cut();
        var failedFast = await Task.WhenAny(pending, Task.Delay(3000)) == pending && pending.IsFaulted;
        check(failedFast && oldLost == 0, "...and a cut fails its requests at once, as before (no linger)");
        check(old.SawSeq == false, "...and it never sees seq/ack fields");
        await oldClient.SendLinkStateAsync("usb", true, "cable", Timeout(5));
        check(old.LinkStates == 0, "...and no link.state: an older phone keeps its own view");

        // The engine switched off: the PC offers 31 even to a v32 phone.
        var (pcEnd6, phoneEnd6) = Duplex.Pair();
        _ = new FakeLinkPhone().ServeAsync(phoneEnd6);
        var off = new CompanionClient { PcId = "pc1", MaxVersion = 31 };
        var hello6 = await off.ConnectAsync(pcEnd6, "test", Timeout(5));
        check(hello6.V == 31 && !off.HasLinkSession, "with the new engine off, a v32 phone is spoken to as v31");
        off.Dispose();
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    private static CancellationToken Timeout(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static async Task WaitFor(Func<bool> condition, int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms) await Task.Delay(20);
    }
}

/// <summary>The phone's side of a v32 session, as SocketServer.kt implements it.</summary>
sealed class FakeLinkPhone
{
    public int Version { get; init; } = 32;
    public volatile bool Mute;
    public volatile string PhoneId = "phone1";

    /// <summary>What the phone's screens show from the PC's link.state, or null before the PC said anything.</summary>
    public (string Route, bool Adb, string? Via)? Shown;
    public int LinkStates;
    public bool SawSeq;

    private sealed class Session(string id)
    {
        public readonly LinkSession Link = new(id);
        public readonly object Lock = new();
        public Duplex? Transport;
    }

    private readonly Dictionary<string, Session> _sessions = [];
    private readonly List<(string Type, string Body)> _handled = [];
    private Session? _current;

    public List<string> Handled(string type)
    {
        lock (_handled) return _handled.Where(h => h.Type == type).Select(h => h.Body).ToList();
    }

    public void Reboot()
    {
        lock (_sessions) { _sessions.Clear(); _current = null; }
    }

    public void SendEvent(string text) =>
        Send(_current, Envelope.Create(MessageType.ClipboardChanged, new JsonObject { ["text"] = text }));

    private void Send(Session? session, Envelope envelope)
    {
        if (session is null) return;
        lock (session.Lock)
        {
            string json;
            if (Version >= 32)
            {
                var seq = session.Link.Stamp();
                json = (envelope with { Seq = seq, Ack = session.Link.Received }).ToJson();
                session.Link.Keep(seq, json);
                session.Link.AckSent(session.Link.Received);
            }
            else json = envelope.ToJson();
            if (session.Transport is { } t && !Mute) t.WriteFrame(json);
        }
    }

    public async Task ServeAsync(Duplex transport)
    {
        var helloJson = await transport.ReadFrameAsync();
        if (helloJson is null) return;
        var hello = Envelope.Parse(helloJson);
        var negotiated = Math.Min((int?)hello.Payload["maxV"] ?? 0, Version);
        Session session;
        var resumed = false;
        lock (_sessions)
        {
            var offer = negotiated >= 32 ? hello.Payload["link"] as JsonObject : null;
            var id = (string?)offer?["session"];
            var pcRecv = (long?)offer?["recv"] ?? 0;
            var pcKeep = (long?)offer?["keep"] ?? 1;
            if (id is not null && _sessions.TryGetValue(id, out var known) && pcKeep <= known.Link.Received + 1 &&
                known.Link.ReplayAfter(pcRecv) is not null)
            {
                session = known;
                resumed = true;
            }
            else
            {
                session = new Session(Guid.NewGuid().ToString("N"));
                if (negotiated >= 32) _sessions[session.Link.Id] = session;
            }
            _current = session;
        }
        lock (session.Lock)
        {
            var old = session.Transport;
            session.Transport = transport;
            old?.Cut(); // the PC moved the session: the old transport goes
            var payload = new JsonObject { ["app"] = "linc-android", ["appVersion"] = "sim", ["v"] = negotiated, ["sessionToken"] = "tok" };
            if (negotiated >= 32)
            {
                payload["link"] = new JsonObject { ["session"] = session.Link.Id, ["recv"] = session.Link.Received, ["resumed"] = resumed, ["id"] = PhoneId, ["gen"] = 0 };
            }
            transport.WriteFrame(Envelope.Create(MessageType.Hello, payload, replyTo: hello.Id).ToJson());
            if (resumed)
            {
                var offer = (JsonObject)hello.Payload["link"]!;
                foreach (var frame in session.Link.ReplayAfter((long?)offer["recv"] ?? 0)!) transport.WriteFrame(frame);
            }
        }
        while (true)
        {
            var json = await transport.ReadFrameAsync();
            if (json is null) break;
            if (Mute) continue; // a half-open link: frames vanish
            var message = Envelope.Parse(json);
            if (message.Seq is not null || message.Ack is not null) SawSeq = true;
            if (message.Type == MessageType.LinkAck)
            {
                lock (session.Lock)
                {
                    if ((long?)message.Payload["ack"] is { } a) session.Link.OnAck(a);
                }
                if ((bool?)message.Payload["echo"] == true)
                {
                    lock (session.Lock)
                    {
                        var ack = Envelope.Create(MessageType.LinkAck, new JsonObject { ["ack"] = session.Link.Received });
                        session.Link.AckSent(session.Link.Received);
                        if (session.Transport == transport) transport.WriteFrame(ack.ToJson());
                    }
                }
                continue;
            }
            lock (session.Lock)
            {
                if (message.Ack is { } ack) session.Link.OnAck(ack);
                if (message.Seq is { } seq && session.Link.Accept(seq, 0) == FrameArrival.Duplicate) continue;
            }
            if (message.Type == MessageType.LinkNoop) continue; // only takes up its number
            if (message.Type == MessageType.LinkState)
            {
                // As the phone does (CompanionStateHolder.PcLink.from): shown exactly, or not at all.
                var route = (string?)message.Payload["route"];
                var adb = (bool?)message.Payload["adb"];
                Shown = route is "usb" or "wifi" or "direct" && adb is { } a
                    ? (route, a, a ? (string?)message.Payload["adbVia"] : null)
                    : null;
                LinkStates++;
                continue;
            }
            lock (_handled) _handled.Add((message.Type, (string?)message.Payload["body"] ?? ""));
            Send(session, Envelope.Create(MessageType.Ok, replyTo: message.Id));
        }
        lock (session.Lock)
        {
            if (session.Transport == transport) session.Transport = null; // detached: kept for a resume
        }
    }
}

/// <summary>One end of an in-memory link. <see cref="Cut"/> ends both directions, losing whatever was in flight.</summary>
sealed class Duplex : Stream
{
    private readonly Channel<byte[]> _in;
    private readonly Channel<byte[]> _out;
    private Duplex? _peer;
    private byte[] _pending = [];
    private int _offset;

    private Duplex(Channel<byte[]> @in, Channel<byte[]> @out) { _in = @in; _out = @out; }

    public static (Duplex A, Duplex B) Pair()
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        var a = new Duplex(ba, ab);
        var b = new Duplex(ab, ba);
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public void Cut()
    {
        _in.Writer.TryComplete();
        _out.Writer.TryComplete();
        while (_in.Reader.TryRead(out _)) { }
        while (_out.Reader.TryRead(out _)) { }
    }

    public void WriteFrame(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        _out.Writer.TryWrite(frame);
    }

    public Task<string?> ReadFrameAsync() => Framing.ReadAsync(this, CancellationToken.None);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset >= _pending.Length)
        {
            try
            {
                if (!await _in.Reader.WaitToReadAsync(ct)) return 0;
                if (!_in.Reader.TryRead(out var next)) return 0;
                _pending = next;
                _offset = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
        var n = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (!_out.Writer.TryWrite(buffer.ToArray())) throw new IOException("the link was cut");
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        Cut();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
}
