using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Linc.Desktop.Services.Mf;

namespace Linc.Desktop.Services;

/// <summary>What the encoder just produced, ready for framing onto the pc-video channel.</summary>
/// <param name="Data">Annex-B H.264 bytes.</param>
/// <param name="Keyframe">True if this access unit contains an IDR (and its SPS/PPS).</param>
/// <param name="TimestampUs">Presentation timestamp in microseconds.</param>
public readonly record struct EncodedFrame(byte[] Data, bool Keyframe, long TimestampUs);

/// <summary>
/// Hardware H.264 encoder for the reverse mirror (M05, D-029), driven through Media
/// Foundation's <b>async MFT</b> event model.
///
/// <para><b>Why hardware only.</b> Every H.264 encoder MFT on a machine like this one is
/// async and D3D11-aware, and takes ARGB32 directly — so captured frames are encoded on the
/// GPU with no colour conversion and no CPU copy. The Microsoft software fallback accepts
/// only YUV, which would mean writing and maintaining a whole BGRA->NV12 stage that cannot
/// be exercised on any machine that has hardware encode. Rather than ship an untested path,
/// a PC with no hardware encoder gets a clear refusal. See D-039.</para>
///
/// <para><b>Async MFT rules that are not obvious.</b> The transform answers
/// MF_E_TRANSFORM_ASYNC_LOCKED to <i>every</i> call — even enumerating input types — until
/// MF_TRANSFORM_ASYNC_UNLOCK is set on its attribute store. And the attributes on the
/// <c>IMFActivate</c> are not the transform's: on this machine one encoder reported no async
/// flag on its activate and async=1 on the transform itself. Always ask the transform.</para>
///
/// <para>Drive it by pumping <see cref="NextEvent"/>: on
/// <see cref="Mf.METransformNeedInput"/> call <see cref="Submit"/>, on
/// <see cref="Mf.METransformHaveOutput"/> call <see cref="Receive"/>.</para>
/// </summary>
public sealed class PcVideoEncoder : IDisposable
{
    private readonly IMFTransform _transform = null!;
    private readonly IMFMediaEventGenerator _events = null!;
    private readonly ICodecAPI? _codecApi;
    private readonly IMFDXGIDeviceManager _deviceManager = null!;
    private readonly IntPtr _deviceManagerPtr;
    private readonly int _framesPerSecond;
    private bool _disposed;

    /// <summary>Friendly name of the selected MFT, e.g. "AMDh264Encoder". For logs.</summary>
    public string EncoderName { get; } = "";

    /// <summary>
    /// Open the first hardware encoder that accepts the stream. Encoders on the graphics chip that
    /// captures the screen come first: on a hybrid laptop (Intel + NVIDIA/AMD) the first encoder
    /// Windows lists can belong to the other chip, and handed this device it fails — on a friend's
    /// PC as START_OF_STREAM MF_E_SHUTDOWN, after which the half-built encoder, never released,
    /// held the hardware session and every retry failed with E_INVALIDARG (2026-10-10). So every
    /// candidate is tried, and each one that fails is shut down and released before the next.
    /// </summary>
    public PcVideoEncoder(ID3D11Device device, int width, int height, int framesPerSecond, int bitrate)
    {
        _framesPerSecond = framesPerSecond;
        var failures = new List<string>();
        var candidates = HardwareEncoders(AdapterLuid(device));
        try
        {
            if (candidates.Count == 0)
                throw new LincException(
                    "This PC has no hardware H.264 video encoder, so it can't stream its screen to your phone.");
            foreach (var candidate in candidates)
            {
                IMFTransform? transform = null;
                IMFDXGIDeviceManager? manager = null;
                IntPtr managerPtr = IntPtr.Zero;
                try
                {
                    var iid = typeof(IMFTransform).GUID;
                    Check(candidate.Activate.ActivateObject(ref iid, out object activated), "ActivateObject");
                    transform = (IMFTransform)activated;

                    // Unlock first: until this is set the transform rejects everything else.
                    Check(transform.GetAttributes(out IMFAttributes attributes), "GetAttributes");
                    var key = MF_TRANSFORM_ASYNC_UNLOCK;
                    Check(attributes.SetUINT32(ref key, 1), "MF_TRANSFORM_ASYNC_UNLOCK");
                    key = MF_LOW_LATENCY;
                    attributes.SetUINT32(ref key, 1);   // advisory — encoders may ignore it
                    Marshal.ReleaseComObject(attributes);

                    // Share our D3D device so the encoder reads captured textures in place.
                    Check(MFCreateDXGIDeviceManager(out int resetToken, out manager), "MFCreateDXGIDeviceManager");
                    Check(manager.ResetDevice(device.NativePointer, resetToken), "ResetDevice");
                    managerPtr = Marshal.GetIUnknownForObject(manager);
                    Check(transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, managerPtr), "SET_D3D_MANAGER");

                    _transform = transform;
                    _events = (IMFMediaEventGenerator)transform;
                    _codecApi = transform as ICodecAPI;
                    _deviceManager = manager;
                    _deviceManagerPtr = managerPtr;

                    // Encoders require the output type before the input type.
                    SetOutputType(width, height, framesPerSecond, bitrate);
                    SetInputType(width, height, framesPerSecond);
                    // Rate control and GOP length live on ICodecAPI, not on the media type.
                    ConfigureRateControl(framesPerSecond, bitrate);

                    Check(transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "BEGIN_STREAMING");
                    Check(transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "START_OF_STREAM");
                    EncoderName = failures.Count == 0 ? candidate.Name : $"{candidate.Name} (after: {string.Join("; ", failures)})";
                    return;
                }
                catch (Exception ex)
                {
                    failures.Add($"{candidate.Name} — {ex.Message}");
                    // Give the hardware session back before trying the next encoder.
                    if (transform is not null)
                    {
                        try { transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
                        try { if (transform is IMFShutdown shutdown) shutdown.Shutdown(); } catch { }
                        try { candidate.Activate.ShutdownObject(); } catch { }
                        try { Marshal.ReleaseComObject(transform); } catch { }
                    }
                    try { if (managerPtr != IntPtr.Zero) Marshal.Release(managerPtr); } catch { }
                    try { if (manager is not null) Marshal.ReleaseComObject(manager); } catch { }
                    _transform = null!; _events = null!; _codecApi = null; _deviceManager = null!; _deviceManagerPtr = IntPtr.Zero;
                }
            }
            throw new LincException("None of this PC's video encoders would start: " + string.Join("; ", failures));
        }
        finally
        {
            foreach (var candidate in candidates) { try { Marshal.ReleaseComObject(candidate.Activate); } catch { } }
        }
    }

