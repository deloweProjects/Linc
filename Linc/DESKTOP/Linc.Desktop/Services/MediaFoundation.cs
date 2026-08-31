using System;
using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

/// <summary>
/// Hand-rolled Media Foundation interop — only the surface M05's reverse mirror needs
/// (see docs/PROTOCOL.md v14, D-029). There is no MF binding package in this project;
/// these few interfaces are cheaper than taking one on.
///
/// <para><b>Vtable order is load-bearing.</b> A COM interface's layout is the base
/// interface's methods followed by its own, so every inherited method must be redeclared
/// here in exact order — including ones we never call. Omitting one silently shifts every
/// later slot and you end up calling the wrong function.</para>
///
/// <para><b>Read the HRESULT, never the out-param alone.</b> <c>GetUINT32</c> and friends
/// leave their out-param undefined when an attribute is absent, so trusting it without
/// checking the return code yields convincing garbage — that mistake cost a wrong reading
/// of every encoder's async/D3D flags while building this.</para>
/// </summary>
public static class Mf
{
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("c16eb52b-73a1-476f-8d62-839d6a020652");

    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    public const int MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const int MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const int MFT_MESSAGE_SET_D3D_MANAGER = 2;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    public const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72;
    public const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61;
    public const uint MF_E_INVALIDMEDIATYPE = 0xC00D36B4;
    public const uint MF_E_NO_MORE_TYPES = 0xC00D36B9;

