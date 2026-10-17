using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// Windows Voice Access driven from the phone, with the phone as the PC's microphone (v21,
/// <c>pc.voiceaccess.set</c>, pc-mic channel 7, D-070).
///
/// <para><b>Why a virtual cable.</b> Windows only lets a <i>driver</i> create a microphone, and
/// Voice Access listens to the default one. So the phone's audio is played into a virtual audio
/// cable's playback side (VB-CABLE's "CABLE Input", or any endpoint pair named like one), the
/// cable's recording side is made the default microphone for the session, and the user's own
/// default is put back afterwards. With no cable installed Voice Access still starts, on the PC's
/// own microphone, and the phone is told why its mic is not in use. With the user's agreement
/// (given on the phone) Linc installs the cable itself — see <see cref="VirtualCableSetup"/>,
/// D-071 — and reports how that is going in the state's <c>setup</c> field.</para>
///
/// <para>The user's own default microphone is written to disk before it is switched, so it is
/// put back after the session, when Linc exits, and on the next start if Linc was killed
/// mid-session (<see cref="RestorePendingDefaultMic"/>).</para>
///
/// <para>The phone sends 16 kHz mono 16-bit PCM — speech needs no more, and 256 kb/s is nothing
/// next to the mirror — which is upsampled to the cable's mix format here. At most ~150 ms is
/// buffered; anything older is dropped, because a voice command heard late is worse than one
/// clipped.</para>
/// </summary>
public sealed class PcVoiceAccess : IDisposable
{
    public const int Channel = 7;
    private const int PhoneRate = 16_000;
    private const int MaxBufferedMs = 150;

    private readonly IConnectionManager _connection;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private Stream? _channel;
    private Thread? _reader;
    private Thread? _renderer;
    private CancellationTokenSource? _stop;
    private string? _restoreCaptureId;
    private readonly Queue<float> _ring = new();

    public PcVoiceAccess(IConnectionManager connection, ILogService log)
    {
        _connection = connection;
        _log = log;
        CableSetup = new VirtualCableSetup(log, CablePresent);
    }

    public VirtualCableSetup CableSetup { get; }

    /// <summary>"failed" after a setup attempt that didn't work, until the next one.</summary>
    private string? _setupState;

