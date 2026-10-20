using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using Linc.Desktop.Protocol;
using static Linc.Desktop.Services.Mf;

namespace Linc.Desktop.Services;

/// <summary>
/// How a stream runs. The defaults are PC Remote's plain stream (v20). Play everywhere (v26,
/// D-096) captures the virtual cable by id, stamps every packet with the PC-clock time it was
/// captured, answers the phone's clock probes on the channel's back direction, and hands each
/// block of raw sound to <paramref name="Tap"/> for the PC's own speakers.
/// </summary>
public sealed record AudioStreamOptions(string? LoopbackDeviceId = null, bool Clock = false, Action<SoundBlock>? Tap = null);

/// <summary>
/// Plays the PC's sound on the phone while PC Remote is open (v20 <c>pc.audio.start</c>,
/// pc-audio channel 6, D-067) — Windows App's "play remote audio on this device".
///
/// <para><b>Pipeline.</b> WASAPI loopback on the default render device (whatever the PC is
/// playing, mixed) → 16-bit stereo PCM → Windows' own AAC-LC encoder MFT at 128 kb/s → one
/// raw AAC frame (1024 samples, ~21 ms) per packet, framed exactly like pc-video (12-byte
/// header, <see cref="VideoFraming"/>). AAC because Windows ships an encoder and every Android
/// phone ships a decoder, so neither end takes on a codec library; 128 kb/s is ~2% of a
/// healthy mirror's video budget.</para>
///
/// <para><b>Live, never late.</b> AAC frames decode independently, so unlike video a lost or
/// discarded packet costs one 21 ms blip and nothing after it. The writer therefore keeps at
/// most <see cref="MaxQueuedPackets"/> (~200 ms) queued and drops the oldest beyond that:
/// sound that arrives half a second after the picture is worse than a click.</para>
///
/// <para>Loopback delivers nothing while the PC is silent, so silence costs no bandwidth.</para>
///
/// <para><b>Timestamps.</b> Each packet carries the PC-clock time (µs, QPC) its first sample was
/// captured, from WASAPI's own capture stamp. A block that straddles a silence is padded and sent
/// at once, so a timestamp never spans a gap. With <see cref="AudioStreamOptions.Clock"/>, the
/// phone writes 16-byte probes up the channel (<c>"LCLK"</c>, 0, its time) and each is answered
/// with a config-flagged packet of three longs: the probe's time, when it arrived, when the
/// answer left.</para>
/// </summary>
public sealed class PcAudioStreamer : IDisposable
{
    public const int Channel = 6;
    private const int MaxQueuedPackets = 10;
    private const int FramesPerAac = 1024;
    private const int Bitrate = 128_000;

    private readonly Stream _channel;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly Thread _capture;
    private readonly Thread _writer;
    private readonly Thread? _clockReader;
    private readonly object _writeLock = new();
    private readonly AudioStreamOptions _options;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Queue<long> _blockTimes = new(); // capture time of each block handed to the encoder, oldest first
    private bool _disposed;

    /// <summary>The probe a phone writes up the channel: "LCLK".</summary>
    public const int ClockMagic = 0x4C434C4B;
    public const int ClockProbeBytes = 16;
    public const int ClockReplyBytes = 24;

    // Capture and send timings.
    private const int DiscontinuityUs = 5_000; // a packet this far off the timeline starts it afresh
    private const int EaseDivisor = 64; // the timeline moves this fraction of the way to each packet's stamp
    private const int LateFlushUs = 30_000; // send a part-filled block once the PC has been quiet this long
    private const int OrderlyStopMs = 1000; // how long Dispose waits for each thread
    private const int WriterWaitMs = 250; // the writer's idle wake-up
    private const int EncoderMinBufferBytes = 8192; // the smallest output buffer handed to the AAC encoder

    /// <summary>Raised once when the stream ends (the phone hung up, or capture failed).</summary>
    public event Action<string>? Ended;

    public int SampleRate { get; }
    public int Channels => 2;