    /// <summary>The LUID of the graphics adapter a D3D device runs on.</summary>
    private static long AdapterLuid(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var luid = adapter.Description.Luid;
            return ((long)luid.HighPart << 32) | luid.LowPart;
        }
        catch { return 0; }
    }

    private sealed record Candidate(IMFActivate Activate, string Name, long? Luid);

    /// <summary>
    /// Every hardware H.264 encoder, those on <paramref name="adapterLuid"/> first, then those
    /// that don't say which adapter they belong to, then the rest — Windows' own order otherwise.
    /// </summary>
    private static List<Candidate> HardwareEncoders(long adapterLuid)
    {
        MFT_REGISTER_TYPE_INFO[] output =
            [new() { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 }];
        Check(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            null!, output, out IntPtr list, out uint count), "MFTEnumEx");
        var found = new List<Candidate>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                var nameKey = MFT_FRIENDLY_NAME_Attribute;
                string name = activate.GetAllocatedString(ref nameKey, out string friendly, out _) == 0 ? friendly : "unknown encoder";
                var luidKey = MFT_ENUM_ADAPTER_LUID;
                long? luid = activate.GetUINT64(ref luidKey, out long value) == 0 ? value : null;
                found.Add(new Candidate(activate, name, luid));
            }
        }
        finally
        {
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(list, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(list);
        }
        return found
            .Select((c, i) => (c, i, rank: c.Luid == adapterLuid && adapterLuid != 0 ? 0 : c.Luid is null ? 1 : 2))
            .OrderBy(x => x.rank).ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    /// <summary>For logs: this PC's graphics adapters and hardware H.264 encoders.</summary>
    public static IEnumerable<string> Describe()
    {
        var lines = new List<string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    long luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
                    int outputs = 0;
                    while (adapter.EnumOutputs((uint)outputs, out IDXGIOutput o).Success) { o.Dispose(); outputs++; }
                    lines.Add($"GPU {a}: {d.Description} (luid {luid:X}, {outputs} display(s))");
                }
            }
            foreach (var c in HardwareEncoders(0))
            {
                lines.Add($"H.264 encoder: {c.Name}" + (c.Luid is { } l ? $" (luid {l:X})" : ""));
                Marshal.ReleaseComObject(c.Activate);
            }
        }
        catch (Exception ex) { lines.Add($"(couldn't list graphics hardware: {ex.Message})"); }
        return lines;
    }

    /// <summary>
    /// Peak-constrained VBR with a quality floor, a two-second GOP, one reference frame and
    /// low-latency mode.
    ///
    /// <para><b>Not CBR — measured (D-066).</b> Constant bit rate on this class of encoder means
    /// <i>padding</i>: an idle desktop at an 8 Mb/s target measured 7.2 Mb/s on the wire, about
    /// half of it filler NALs (type 12) and the rest P-frames bloated to use the budget. The
    /// encoder's own default was identical. So a mirror of a screen nobody was touching kept a
    /// Wi-Fi link saturated full-time, and every real change queued behind the padding. VBR sends
    /// what the picture needs, capped at the target; the target itself is what the sender's
    /// congestion control moves.</para>
    ///
    /// <para><b>The QP floor</b> stops VBR spending the headroom on "refining" an image that has
    /// not changed, pass after pass, toward a quality no phone screen can show: with it an idle
    /// screen measured 0.55 Mb/s instead of 3.35. QP 20 is sharp for desktop text — 26 saved
    /// another 0.17 Mb/s and started to soften it.</para>
    ///
    /// <para>The GOP bounds recovery: a phone that lost sync shows nothing usable until the
    /// next IDR, so two seconds is the worst case and <see cref="ForceKeyFrame"/> normally beats
    /// it. One reference frame means a lost frame can only damage the frame after it.</para>
    ///
    /// <para>Every setting is best-effort: encoders are allowed to refuse any of them, and a
    /// refusal is not a reason to fail the mirror.</para>
    /// </summary>
    private void ConfigureRateControl(int fps, int bitrate)
    {
        TrySet(CODECAPI_AVEncCommonRateControlMode, (uint)eAVEncCommonRateControlMode_PeakConstrainedVBR);
        TrySet(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
        TrySet(CODECAPI_AVEncCommonMaxBitRate, (uint)bitrate);
        TrySet(CODECAPI_AVEncVideoMinQP, MinQp);
        TrySet(CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(fps * 2, 1));
        TrySet(CODECAPI_AVEncVideoMaxNumRefFrame, 1u);
        TrySet(CODECAPI_AVLowLatencyMode, true);
    }

    /// <summary>The quality floor — see <see cref="ConfigureRateControl"/>.</summary>
    private const uint MinQp = 20;

    /// <summary>
    /// Ask for the next encoded frame to be an IDR. Used when the stream has lost bytes or
    /// frames were dropped to catch up, so the phone can resynchronise immediately rather
    /// than waiting out the GOP. Best-effort by design — a refusal just means the phone
    /// waits for the scheduled keyframe instead.
    /// </summary>
    public void ForceKeyFrame() => TrySet(CODECAPI_AVEncVideoForceKeyFrame, 1u);

    /// <summary>
    /// Change the target bit rate while streaming — the lever the sender's congestion control
    /// pulls when the link cannot carry what was asked for. Returns false when the encoder
    /// refused, so the caller knows the stream is still running at the old rate.
    /// </summary>
    public bool SetBitrate(int bitrate)
    {
        bool mean = TrySet(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
        TrySet(CODECAPI_AVEncCommonMaxBitRate, (uint)bitrate);
        return mean;
    }

    private bool TrySet(Guid api, object value)
    {
        var codecApi = _codecApi;
        if (codecApi is null || _disposed) return false;
        try
        {
            var key = api;
            if (codecApi.IsSupported(ref key) != 0) return false;
            return codecApi.SetValue(ref key, ref value) == 0;
        }
        catch { return false; /* the encoder is entitled to refuse; the mirror runs without it */ }
    }

    private void SetOutputType(int width, int height, int fps, int bitrate)
    {
        Check(MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType(output)");
        var key = MF_MT_MAJOR_TYPE; var value = MFMediaType_Video;
        type.SetGUID(ref key, ref value);
        key = MF_MT_SUBTYPE; value = MFVideoFormat_H264; type.SetGUID(ref key, ref value);
        key = MF_MT_AVG_BITRATE; type.SetUINT32(ref key, bitrate);
        key = MF_MT_FRAME_SIZE; type.SetUINT64(ref key, Pack(width, height));
        key = MF_MT_FRAME_RATE; type.SetUINT64(ref key, Pack(fps, 1));
        key = MF_MT_PIXEL_ASPECT_RATIO; type.SetUINT64(ref key, Pack(1, 1));
        key = MF_MT_INTERLACE_MODE; type.SetUINT32(ref key, MFVideoInterlace_Progressive);
        key = MF_MT_MPEG2_PROFILE; type.SetUINT32(ref key, eAVEncH264VProfile_High);
        Check(_transform.SetOutputType(0, type, 0), "SetOutputType");
        Marshal.ReleaseComObject(type);
    }

    private void SetInputType(int width, int height, int fps)
    {
        // ARGB32 is byte-identical to the captured B8G8R8A8 texture, so this is the
        // no-conversion path. Hardware MFTs accept it even though some refuse to enumerate it.
        Check(MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType(input)");
        var key = MF_MT_MAJOR_TYPE; var value = MFMediaType_Video;
        type.SetGUID(ref key, ref value);
        key = MF_MT_SUBTYPE; value = MFVideoFormat_ARGB32; type.SetGUID(ref key, ref value);
        key = MF_MT_FRAME_SIZE; type.SetUINT64(ref key, Pack(width, height));
        key = MF_MT_FRAME_RATE; type.SetUINT64(ref key, Pack(fps, 1));
        key = MF_MT_PIXEL_ASPECT_RATIO; type.SetUINT64(ref key, Pack(1, 1));
        key = MF_MT_INTERLACE_MODE; type.SetUINT32(ref key, MFVideoInterlace_Progressive);
        Check(_transform.SetInputType(0, type, 0), "SetInputType");
        Marshal.ReleaseComObject(type);
    }

    /// <summary>Block until the encoder next wants input or has output. Returns an ME* constant.</summary>
    public int NextEvent()
    {
        Check(_events.GetEvent(0, out IMFMediaEvent mediaEvent), "GetEvent");
        Check(mediaEvent.GetTypeInfo(out int eventType), "GetTypeInfo");
        Marshal.ReleaseComObject(mediaEvent);
        return eventType;
    }

    /// <summary>
    /// Hand a captured texture to the encoder. <paramref name="sampleTime"/> is its
    /// presentation time in 100 ns units (Media Foundation's clock), from the stream's wall clock.
    /// </summary>
    public void Submit(ID3D11Texture2D texture, long sampleTime)
    {
        var iid = typeof(ID3D11Texture2D).GUID;
        Check(MFCreateDXGISurfaceBuffer(ref iid, texture.NativePointer, 0, false,
            out IMFMediaBuffer buffer), "MFCreateDXGISurfaceBuffer");
        Check(MFCreateSample(out IMFSample sample), "MFCreateSample");
        Check(sample.AddBuffer(buffer), "AddBuffer");
        Check(sample.SetSampleTime(sampleTime), "SetSampleTime");
        Check(sample.SetSampleDuration(10_000_000L / _framesPerSecond), "SetSampleDuration");
        Check(_transform.ProcessInput(0, sample, 0), "ProcessInput");
        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buffer);
    }

    /// <summary>
    /// Collect one encoded access unit, or null if the encoder needs more input first
    /// (a normal answer, not a failure).
    /// </summary>
    public EncodedFrame? Receive()
    {
        var buffers = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0 };
        int hr = _transform.ProcessOutput(0, 1, ref buffers, out _);
        if ((uint)hr is MF_E_TRANSFORM_NEED_MORE_INPUT or MF_E_TRANSFORM_STREAM_CHANGE) return null;
        Check(hr, "ProcessOutput");
        if (buffers.pSample is null) return null;

        try
        {
            buffers.pSample.GetSampleTime(out long sampleTime);
            Check(buffers.pSample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer),
                "ConvertToContiguousBuffer");
            try
            {
                Check(buffer.Lock(out IntPtr pointer, out _, out int length), "Lock");
                var data = new byte[length];
                Marshal.Copy(pointer, data, 0, length);
                buffer.Unlock();
                return new EncodedFrame(data, ContainsKeyframe(data), sampleTime / 10);
            }
            finally { Marshal.ReleaseComObject(buffer); }
        }
        finally
        {
            Marshal.ReleaseComObject(buffers.pSample);
            if (buffers.pEvents is not null) Marshal.ReleaseComObject(buffers.pEvents);
        }
    }

    /// <summary>
    /// True if this access unit carries an IDR (NAL type 5). The phone can only join the
    /// stream on one of these, so the channel uses it to set the keyframe flag.
    /// </summary>
    private static bool ContainsKeyframe(byte[] data)
    {
        for (int i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int header = data[i + 2] == 1 ? i + 3
                       : data[i + 2] == 0 && data[i + 3] == 1 ? i + 4
                       : -1;
            if (header < 0 || header >= data.Length) continue;
            if ((data[header] & 0x1F) == 5) return true;
            i = header;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Shutdown ordering matters and a throw here would mask the real error, so each
        // step is best-effort.
        try { _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
        // Wakes a pump thread parked in GetEvent (it gets MF_E_SHUTDOWN and throws). Without
        // this, Dispose left that thread blocked for good — harmless-looking at the end of a
        // session, fatal when the encoder is rebuilt mid-stream (D-069).
        try { if (_transform is IMFShutdown shutdown) shutdown.Shutdown(); } catch { }
        try { if (_deviceManagerPtr != IntPtr.Zero) Marshal.Release(_deviceManagerPtr); } catch { }
        try { Marshal.ReleaseComObject(_transform); } catch { }
        try { Marshal.ReleaseComObject(_deviceManager); } catch { }
    }
}