    public const int MFVideoInterlace_Progressive = 2;
    public const int eAVEncH264VProfile_Main = 77;
    public const int eAVEncH264VProfile_High = 100;

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags; public uint cbSize; public uint cbAlignment; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency; public uint dwFlags;
        public uint cbSize; public uint cbMaxLookahead; public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        [MarshalAs(UnmanagedType.Interface)] public IMFSample pSample;
        public uint dwStatus;
        [MarshalAs(UnmanagedType.Interface)] public IMFCollection pEvents;
    }

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(Guid guidCategory, uint flags,
        [In] MFT_REGISTER_TYPE_INFO[] pInputType, [In] MFT_REGISTER_TYPE_INFO[] pOutputType,
        out IntPtr pppMFTActivate, out uint pcMFTActivate);

    // MFSetAttributeSize / MFSetAttributeRatio are inline C helpers: pack two
    // UINT32 into the hi/lo halves of a UINT64 attribute.
    public static long Pack(int hi, int lo) => ((long)hi << 32) | (uint)lo;
    public static (int hi, int lo) Unpack(long v) => ((int)(v >> 32), (int)(v & 0xFFFFFFFF));

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buf, int bufSize, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, [In] byte[] buf, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType : IMFAttributes
    {
        // --- IMFAttributes slots, repeated so the vtable lines up ---
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out] byte[] buf, int bufSize, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [In] byte[] buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        // --- IMFMediaType's own ---
        [PreserveSig] int GetMajorType(out Guid major);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
        [PreserveSig] int GetRepresentation(Guid rep, out IntPtr ppv);
        [PreserveSig] int FreeRepresentation(Guid rep, IntPtr pv);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample : IMFAttributes
    {
        // IMFAttributes slots (30)
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out] byte[] buf, int bufSize, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [In] byte[] buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        // IMFSample's own
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("5bc8a76b-869a-46a3-9b03-fa218a66aebe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFCollection
    {
        [PreserveSig] int GetElementCount(out int count);
        [PreserveSig] int GetElement(int index, [MarshalAs(UnmanagedType.IUnknown)] out object element);
        [PreserveSig] int AddElement([MarshalAs(UnmanagedType.IUnknown)] object element);
        [PreserveSig] int RemoveElement(int index, [MarshalAs(UnmanagedType.IUnknown)] out object element);
        [PreserveSig] int InsertElementAt(int index, [MarshalAs(UnmanagedType.IUnknown)] object element);
        [PreserveSig] int RemoveAllElements();
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int inMin, out int inMax, out int outMin, out int outMax);
        [PreserveSig] int GetStreamCount(out int inCount, out int outCount);
        [PreserveSig] int GetStreamIDs(int inSize, [Out] int[] inIds, int outSize, [Out] int[] outIds);
        [PreserveSig] int GetInputStreamInfo(int id, out MFT_INPUT_STREAM_INFO info);
        [PreserveSig] int GetOutputStreamInfo(int id, out MFT_OUTPUT_STREAM_INFO info);
        [PreserveSig] int GetAttributes(out IMFAttributes attrs);
        [PreserveSig] int GetInputStreamAttributes(int id, out IMFAttributes attrs);
        [PreserveSig] int GetOutputStreamAttributes(int id, out IMFAttributes attrs);
        [PreserveSig] int DeleteInputStream(int id);
        [PreserveSig] int AddInputStreams(int count, [In] int[] ids);
        [PreserveSig] int GetInputAvailableType(int id, int index, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(int id, int index, out IMFMediaType type);
        [PreserveSig] int SetInputType(int id, IMFMediaType type, int flags);
        [PreserveSig] int SetOutputType(int id, IMFMediaType type, int flags);
        [PreserveSig] int GetInputCurrentType(int id, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(int id, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(int id, out int flags);
        [PreserveSig] int GetOutputStatus(out int flags);
        [PreserveSig] int SetOutputBounds(long lower, long upper);
        [PreserveSig] int ProcessEvent(int id, IntPtr evt);
        [PreserveSig] int ProcessMessage(int message, IntPtr param);
        [PreserveSig] int ProcessInput(int id, IMFSample sample, int flags);
        [PreserveSig] int ProcessOutput(int flags, int count, ref MFT_OUTPUT_DATA_BUFFER buffers, out int status);
    }

    [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFActivate : IMFAttributes
    {
        // IMFAttributes slots (30)
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out] byte[] buf, int bufSize, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [In] byte[] buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        // IMFActivate's own
        [PreserveSig] int ActivateObject(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    // Async-MFT event types (IMFMediaEvent.GetType)
    public const int METransformNeedInput = 601;
    public const int METransformHaveOutput = 602;
    public const int METransformDrainComplete = 603;
    public const int METransformMarker = 604;

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IMFDXGIDeviceManager manager);

    // Vortice objects are raw-pointer wrappers, not COM RCWs -- take IntPtr and
    // pass their NativePointer rather than letting the marshaller try.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGISurfaceBuffer(ref Guid riid,
        IntPtr surface, int subresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenRgb, out IMFMediaBuffer buffer);

    [ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIDeviceManager
    {
        [PreserveSig] int CloseDeviceHandle(IntPtr handle);
        [PreserveSig] int GetVideoService(IntPtr handle, ref Guid riid, out IntPtr service);
        [PreserveSig] int LockDevice(IntPtr handle, ref Guid riid, out IntPtr ppv, bool block);
        [PreserveSig] int OpenDeviceHandle(out IntPtr handle);
        [PreserveSig] int ResetDevice(IntPtr device, int resetToken);
        [PreserveSig] int TestDevice(IntPtr handle);
        [PreserveSig] int UnlockDevice(IntPtr handle, bool saveState);
    }

    [ComImport, Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(int flags, out IMFMediaEvent evt);
        [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result, out IMFMediaEvent evt);
        [PreserveSig] int QueueEvent(int met, ref Guid extendedType, int status, IntPtr value);
    }

    [ComImport, Guid("df598932-f10c-4e39-bba2-c308f101daa3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEvent : IMFAttributes
    {
        // IMFAttributes slots (30)
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out] byte[] buf, int bufSize, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [In] byte[] buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        // IMFMediaEvent's own
        [PreserveSig] int GetTypeInfo(out int met);
        [PreserveSig] int GetExtendedType(out Guid guid);
        [PreserveSig] int GetStatus(out int status);
        [PreserveSig] int GetValue(IntPtr value);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed 0x{hr:X8}");
    }
}
