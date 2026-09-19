using System;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public interface IPcMirrorService : IDisposable
{
    /// <summary>Begin answering the phone's <c>pc.mirror.*</c> / <c>pc.displays.get</c> requests.</summary>
    void Attach();

    /// <summary>True while a stream is being served to the phone.</summary>
    bool IsStreaming { get; }

    /// <summary>Displays the phone may choose between (<c>pc.displays</c>).</summary>
    IReadOnlyList<PcDisplay> Displays { get; }

    /// <summary>
    /// Begin streaming a display at the phone-chosen quality (<paramref name="fps"/> and
    /// <paramref name="bitrate"/>; pass 0 for either to use the default). Returns the stream's
    /// physical size, which the desktop replies with and the phone uses to size its surface.
    /// </summary>
    Task<(int Width, int Height)> StartAsync(int displayIndex, int fps, int bitrate, CancellationToken ct);

    Task StopAsync();
}

/// <summary>
/// Serves the reverse mirror to the phone over the pc-video channel (5) — protocol v14,
/// M05, D-029. Turns <see cref="PcMirrorSource"/>'s encoded frames into framed packets.
///
/// <para><b>Channel 5 is the first desktop-served stream</b>, but it needs no new transport
/// work: the phone dials back in both directions already. Over LAN it connects to the
/// desktop's TLS listener the same way channels 2 and 3 do; over the USB cable it dials the
/// port D-022's <c>adb reverse</c> maps onto that same listener. So one mechanism —
/// <c>channel.open</c> then <see cref="ITlsTransportService.AwaitChannelAsync"/> — covers
/// both transports, and the waiter is armed <i>before</i> the request is sent to avoid the
/// race where the phone dials back faster than the local task is registered.</para>
///
/// <para><b>A viewer never joins mid-GOP.</b> The channel is opened fresh for each session
/// and the encoder's first output is always an IDR carrying SPS/PPS, so the phone can decode
/// from the first packet it receives. (The encoder also repeats SPS/PPS with every later
/// keyframe, which is what makes recovery after a dropped packet possible.)</para>
/// </summary>
public sealed class PcMirrorService(
    IConnectionManager connection,
    IPcInputService input,
    ILogService log) : IPcMirrorService
{
    private const int VideoChannel = 5;
    private const int FramesPerSecond = 30;
    private const int Bitrate = 8_000_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PcMirrorSource? _source;
    private PcMirrorSender? _sender;
    private Stream? _channel;
    private CancellationTokenSource? _streaming;
    private IReadOnlyList<PcDisplay>? _displays;
    private PcDisplay? _activeDisplay;
    private bool _disposed;
    private bool _subscribed;

    // What is currently streaming, so a repeated pc.mirror.start can be recognised as the
    // phone's retry rather than a request for a different stream.
    private (int Display, int Fps, int Bitrate)? _active;
    private int _starting;

    public bool IsStreaming => _source is not null;

    /// <summary>Begin answering the phone's <c>pc.mirror.*</c> / <c>pc.displays.get</c> requests.</summary>
    public void Attach()
    {
        if (_subscribed) return;
        _subscribed = true;
        connection.CompanionMessageReceived += OnCompanionMessage;
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.PcDisplaysGet:
                _ = SendDisplaysAsync();
                break;

            case MessageType.PcMirrorStart:
                // "virtual" is M06's extend display; until then only real displays exist.
                int displayIndex = (int?)envelope.Payload["displayId"] ?? 0;
                int fps = (int?)envelope.Payload["fps"] ?? 0;         // 0 = use the default
                int bitrate = (int?)envelope.Payload["bitrate"] ?? 0; // (phone picks quality, v14)
                _ = StartFromPhoneAsync(displayIndex, fps, bitrate);
                break;

            case MessageType.PcMirrorKeyframe:
                // The phone lost sync (dropped packets, a decoder restart). Give it an IDR
                // now rather than making it wait out the GOP. (v19)
                _source?.RequestKeyFrame();
                break;

            case MessageType.PcMirrorStop:
                _ = StopAsync();
                break;

            case MessageType.PcInput:
                RouteInput(envelope.Payload);
                break;
        }
    }

    /// <summary>
    /// Apply a phone input event to whichever display is being mirrored. Ignored when nothing
    /// is streaming — an input with no active view has no coordinate space to land in.
    /// </summary>
    private void RouteInput(JsonObject payload)
    {
        var display = _activeDisplay;
        if (display is null) return;

        var target = new DisplayRect(display.Left, display.Top, display.Width, display.Height);
        var bounds = VirtualDesktopBounds();
        try { input.Apply(payload, target, bounds); }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"Couldn't apply phone input: {ex.Message}"); }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static DisplayRect VirtualDesktopBounds() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    // ---- text-field focus (drives the phone's keyboard) ---------------------

    /// <summary>
    /// While streaming, watch whether a text field is focused on the PC and tell the phone
    /// (<c>pc.textfocus</c>), so it can raise/lower its soft keyboard. The signal is the
    /// caret: <c>GetGUIThreadInfo</c> for the foreground window's thread reports a non-zero
    /// <c>hwndCaret</c> exactly while a text caret is showing — pure user32, no UI Automation
    /// dependency, and it works across Win32, WinUI and browsers alike.
    /// </summary>
    private void WatchTextFocus(CancellationToken token)
    {
        bool last = false;
        while (!token.IsCancellationRequested)
        {
            bool focused = CaretVisible();
            if (focused != last)
            {
                last = focused;
                try
                {
                    connection.SendToPhoneAsync(Envelope.Create(MessageType.PcTextFocus,
                        new JsonObject { ["focused"] = focused }), token).GetAwaiter().GetResult();
                }
                catch { /* link hiccup; state resyncs on the next change */ }
            }
            token.WaitHandle.WaitOne(400);
        }
    }

    private static bool CaretVisible()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        uint thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) && info.hwndCaret != IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int l, t, r, b;
    }

    private async Task SendDisplaysAsync()
    {
        try
        {
            var list = new JsonArray();
            foreach (var display in Displays)
                list.Add(new JsonObject
                {
                    ["id"] = display.Index,
                    ["name"] = display.Name,
                    ["width"] = display.Width,
                    ["height"] = display.Height,
                    ["primary"] = display.Primary,
                });

            await connection.SendToPhoneAsync(
                Envelope.Create(MessageType.PcDisplays, new JsonObject { ["displays"] = list }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't tell the phone about this PC's displays: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle the phone's <c>pc.mirror.start</c>.
    ///
    /// <para><b>The phone retries, and it must be allowed to.</b> The Mirror screen re-asks
    /// every few seconds until frames flow, because a link that was mid-reconnect would
    /// otherwise leave it on "Loading…" forever. But starting a stream is not quick — the
    /// channel dial-back alone is allowed eight seconds, and over Wi-Fi it often uses several
    /// of them — so those retries arrive while the first start is still running. Treating each
    /// one as a fresh request tore down a stream that was seconds from working and started
    /// again from nothing, which over a slow link is a loop that never converges: the mirror
    /// appears to hang, and the desktop builds and destroys an encoder every three seconds.</para>
    ///
    /// <para>So a start that arrives while another is in flight is ignored, and a start that
    /// asks for exactly what is already streaming is ignored too. Only a genuine change of
    /// display or quality restarts the stream.</para>
    /// </summary>
    private async Task StartFromPhoneAsync(int displayIndex, int fps, int bitrate)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) == 1)
        {
            log.Log(LogLevel.Info, "PC mirror: already starting, ignoring the phone's retry");
            return;
        }

        try
        {
            if (_source is not null && _active == (displayIndex, fps, bitrate))
            {
                // Already giving the phone exactly this. Nudge a keyframe in case its
                // decoder is the reason it asked again, and leave the stream alone.
                log.Log(LogLevel.Info, "PC mirror: already streaming this display, sending a keyframe");
                _source.RequestKeyFrame();
                return;
            }

            await StartAsync(displayIndex, fps, bitrate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The phone asked and cannot see this failure otherwise, so say why in the log.
            log.Log(LogLevel.Warn, $"Couldn't start mirroring this PC: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _starting, 0); }
    }

    public IReadOnlyList<PcDisplay> Displays => _displays ??= PcScreenCapture.Enumerate();

    public async Task<(int, int)> StartAsync(int displayIndex, int fps, int bitrate, CancellationToken ct)
    {
        // 0 means "phone didn't ask" — fall back to the defaults. Clamp to sane bounds.
        var useFps = fps > 0 ? Math.Clamp(fps, 5, 60) : FramesPerSecond;
        var useBitrate = bitrate > 0 ? Math.Clamp(bitrate, 500_000, 20_000_000) : Bitrate;

        await _gate.WaitAsync(ct);
        try
        {
            if (_source is not null) await StopCoreAsync();

            // Build the capture+encoder on a thread-pool (MTA) thread, NEVER inline. The companion
            // receive loop resumes on the WinUI UI thread (STA), so creating the Media Foundation
            // encoder here would place its COM objects in the UI apartment — and then every
            // ProcessInput/GetEvent from the MTA pump/feed threads marshals back onto the UI
            // thread, burying the message pump at 30 fps and hanging the whole app (AppHangB1).
            // Keeping the encoder in the MTA lets those calls run directly on the worker threads.
            var source = await Task.Run(
                () => new PcMirrorSource(displayIndex, useFps, useBitrate, log), ct);

            Stream channel;
            try
            {
                channel = await connection.OpenPhoneDialledChannelAsync(VideoChannel, ct);

                // Tell the phone what it is about to receive before any packets arrive.
                var config = new JsonObject
                {
                    ["codec"] = "h264",
                    ["width"] = source.Width,
                    ["height"] = source.Height,
                    ["displayId"] = displayIndex,
                };
                await Framing.WriteAsync(channel, config.ToJsonString(), ct);
            }
            catch
            {
                // The dial-back can time out or the phone can vanish mid-handshake — common
                // enough on Wi-Fi. The capture and the hardware encoder are already built at
                // this point, so they must be released here or every failed attempt leaks a
                // GPU encoder session and the next attempt is slower than the last.
                await Task.Run(source.Dispose);
                throw;
            }

            _source = source;
            _channel = channel;
            _activeDisplay = displayIndex < Displays.Count ? Displays[displayIndex] : null;
            _active = (displayIndex, useFps, useBitrate);
            _streaming = new CancellationTokenSource();

            // Frames leave through the sender's own thread; the encoder pump must never wait
            // on the socket (see PcMirrorSender).
            var sender = new PcMirrorSender(channel, source.RequestKeyFrame, log);
            sender.Ended += reason =>
            {
                log.Log(LogLevel.Info, $"PC mirror stream ended: {reason}");
                _ = Task.Run(StopAsync);
            };
            _sender = sender;

            source.FrameEncoded += OnFrameEncoded;
            await Task.Run(source.Start, ct); // start streaming on an MTA thread too (see above)

            var focusToken = _streaming.Token;
            new Thread(() => WatchTextFocus(focusToken))
                { IsBackground = true, Name = "linc-mirror-textfocus" }.Start();

            log.Log(LogLevel.Info,
                $"Mirroring this PC to the phone: {source.Width}x{source.Height} on display {displayIndex}");
            return (source.Width, source.Height);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Hands one encoded frame to the sender. Runs on the source's pump thread, so it only
    /// ever queues — it must not touch the socket (see <see cref="PcMirrorSender"/>).
    /// </summary>
    private void OnFrameEncoded(EncodedFrame frame)
    {
        if (_streaming?.IsCancellationRequested != false) return;
        _sender?.Enqueue(frame);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_source is null) return;

        _streaming?.Cancel();
        _source.FrameEncoded -= OnFrameEncoded;
        var source = _source;
        _source = null;
        _activeDisplay = null;
        _active = null;

        // Stop the writer before the channel it writes to goes away.
        _sender?.Dispose();
        _sender = null;

        await Task.Run(source.Dispose); // dispose the encoder off the UI thread (its MTA home)

        if (_channel is not null)
        {
            try { await _channel.DisposeAsync(); } catch { }
            _channel = null;
        }

        _streaming?.Dispose();
        _streaming = null;
        log.Log(LogLevel.Info, "Stopped mirroring this PC to the phone");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
    }
}
