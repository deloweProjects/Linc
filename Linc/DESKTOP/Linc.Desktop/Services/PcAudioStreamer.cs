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
    private bool _disposed;

    /// <summary>Raised once when the stream ends (the phone hung up, or capture failed).</summary>
    public event Action<string>? Ended;

    public int SampleRate { get; }
    public int Channels => 2;

    private PcAudioStreamer(Stream channel, int sampleRate, ILogService log)
    {
        _channel = channel;
        _log = log;
        SampleRate = sampleRate;
        _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "linc-audio-capture" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "linc-audio-writer" };
    }

    /// <summary>
    /// The mix rate of the default output device. AAC is encoded at that rate (the MFT takes
    /// 44.1 and 48 kHz), so no resampler is needed; anything else is refused up front.
    /// </summary>
    public static int ProbeSampleRate()
    {
        var client = Wasapi.OpenLoopback(out var format);
        try
        {
            if (format.SampleRate is not (44100 or 48000))
                throw new LincException($"This PC's sound runs at {format.SampleRate} Hz, which PC audio can't stream yet (44.1 or 48 kHz).");
            return format.SampleRate;
        }
        finally { Marshal.ReleaseComObject(client); }
    }

    /// <summary>Begin streaming onto an already-open channel, after its config frame is written.</summary>
    public static PcAudioStreamer Start(Stream channel, int sampleRate, ILogService log)
    {
        var streamer = new PcAudioStreamer(channel, sampleRate, log);
        streamer._capture.Start();
        streamer._writer.Start();
        return streamer;
    }

    public static JsonObject Config(int sampleRate) => new()
    {
        ["codec"] = "aac",
        ["sampleRate"] = sampleRate,
        ["channels"] = 2,
    };

    // ---- capture + encode (one MTA thread owns both COM objects) ----------

    private void CaptureLoop()
    {
        var token = _stopping.Token;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        AacEncoder? encoder = null;
        try
        {
            client = Wasapi.OpenLoopback(out var format);
            capture = Wasapi.StartCapture(client);
            encoder = new AacEncoder(format.SampleRate, Bitrate);

            var pcm = new short[FramesPerAac * 2];
            int filled = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                    try
                    {
                        bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        for (uint f = 0; f < frames; f++)
                        {
                            Wasapi.ReadStereo(data, f, format, silent, out short left, out short right);
                            pcm[filled * 2] = left;
                            pcm[filled * 2 + 1] = right;
                            if (++filled == FramesPerAac)
                            {
                                foreach (var aac in encoder.Encode(pcm))
                                    Enqueue(aac, clock.Elapsed.Ticks / 10);
                                filled = 0;
                            }
                        }
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
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
                if (!_pending.Wait(250, token)) continue;
                byte[] packet;
                lock (_queue)
                {
                    if (_queue.Count == 0) continue;
                    packet = _queue.Dequeue();
                }
                _channel.Write(packet, 0, packet.Length);
                _channel.Flush();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Ended?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        _capture.Join(1000);
        _writer.Join(1000);
        try { _channel.Dispose(); } catch { }
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
                    Check(MFCreateMemoryBuffer((int)Math.Max(info.cbSize, 8192), out ownBuffer), "MFCreateMemoryBuffer");
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

    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

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

        public static IAudioClient OpenLoopback(out MixFormat format)
        {
            var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out IMMDevice device),
                    "GetDefaultAudioEndpoint");
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
