using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
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
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly ICodecAPI? _codecApi;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IntPtr _deviceManagerPtr;
    private readonly int _framesPerSecond;
    private bool _disposed;

    /// <summary>Friendly name of the selected MFT, e.g. "AMDh264Encoder". For logs.</summary>
    public string EncoderName { get; }

    public PcVideoEncoder(ID3D11Device device, int width, int height, int framesPerSecond, int bitrate)
    {
        _framesPerSecond = framesPerSecond;
        (_transform, EncoderName) = SelectHardwareEncoder();
        _events = (IMFMediaEventGenerator)_transform;

        // Unlock first: until this is set the transform rejects everything else.
        Check(_transform.GetAttributes(out IMFAttributes attributes), "GetAttributes");
        var key = MF_TRANSFORM_ASYNC_UNLOCK;
        Check(attributes.SetUINT32(ref key, 1), "MF_TRANSFORM_ASYNC_UNLOCK");
        key = MF_LOW_LATENCY;
        attributes.SetUINT32(ref key, 1);   // advisory — encoders may ignore it
        Marshal.ReleaseComObject(attributes);

        // Share our D3D device so the encoder reads captured textures in place.
        Check(MFCreateDXGIDeviceManager(out int resetToken, out _deviceManager), "MFCreateDXGIDeviceManager");
        Check(_deviceManager.ResetDevice(device.NativePointer, resetToken), "ResetDevice");
        _deviceManagerPtr = Marshal.GetIUnknownForObject(_deviceManager);
        Check(_transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, _deviceManagerPtr), "SET_D3D_MANAGER");

        // Encoders require the output type before the input type.
        SetOutputType(width, height, framesPerSecond, bitrate);
        SetInputType(width, height, framesPerSecond);

        // Rate control and GOP length live on ICodecAPI, not on the media type.
        _codecApi = _transform as ICodecAPI;
        ConfigureRateControl(framesPerSecond, bitrate);

        Check(_transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "BEGIN_STREAMING");
        Check(_transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "START_OF_STREAM");
    }

    /// <summary>
    /// Constant bit rate, a two-second GOP, one reference frame and low-latency mode.
    ///
    /// <para><b>Why each matters on a wireless link.</b> CBR keeps the stream inside the
    /// bandwidth the phone asked for instead of spiking on a screen change and blowing the
    /// link's budget. The GOP bounds recovery: a phone that lost bytes shows nothing usable
    /// until the next IDR, so an encoder-default GOP (which can be many seconds, or a single
    /// keyframe at the start) turns one dropped packet into a long smear — two seconds is the
    /// worst case, and <see cref="ForceKeyFrame"/> normally beats it. One reference frame
    /// means a lost frame can only damage the frame after it, not a whole pyramid.</para>
    ///
    /// <para>Every setting is best-effort: encoders are allowed to refuse any of them, and a
    /// refusal is not a reason to fail the mirror.</para>
    /// </summary>
    private void ConfigureRateControl(int fps, int bitrate)
    {
        TrySet(CODECAPI_AVEncCommonRateControlMode, (uint)eAVEncCommonRateControlMode_CBR);
        TrySet(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
        TrySet(CODECAPI_AVEncCommonMaxBitRate, (uint)bitrate);
        TrySet(CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(fps * 2, 1));
        TrySet(CODECAPI_AVEncVideoMaxNumRefFrame, 1u);
        TrySet(CODECAPI_AVLowLatencyMode, true);
    }

    /// <summary>
    /// Ask for the next encoded frame to be an IDR. Used when the stream has lost bytes or
    /// frames were dropped to catch up, so the phone can resynchronise immediately rather
    /// than waiting out the GOP. Best-effort by design — a refusal just means the phone
    /// waits for the scheduled keyframe instead.
    /// </summary>
    public void ForceKeyFrame() => TrySet(CODECAPI_AVEncVideoForceKeyFrame, 1u);

    private void TrySet(Guid api, object value)
    {
        var codecApi = _codecApi;
        if (codecApi is null || _disposed) return;
        try
        {
            var key = api;
            if (codecApi.IsSupported(ref key) != 0) return;
            codecApi.SetValue(ref key, ref value);
        }
        catch { /* the encoder is entitled to refuse; the mirror runs without it */ }
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

    private static (IMFTransform, string) SelectHardwareEncoder()
    {
        MFT_REGISTER_TYPE_INFO[] output =
            [new() { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 }];

        Check(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            null!, output, out IntPtr list, out uint count), "MFTEnumEx");

        try
        {
            if (count == 0)
                throw new LincException(
                    "This PC has no hardware H.264 video encoder, so it can't stream its screen to your phone.");

            IntPtr first = Marshal.ReadIntPtr(list, 0);
            var activate = (IMFActivate)Marshal.GetObjectForIUnknown(first);
            var nameKey = MFT_FRIENDLY_NAME_Attribute;
            string name = activate.GetAllocatedString(ref nameKey, out string friendly, out _) == 0
                ? friendly : "unknown encoder";

            var iid = typeof(IMFTransform).GUID;
            Check(activate.ActivateObject(ref iid, out object transform), "ActivateObject");
            Marshal.ReleaseComObject(activate);
            return ((IMFTransform)transform, name);
        }
        finally
        {
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(list, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(list);
        }
    }

    /// <summary>Block until the encoder next wants input or has output. Returns an ME* constant.</summary>
    public int NextEvent()
    {
        Check(_events.GetEvent(0, out IMFMediaEvent mediaEvent), "GetEvent");
        Check(mediaEvent.GetTypeInfo(out int eventType), "GetTypeInfo");
        Marshal.ReleaseComObject(mediaEvent);
        return eventType;
    }

    /// <summary>Hand a captured texture to the encoder. <paramref name="frameIndex"/> drives PTS.</summary>
    public void Submit(ID3D11Texture2D texture, long frameIndex)
    {
        var iid = typeof(ID3D11Texture2D).GUID;
        Check(MFCreateDXGISurfaceBuffer(ref iid, texture.NativePointer, 0, false,
            out IMFMediaBuffer buffer), "MFCreateDXGISurfaceBuffer");
        Check(MFCreateSample(out IMFSample sample), "MFCreateSample");
        Check(sample.AddBuffer(buffer), "AddBuffer");
        Check(sample.SetSampleTime(frameIndex * 10_000_000L / _framesPerSecond), "SetSampleTime");
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
        try { if (_deviceManagerPtr != IntPtr.Zero) Marshal.Release(_deviceManagerPtr); } catch { }
        try { Marshal.ReleaseComObject(_transform); } catch { }
        try { Marshal.ReleaseComObject(_deviceManager); } catch { }
    }
}