    private PcAudioStreamer(Stream channel, int sampleRate, ILogService log, AudioStreamOptions options)
    {
        _channel = channel;
        _log = log;
        _options = options;
        SampleRate = sampleRate;
        _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "linc-audio-capture" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "linc-audio-writer" };
        if (options.Clock)
            _clockReader = new Thread(ClockLoop) { IsBackground = true, Name = "linc-audio-clock", Priority = ThreadPriority.AboveNormal };
    }

    public AudioStreamOptions Options => _options;

    /// <summary>
    /// The mix rate of the default output device. AAC is encoded at that rate (the MFT takes
    /// 44.1 and 48 kHz), so no resampler is needed; anything else is refused up front.
    /// </summary>
    public static int ProbeSampleRate(string? deviceId = null)
    {
        var client = Wasapi.OpenLoopback(deviceId, out var format);
        try
        {
            if (format.SampleRate is not (44100 or 48000))
                throw new LincException($"This PC's sound runs at {format.SampleRate} Hz, which PC audio can't stream yet (44.1 or 48 kHz).");
            return format.SampleRate;
        }
        finally { Marshal.ReleaseComObject(client); }
    }

    /// <summary>Begin streaming onto an already-open channel, after its config frame is written.</summary>
    public static PcAudioStreamer Start(Stream channel, int sampleRate, ILogService log, AudioStreamOptions? options = null)
    {
        var streamer = new PcAudioStreamer(channel, sampleRate, log, options ?? new AudioStreamOptions());
        streamer._capture.Start();
        streamer._writer.Start();
        streamer._clockReader?.Start();
        return streamer;
    }

    public static JsonObject Config(int sampleRate, bool clock = false)
    {
        var config = new JsonObject
        {
            ["codec"] = "aac",
            ["sampleRate"] = sampleRate,
            ["channels"] = 2,
        };
        // v26: timestamps are PC-clock capture times and probes are answered (a v25 phone never sends one).
        if (clock) config["clock"] = true;
        return config;
    }

    // ---- capture + encode (one MTA thread owns both COM objects) ----------

    private void CaptureLoop()
    {
        var token = _stopping.Token;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        AacEncoder? encoder = null;
        try
        {
            client = Wasapi.OpenLoopback(_options.LoopbackDeviceId, out var format);
            capture = Wasapi.StartCapture(client);
            encoder = new AacEncoder(format.SampleRate, Bitrate);

            var pcm = new short[FramesPerAac * 2];
            int filled = 0;
            long blockStartUs = 0;
            // The capture timeline, µs: continuous across packets so consecutive blocks follow on
            // exactly (WASAPI's per-packet stamps jitter by a little, and a jump between blocks
            // reads as a gap to every speaker), pulled gently towards the stamps so it keeps
            // following the device's own clock.
            double timelineUs = 0;
            bool timelineSet = false;
            double frameUs = 1_000_000.0 / format.SampleRate;

            void Flush()
            {
                if (filled == 0) return;
                Array.Clear(pcm, filled * 2, pcm.Length - filled * 2); // a block cut short by silence is padded
                _options.Tap?.Invoke(new SoundBlock((short[])pcm.Clone(), blockStartUs));
                lock (_blockTimes) _blockTimes.Enqueue(blockStartUs);
                foreach (var aac in encoder.Encode(pcm))
                    Enqueue(aac, NextBlockTime());
                filled = 0;
            }

            while (!token.IsCancellationRequested)
            {
                while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc), "GetBuffer");
                    try
                    {
                        bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        bool stampOk = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0 && qpc != 0;
                        long packetUs = stampOk ? PcClock.QpcHnsToUs(qpc) : PcClock.NowUs();
                        // A packet that doesn't follow on (the PC was silent in between, or
                        // WASAPI says so): finish the block being filled, and start the timeline
                        // afresh from this packet's stamp.
                        if (!timelineSet || (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 || Math.Abs(packetUs - timelineUs) > DiscontinuityUs)
                        {
                            Flush();
                            timelineUs = packetUs;
                            timelineSet = true;
                        }
                        else timelineUs += (packetUs - timelineUs) / EaseDivisor;
                        for (uint f = 0; f < frames; f++)
                        {
                            if (filled == 0) blockStartUs = (long)timelineUs;
                            Wasapi.ReadStereo(data, f, format, silent, out short left, out short right);
                            pcm[filled * 2] = left;
                            pcm[filled * 2 + 1] = right;
                            timelineUs += frameUs;
                            if (++filled == FramesPerAac) Flush();
                        }
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
                // The PC went quiet mid-block (loopback then sends nothing at all): send what
                // there is now, rather than holding the end of a sound until the next one starts.
                if (filled > 0 && PcClock.NowUs() - timelineUs > LateFlushUs) Flush();
                token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _log.Log(LogLevel.Warn, $"PC audio stopped: {ex.Message}");
            Ended?.Invoke(ex.Message);
        }
        catch { /* shutting down */ }
        finally
        {
            encoder?.Dispose();
            try { client?.Stop(); } catch { }
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>
    /// The capture time for the encoder's next output frame. The encoder gives one frame per
    /// block, in order, after a fixed start-up delay; that constant offset is part of what the
    /// phone's alignment measures, so it needs no modelling here.
    /// </summary>
    private long NextBlockTime()
    {
        lock (_blockTimes) return _blockTimes.Count > 0 ? _blockTimes.Dequeue() : PcClock.NowUs();
    }

    private void Enqueue(byte[] aac, long timestampUs)
    {
        var packet = new byte[VideoFraming.HeaderBytes + aac.Length];
        VideoFraming.WriteHeader(packet, timestampUs, config: false, keyframe: false, aac.Length);
        aac.CopyTo(packet, VideoFraming.HeaderBytes);
        lock (_queue)
        {
            _queue.Enqueue(packet);
            if (_queue.Count > MaxQueuedPackets)
            {
                _queue.Dequeue();
                return; // the semaphore already counts the packet that replaced it
            }
        }
        _pending.Release();
    }

    private void WriteLoop()
    {
        var token = _stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_pending.Wait(WriterWaitMs, token)) continue;
                byte[] packet;
                lock (_queue)
                {
                    if (_queue.Count == 0) continue;
                    packet = _queue.Dequeue();
                }
                lock (_writeLock)
                {
                    _channel.Write(packet, 0, packet.Length);
                    _channel.Flush();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // Which socket error, and how long in: "aborted by the software in your host
            // machine" a second after every start is the field report this has to explain.
            var code = (ex.InnerException as System.Net.Sockets.SocketException)?.SocketErrorCode.ToString() ?? ex.GetType().Name;
            if (!token.IsCancellationRequested) Ended?.Invoke($"{ex.Message} [{code}, writing, {(DateTime.UtcNow - _startedUtc).TotalMilliseconds:0} ms in]");
        }
    }

    /// <summary>
    /// Answer the phone's clock probes (v26). Its own thread, reading the channel's back
    /// direction, so the arrival time is taken the instant the probe is read and the answer
    /// goes out straight away, ahead of any queued sound.
    /// </summary>
    private void ClockLoop()
    {
        var token = _stopping.Token;
        var probe = new byte[ClockProbeBytes];
        var reply = new byte[VideoFraming.HeaderBytes + ClockReplyBytes];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = 0;
                while (read < probe.Length)
                {
                    int n = _channel.Read(probe, read, probe.Length - read);
                    if (n == 0)
                    {
                        _log.Log(LogLevel.Info, $"Play everywhere: the phone closed the sound channel ({(DateTime.UtcNow - _startedUtc).TotalMilliseconds:0} ms in)");
                        return; // the phone closed its side
                    }
                    read += n;
                }
                long arrived = PcClock.NowUs();
                if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(probe) != ClockMagic) continue;
                long sent = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(probe.AsSpan(8));
                lock (_writeLock)
                {
                    long leaving = PcClock.NowUs();
                    VideoFraming.WriteHeader(reply, arrived, config: true, keyframe: false, ClockReplyBytes);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(12), sent);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(20), arrived);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(28), leaving);
                    _channel.Write(reply, 0, reply.Length);
                    _channel.Flush();
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // The writer notices a dead channel too; this only says why the clock stopped.
            _log.Log(LogLevel.Info, $"Play everywhere: the phone's clock probes stopped ({ex.GetType().Name}: {ex.Message}, {(DateTime.UtcNow - _startedUtc).TotalMilliseconds:0} ms in)");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        _capture.Join(OrderlyStopMs);
        _writer.Join(OrderlyStopMs);
        try { _channel.Dispose(); } catch { }
        _clockReader?.Join(OrderlyStopMs); // its blocking read ends when the channel closes
        _pending.Dispose();
        _stopping.Dispose();
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new LincException($"PC audio: {what} failed (0x{hr:X8}).");
    }

    // ---- AAC encoder (Windows' in-box MFT, synchronous) --------------------

    private sealed class AacEncoder : IDisposable
    {
        private static readonly Guid CLSID_AACMFTEncoder = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        private const uint MF_E_NOTACCEPTING = 0xC00D36B5;
        private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

        private readonly IMFTransform _transform;
        private readonly int _sampleRate;
        private long _time;

        public AacEncoder(int sampleRate, int bitrate)
        {
            _sampleRate = sampleRate;
            MFStartup(0x00020070, 0);
            var type = Type.GetTypeFromCLSID(CLSID_AACMFTEncoder)
                ?? throw new LincException("This PC has no AAC audio encoder.");
            _transform = (IMFTransform)(Activator.CreateInstance(type)
                ?? throw new LincException("This PC's AAC audio encoder couldn't be started."));

            // An encoder takes its output type first; the input type is then checked against it.
            Check(MFCreateMediaType(out IMFMediaType output), "MFCreateMediaType");
            SetGuid(output, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(output, MF_MT_SUBTYPE, MFAudioFormat_AAC);
            SetInt(output, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetInt(output, MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
            SetInt(output, MF_MT_AUDIO_NUM_CHANNELS, 2);
            SetInt(output, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bitrate / 8);
            SetInt(output, MF_MT_AAC_PAYLOAD_TYPE, 0); // raw AAC: the phone is given the config itself
            Check(_transform.SetOutputType(0, output, 0), "SetOutputType");
            Marshal.ReleaseComObject(output);

            Check(MFCreateMediaType(out IMFMediaType input), "MFCreateMediaType");
            SetGuid(input, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(input, MF_MT_SUBTYPE, MFAudioFormat_PCM);
            SetInt(input, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetInt(input, MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
            SetInt(input, MF_MT_AUDIO_NUM_CHANNELS, 2);
            SetInt(input, MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
            SetInt(input, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate * 4);
            Check(_transform.SetInputType(0, input, 0), "SetInputType");
            Marshal.ReleaseComObject(input);

            _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        }

        /// <summary>Encode 1024 interleaved stereo frames; returns any AAC frames that came out.</summary>
        public List<byte[]> Encode(short[] pcm)
        {
            var results = new List<byte[]>();
            int bytes = pcm.Length * 2;
            Check(MFCreateMemoryBuffer(bytes, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
            Check(MFCreateSample(out IMFSample sample), "MFCreateSample");
            try
            {
                Check(buffer.Lock(out IntPtr pointer, out _, out _), "Lock");
                Marshal.Copy(pcm, 0, pointer, pcm.Length);
                buffer.Unlock();
                buffer.SetCurrentLength(bytes);
                sample.AddBuffer(buffer);
                long duration = 10_000_000L * FramesPerAac / _sampleRate;
                sample.SetSampleTime(_time);
                sample.SetSampleDuration(duration);
                _time += duration;

                int hr = _transform.ProcessInput(0, sample, 0);
                if ((uint)hr == MF_E_NOTACCEPTING)
                {
                    Drain(results);
                    hr = _transform.ProcessInput(0, sample, 0);
                }
                Check(hr, "ProcessInput");
                Drain(results);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
            }
            return results;
        }

        private void Drain(List<byte[]> results)
        {
            Check(_transform.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO info), "GetOutputStreamInfo");
            bool provides = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            while (true)
            {
                IMFSample? own = null;
                IMFMediaBuffer? ownBuffer = null;
                if (!provides)
                {
                    Check(MFCreateSample(out own), "MFCreateSample");
                    Check(MFCreateMemoryBuffer((int)Math.Max(info.cbSize, EncoderMinBufferBytes), out ownBuffer), "MFCreateMemoryBuffer");
                    own.AddBuffer(ownBuffer);
                }
                var output = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = own! };
                int hr = _transform.ProcessOutput(0, 1, ref output, out _);
                try
                {
                    if ((uint)hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                    Check(hr, "ProcessOutput");
                    if (output.pSample is null) return;
                    Check(output.pSample.ConvertToContiguousBuffer(out IMFMediaBuffer contiguous), "ConvertToContiguousBuffer");
                    try
                    {
                        Check(contiguous.Lock(out IntPtr pointer, out _, out int length), "Lock");
                        if (length > 0)
                        {
                            var data = new byte[length];
                            Marshal.Copy(pointer, data, 0, length);
                            results.Add(data);
                        }
                        contiguous.Unlock();
                    }
                    finally { Marshal.ReleaseComObject(contiguous); }
                }
                finally
                {
                    if (output.pEvents is not null) Marshal.ReleaseComObject(output.pEvents);
                    if (provides && output.pSample is not null) Marshal.ReleaseComObject(output.pSample);
                    if (ownBuffer is not null) Marshal.ReleaseComObject(ownBuffer);
                    if (own is not null) Marshal.ReleaseComObject(own);
                }
            }
        }

        private static void SetGuid(IMFMediaType type, Guid key, Guid value) =>
            Check(type.SetGUID(ref key, ref value), "SetGUID");

        private static void SetInt(IMFMediaType type, Guid key, int value) =>
            Check(type.SetUINT32(ref key, value), "SetUINT32");

        public void Dispose()
        {
            try { _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
            Marshal.ReleaseComObject(_transform);
            MFShutdown();
        }
    }

    // ---- WASAPI loopback ----------------------------------------------------

    private const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;

    /// <summary>The mix format, reduced to what the sample reader needs.</summary>
    private readonly record struct MixFormat(int SampleRate, int Channels, int BitsPerSample, bool Float, int BlockAlign);

    private static class Wasapi
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
        private const int CLSCTX_ALL = 0x17;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int BufferHns = 1_000_000; // 100 ms of device buffer; we drain every ~5 ms

        /// <summary>Loopback on <paramref name="deviceId"/>, or on the default output when null.</summary>
        public static IAudioClient OpenLoopback(string? deviceId, out MixFormat format)
        {
            var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                IMMDevice device;
                if (deviceId is null)
                    Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device), "GetDefaultAudioEndpoint");
                else
                    Check(enumerator.GetDevice(deviceId, out device), "GetDevice");
                try
                {
                    var iid = IID_IAudioClient;
                    Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object unknown), "Activate");
                    var client = (IAudioClient)unknown;
                    Check(client.GetMixFormat(out IntPtr mix), "GetMixFormat");
                    try
                    {
                        format = ReadFormat(mix);
                        Check(client.Initialize(0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK, BufferHns, 0, mix, IntPtr.Zero),
                            "Initialize");
                    }
                    finally { Marshal.FreeCoTaskMem(mix); }
                    return client;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        public static IAudioCaptureClient StartCapture(IAudioClient client)
        {
            var iid = IID_IAudioCaptureClient;
            Check(client.GetService(ref iid, out object unknown), "GetService");
            Check(client.Start(), "Start");
            return (IAudioCaptureClient)unknown;
        }

        private static MixFormat ReadFormat(IntPtr wfx)
        {
            ushort tag = (ushort)Marshal.ReadInt16(wfx, 0);
            int channels = Marshal.ReadInt16(wfx, 2);
            int rate = Marshal.ReadInt32(wfx, 4);
            int blockAlign = Marshal.ReadInt16(wfx, 12);
            int bits = Marshal.ReadInt16(wfx, 14);
            bool isFloat = tag == 3;
            if (tag == 0xFFFE)
            {
                var sub = Marshal.PtrToStructure<Guid>(wfx + 24);
                isFloat = sub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            }
            return new MixFormat(rate, channels, bits, isFloat, blockAlign);
        }

        /// <summary>Read frame <paramref name="index"/> as 16-bit left/right (extra channels are dropped).</summary>
        public static void ReadStereo(IntPtr data, uint index, MixFormat format, bool silent, out short left, out short right)
        {
            if (silent) { left = right = 0; return; }
            IntPtr frame = data + (int)(index * (uint)format.BlockAlign);
            left = ReadSample(frame, 0, format);
            right = format.Channels > 1 ? ReadSample(frame, 1, format) : left;
        }

        private static short ReadSample(IntPtr frame, int channel, MixFormat format)
        {
            int bytes = format.BitsPerSample / 8;
            IntPtr at = frame + channel * bytes;
            if (format.Float && bytes == 4)
            {
                float f = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at));
                return (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);
            }
            return bytes switch
            {
                2 => Marshal.ReadInt16(at),
                3 => (short)(Marshal.ReadInt16(at, 1)),
                4 => (short)(Marshal.ReadInt32(at) >> 16),
                _ => 0,
            };
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