    public static string VoiceAccessPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "VoiceAccess.exe");

    public static bool Available => File.Exists(VoiceAccessPath);

    public static bool Running => Process.GetProcessesByName("VoiceAccess").Length > 0;

    /// <summary>Handle the phone's <c>pc.voiceaccess.set</c>; always answers with the resulting state.</summary>
    public async Task SetAsync(bool on, bool phoneMic, bool installCable = false)
    {
        string mic = "pc";
        bool cable = false;
        string? error = null;
        string? setup = null;
        try
        {
            if (!on)
            {
                StopMic();
                StopVoiceAccess();
            }
            else if (!Available)
            {
                error = "This PC doesn't have Voice Access (it needs Windows 11 22H2 or later).";
            }
            else
            {
                var pair = FindCable();
                if (phoneMic && pair is null && installCable)
                {
                    // The user agreed on the phone: set the cable up now (D-071). The phone
                    // shows "setting up the microphone…" while Windows asks on the PC.
                    await SendStateAsync("pc", cable: false, setup: "installing");
                    var result = await CableSetup.InstallAsync();
                    _setupState = result.Ok ? null : "failed";
                    if (result.Ok) pair = FindCable();
                    else error = result.Message;
                }
                cable = pair is not null;
                if (phoneMic && pair is { } p)
                {
                    await StartMicAsync(p.Render, p.CaptureId);
                    mic = "phone";
                }
                else if (pair is { } unused)
                {
                    Marshal.ReleaseComObject(unused.Render);
                }
                StartVoiceAccess();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Log(LogLevel.Warn, $"Voice Access: {ex.Message}");
        }
        await SendStateAsync(mic, cable, error, setup);
    }

    public Task SendStateAsync(string? mic = null, bool? cable = null, string? error = null, string? setup = null)
    {
        bool running = Running;
        bool hasCable = cable ?? CablePresent();
        var payload = new JsonObject
        {
            ["available"] = Available,
            ["on"] = running,
            ["mic"] = mic ?? (_channel is not null ? "phone" : "pc"),
            ["cable"] = hasCable,
            // v21 addition (D-071): whether the PC can set the cable up itself, and how it's going.
            ["setup"] = setup ?? (CableSetup.IsBusy ? "installing" : hasCable ? "ready" : _setupState ?? "needed"),
        };
        if (error is not null) payload["error"] = error;
        return _connection.SendToPhoneAsync(Envelope.Create(MessageType.PcVoiceAccessState, payload), CancellationToken.None);
    }

    private void StartVoiceAccess()
    {
        if (Running) return;
        Process.Start(new ProcessStartInfo(VoiceAccessPath) { UseShellExecute = true });
        _log.Log(LogLevel.Info, "Voice Access: started");
    }

    private void StopVoiceAccess()
    {
        foreach (var process in Process.GetProcessesByName("VoiceAccess"))
        {
            try
            {
                if (!process.CloseMainWindow() || !process.WaitForExit(2000)) process.Kill();
                _log.Log(LogLevel.Info, "Voice Access: stopped");
            }
            catch { /* already gone */ }
            finally { process.Dispose(); }
        }
    }

    // ---- the phone's microphone -------------------------------------------------

    private async Task StartMicAsync(IMMDevice render, string captureId)
    {
        lock (_gate) { if (_channel is not null) return; }
        var channel = await _connection.OpenPhoneDialledChannelAsync(Channel, CancellationToken.None);
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _channel = channel;
            _stop = stop;
            _restoreCaptureId = DefaultCaptureId();
            _ring.Clear();
        }
        // Remember the user's own microphone on disk first, so a crash can't strand the PC on
        // the cable as its default.
        if (_restoreCaptureId is { } own && own != captureId) WriteRestoreFile(own);
        SetDefaultCapture(captureId, _log);
        _reader = new Thread(() => Read(channel, stop.Token)) { IsBackground = true, Name = "linc-mic-reader" };
        _renderer = new Thread(() => Render(render, stop.Token)) { IsBackground = true, Name = "linc-mic-render" };
        _reader.Start();
        _renderer.Start();
        _log.Log(LogLevel.Info, "Voice Access: using the phone as this PC's microphone");
    }

    public void StopMic()
    {
        CancellationTokenSource? stop;
        Stream? channel;
        string? restore;
        lock (_gate)
        {
            stop = _stop; channel = _channel; restore = _restoreCaptureId;
            _stop = null; _channel = null; _restoreCaptureId = null;
        }
        if (stop is null) return;
        stop.Cancel();
        try { channel?.Dispose(); } catch { }
        _reader?.Join(1000);
        _renderer?.Join(1000);
        if (restore is not null) SetDefaultCapture(restore, _log);
        DeleteRestoreFile();
        _log.Log(LogLevel.Info, "Voice Access: the phone's microphone is off; this PC's own is back");
    }

    /// <summary>Phone PCM → the ring, upsampled to the cable's mix format as it arrives.</summary>
    private void Read(Stream channel, CancellationToken token)
    {
        try
        {
            var configLength = new byte[4];
            ReadExactly(channel, configLength);
            var config = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(configLength)];
            ReadExactly(channel, config); // {"codec":"pcm16","sampleRate":16000,"channels":1}

            var header = new byte[VideoFraming.HeaderBytes];
            while (!token.IsCancellationRequested)
            {
                ReadExactly(channel, header);
                var h = VideoFraming.ReadHeader(header);
                if (h.Length is < 0 or > 64_000) throw new InvalidDataException("bad mic packet");
                var data = new byte[h.Length];
                ReadExactly(channel, data);
                var pcm = new short[data.Length / 2];
                Buffer.BlockCopy(data, 0, pcm, 0, pcm.Length * 2);
                int outRate = Volatile.Read(ref _mixRate);
                if (outRate == 0) continue;
                var samples = PcMicAudio.Upsample(pcm, PhoneRate, outRate);
                lock (_ring)
                {
                    foreach (var s in samples) _ring.Enqueue(s);
                    int max = outRate * MaxBufferedMs / 1000;
                    while (_ring.Count > max) _ring.Dequeue();
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Info, $"Voice Access: the phone's microphone stream ended ({ex.Message})");
            _ = Task.Run(() => { StopMic(); _ = SendStateAsync(); });
        }
    }

    private int _mixRate;

    /// <summary>The ring → the cable's playback side, in its own mix format (mono copied to every channel).</summary>
    private void Render(IMMDevice device, CancellationToken token)
    {
        IAudioClient? client = null;
        IAudioRenderClient? renderClient = null;
        try
        {
            var iid = IID_IAudioClient;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object unknown), "Activate");
            client = (IAudioClient)unknown;
            Check(client.GetMixFormat(out IntPtr mix), "GetMixFormat");
            int channels, rate, bits;
            bool isFloat;
            try
            {
                ushort tag = (ushort)Marshal.ReadInt16(mix, 0);
                channels = Marshal.ReadInt16(mix, 2);
                rate = Marshal.ReadInt32(mix, 4);
                bits = Marshal.ReadInt16(mix, 14);
                isFloat = tag == 3 || (tag == 0xFFFE && Marshal.PtrToStructure<Guid>(mix + 24) == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
                Check(client.Initialize(0, 0, 1_000_000, 0, mix, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeCoTaskMem(mix); }
            Check(client.GetBufferSize(out uint bufferFrames), "GetBufferSize");
            var riid = IID_IAudioRenderClient;
            Check(client.GetService(ref riid, out object service), "GetService");
            renderClient = (IAudioRenderClient)service;
            Volatile.Write(ref _mixRate, rate);
            Check(client.Start(), "Start");

            while (!token.IsCancellationRequested)
            {
                token.WaitHandle.WaitOne(10);
                Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
                int room = (int)(bufferFrames - padding);
                int frames;
                lock (_ring) frames = Math.Min(room, _ring.Count);
                if (frames <= 0) continue;
                Check(renderClient.GetBuffer((uint)frames, out IntPtr buffer), "GetBuffer");
                lock (_ring)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        float v = _ring.Count > 0 ? _ring.Dequeue() : 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            IntPtr at = buffer + (f * channels + c) * (bits / 8);
                            if (isFloat) Marshal.WriteInt32(at, BitConverter.SingleToInt32Bits(v));
                            else Marshal.WriteInt16(at, (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue));
                        }
                    }
                }
                Check(renderClient.ReleaseBuffer((uint)frames, 0), "ReleaseBuffer");
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _log.Log(LogLevel.Warn, $"Voice Access: couldn't play the phone's microphone into the cable: {ex.Message}");
        }
        catch { }
        finally
        {
            try { client?.Stop(); } catch { }
            if (renderClient is not null) Marshal.ReleaseComObject(renderClient);
            if (client is not null) Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("the phone hung up");
            read += n;
        }
    }

    // ---- finding the cable, and the default microphone -----------------------------

    /// <summary>Whether a virtual cable pair is installed and active.</summary>
    public static bool CablePresent()
    {
        if (FindCable() is not { } pair) return false;
        Marshal.ReleaseComObject(pair.Render);
        return true;
    }

    /// <summary>
    /// The store root (<see cref="IDeviceRegistry.RootPath"/>), set once at startup — see
    /// <see cref="VirtualCablePackage.StoreRoot"/> for why it is not resolved here.
    /// </summary>
    public static string? StoreRoot { get; set; }

    // Every caller already treats a file error as "nothing to restore", which an unset root is too.
    private static string RestoreFile => Path.Combine(
        StoreRoot ?? throw new InvalidOperationException("PcVoiceAccess.StoreRoot was never set."),
        "voiceaccess-restore-mic.txt");

    private static void WriteRestoreFile(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestoreFile)!);
            File.WriteAllText(RestoreFile, id);
        }
        catch { /* best effort; StopMic still restores in-process */ }
    }

    private static void DeleteRestoreFile()
    {
        try { File.Delete(RestoreFile); } catch { }
    }

    /// <summary>
    /// Put back the user's own default microphone if a phone-mic session left the cable as the
    /// default — called when Linc exits, and when it starts (after a crash or a kill).
    /// </summary>
    public static void RestorePendingDefaultMic(ILogService log)
    {
        // Speaker mode leaves the same kind of note for the default playback device (D-076).
        PcSpeakerMode.RestorePending(log);

        string? id;
        try { id = File.Exists(RestoreFile) ? File.ReadAllText(RestoreFile).Trim() : null; }
        catch { return; }
        if (string.IsNullOrEmpty(id)) return;
        SetDefaultCapture(id, log);
        DeleteRestoreFile();
        log.Log(LogLevel.Info, "Voice Access: put this PC's own microphone back as the default");
    }

    /// <summary>
    /// A virtual cable: an active playback endpoint named "CABLE Input" (VB-CABLE) or containing
    /// "Virtual" and "Input", and the active recording endpoint that pairs with it. Null if absent.
    /// </summary>
    internal static (IMMDevice Render, string CaptureId)? FindCable()
    {
        try
        {
            var renders = Endpoints(0);
            var captures = Endpoints(1);
            var render = renders.FirstOrDefault(e => PcMicAudio.IsCableInput(e.Name));
            var capture = captures.FirstOrDefault(e => PcMicAudio.IsCableOutput(e.Name));
            foreach (var e in renders.Concat(captures))
                if (!ReferenceEquals(e.Device, render.Device)) Marshal.ReleaseComObject(e.Device);
            if (render.Device is null || capture.Id is null) return null;
            return (render.Device, capture.Id);
        }
        catch { return null; }
    }

    private static List<(IMMDevice Device, string Id, string Name)> Endpoints(int flow)
    {
        var list = new List<(IMMDevice, string, string)>();
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        try
        {
            Check(enumerator.EnumAudioEndpoints(flow, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection collection), "EnumAudioEndpoints");
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out IMMDevice device);
                device.GetId(out string id);
                list.Add((device, id, FriendlyName(device)));
            }
            Marshal.ReleaseComObject(collection);
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return list;
    }

    private static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out IPropertyStore store) != 0) return "";
        try
        {
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out PROPVARIANT value) != 0) return "";
            try { return value.vt == 31 ? Marshal.PtrToStringUni(value.pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? DefaultCaptureId() => DefaultEndpointId(1 /* eCapture */);

    /// <summary>The current default playback device (speaker mode remembers it to put it back).</summary>
    internal static string? DefaultRenderId() => DefaultEndpointId(0 /* eRender */);

    private static string? DefaultEndpointId(int flow)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, 0 /* eConsole */, out IMMDevice device) != 0) return null;
            device.GetId(out string id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    /// <summary>The virtual cable's playback side ("CABLE Input"), or null when no cable is installed.</summary>
    internal static string? CableRenderId()
    {
        if (FindCable() is not { } pair) return null;
        try { return pair.Render.GetId(out string id) == 0 ? id : null; }
        finally { Marshal.ReleaseComObject(pair.Render); }
    }

    /// <summary>
    /// Make <paramref name="id"/> the default recording device for every role. IPolicyConfig is
    /// the interface Windows' own Sound settings use; it is undocumented but has been stable
    /// since Windows 7, and it is the only way to switch the default device.
    /// </summary>
    private static void SetDefaultCapture(string id, ILogService log)
    {
        if (!SetDefaultEndpoint(id))
            log.Log(LogLevel.Warn, "Voice Access: couldn't switch the default microphone");
    }

    /// <summary>
    /// Make <paramref name="id"/> the default device of its kind (playback or recording, which
    /// the id itself decides) for every role. False if Windows refused.
    /// </summary>
    internal static bool SetDefaultEndpoint(string id)
    {
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_PolicyConfigClient)!)!;
            try
            {
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) >= 0;
                return ok;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }
        catch { return false; }
    }

    public void Dispose() => StopMic();

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new LincException($"{what} failed (0x{hr:X8})");
    }

    // ---- interop ----------------------------------------------------------------

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    private const int CLSCTX_ALL = 0x17;

    [StructLayout(LayoutKind.Sequential)] internal struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pointer; }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
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

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool defaultPeriod, out long defaultPeriodValue, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}
