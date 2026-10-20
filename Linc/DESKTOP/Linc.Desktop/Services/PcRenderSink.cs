using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

/// <summary>A playback device on this PC that Play everywhere can use.</summary>
public sealed record PcOutputDevice(string Id, string Name, bool Bluetooth);

/// <summary>
/// One of this PC's speakers in Play everywhere (v26, D-096): a WASAPI shared-mode stream on one
/// device, fed by <see cref="PlayoutEngine"/> so it is heard in step with every other speaker.
///
/// <para><b>When a frame is heard.</b> <c>IAudioClock::GetPosition</c> says which frame the device
/// was playing at which QPC time; the frame about to be written is that many frames further on,
/// plus the device's hidden latency (what the clock can't see, such as a Bluetooth headset's own
/// buffer, measured by the phone's microphone). Only ~40 ms is kept in the device buffer, so a
/// correction takes effect quickly; the group delay itself waits in the engine's queue.</para>
///
/// <para>Runs on its own MTA thread, which creates and owns every COM object it uses.</para>
/// </summary>
public sealed class PcRenderSink : IDisposable
{
    private const long FillUs = 40_000;
    private const long TickMs = 5;
    private const long NeedMarginUs = 15_000;
    private const long StatsWindowUs = 5_000_000;

    private readonly ILogService _log;
    private readonly Func<long> _delayUs;
    private readonly Func<IReadOnlyList<TestSound>> _sounds;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopping = new();
    private readonly int _sourceRate;
    private PlayoutEngine? _engine;

    private long _maxAgeUs, _maxAgeAtUs;
    private long _maxOutUs, _maxOutAtUs;

    public string Id { get; }
    public string Name { get; }
    public bool Bluetooth { get; }

    /// <summary>Hidden latency in µs (measured + the user's nudge); read every tick.</summary>
    public long HiddenUs { get; set; }
    /// <summary>0..1, already shaped for the ear.</summary>
    public float Gain { get; set; } = 1;
    /// <summary>Program sound off (muted, or the alignment is playing its test sounds).</summary>
    public bool Silenced { get; set; }

    public string Status { get; private set; } = "starting";
    public string? Problem { get; private set; }
    public double OffByUs => _engine?.Controller.SmoothedErrorUs ?? 0;

    /// <summary>What this speaker needs the group delay to be, µs: capture → here, plus its own output.</summary>
    public long NeedUs => Interlocked.Read(ref _maxAgeUs) + Interlocked.Read(ref _maxOutUs) + HiddenUs + NeedMarginUs;

    public long SilenceFrames;
    public long SkippedFrames;
    public long Underruns;

    public PcRenderSink(PcOutputDevice device, int sourceRate, Func<long> delayUs, Func<IReadOnlyList<TestSound>> sounds, ILogService log)
    {
        Id = device.Id;
        Name = device.Name;
        Bluetooth = device.Bluetooth;
        _sourceRate = sourceRate;
        _delayUs = delayUs;
        _sounds = sounds;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "linc-everywhere-" + device.Name, Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool Alive => _thread.IsAlive;

    /// <summary>A block of the PC's sound for this speaker (from the capture thread).</summary>
    public void Enqueue(SoundBlock block)
    {
        long now = PcClock.NowUs();
        Track(ref _maxAgeUs, ref _maxAgeAtUs, now - block.CaptureUs, now);
        _engine?.Enqueue(block, _delayUs() + 1_000_000);
    }

    public void Realign() => _engine?.Realign();

    private static void Track(ref long max, ref long at, long value, long now)
    {
        // A max that forgets after a while, so a single hiccup doesn't hold the delay up forever.
        if (value >= Interlocked.Read(ref max) || now - Interlocked.Read(ref at) > StatsWindowUs)
        {
            Interlocked.Exchange(ref max, Math.Max(0, value));
            Interlocked.Exchange(ref at, now);
        }
    }

    private void Run()
    {
        var token = _stopping.Token;
        Wasapi.IAudioClient? client = null;
        Wasapi.IAudioRenderClient? render = null;
        Wasapi.IAudioClock? clock = null;
        try
        {
            client = Wasapi.Activate(Id);
            Check(client.GetMixFormat(out IntPtr mix), "GetMixFormat");
            Wasapi.Format format;
            try
            {
                format = Wasapi.ReadFormat(mix);
                Check(client.Initialize(0 /* shared */, 0, 2_000_000 /* 200 ms */, 0, mix, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeCoTaskMem(mix); }
            Check(client.GetBufferSize(out uint bufferFrames), "GetBufferSize");
            render = Wasapi.Service<Wasapi.IAudioRenderClient>(client, Wasapi.IID_IAudioRenderClient);
            clock = Wasapi.Service<Wasapi.IAudioClock>(client, Wasapi.IID_IAudioClock);
            Check(clock.GetFrequency(out ulong frequency), "GetFrequency");

            int rate = format.SampleRate;
            var engine = new PlayoutEngine(_sourceRate, rate);
            _engine = engine;
            var chirp = TestSignal.Chirp(rate);
            var click = TestSignal.Click(rate);
            int fillFrames = (int)(FillUs * rate / 1_000_000);
            var stereo = new float[Math.Max(fillFrames, (int)bufferFrames) * 2];
            long written = 0;
            long lastTick = PcClock.NowUs();

            Check(client.Start(), "Start");
            Status = "playing";
            _log.Log(LogLevel.Info, $"Play everywhere: {Name} is in ({rate} Hz, {format.Channels} ch{(Bluetooth ? ", Bluetooth" : "")})");

            while (!token.IsCancellationRequested)
            {
                token.WaitHandle.WaitOne((int)TickMs);
                Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
                int frames = Math.Min(fillFrames - (int)padding, (int)(bufferFrames - padding));
                if (frames < rate / 1000) continue; // under a millisecond to do

                Check(clock.GetPosition(out ulong position, out ulong qpc), "GetPosition");
                long now = PcClock.NowUs();
                long atUs = qpc != 0 ? PcClock.QpcHnsToUs(qpc) : now;
                double played = position * (double)rate / frequency;
                long outUs = atUs + (long)((written - played) * 1_000_000 / rate) - now; // until the next frame leaves the device
                Track(ref _maxOutUs, ref _maxOutAtUs, outUs, now);
                long firstHeard = now + outUs + HiddenUs;

                var stats = engine.Render(stereo, frames, firstHeard, _delayUs(), (now - lastTick) / 1_000_000.0);
                lastTick = now;
                SilenceFrames += stats.SilenceFrames;
                SkippedFrames += stats.SkippedFrames;
                if (stats.Underrun) Underruns++;

                var span = stereo.AsSpan(0, frames * 2);
                if (Silenced) span.Clear();
                else if (Gain < 0.999f) for (int i = 0; i < span.Length; i++) span[i] *= Gain;
                var sounds = _sounds();
                if (sounds.Count > 0) TestSignal.Mix(span, 2, rate, firstHeard, sounds, chirp, click);

                Check(render.GetBuffer((uint)frames, out IntPtr buffer), "GetBuffer");
                Wasapi.WriteFrames(buffer, span, frames, format);
                Check(render.ReleaseBuffer((uint)frames, 0), "ReleaseBuffer");
                written += frames;
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Status = "error";
            Problem = ex is COMException { HResult: unchecked((int)0x88890004) } // AUDCLNT_E_DEVICE_INVALIDATED
                ? "unplugged or switched off"
                : ex.Message;
            _log.Log(LogLevel.Warn, $"Play everywhere: {Name} stopped ({Problem})");
        }
        catch { /* stopping */ }
        finally
        {
            try { client?.Stop(); } catch { }
            if (clock is not null) Marshal.ReleaseComObject(clock);
            if (render is not null) Marshal.ReleaseComObject(render);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (Status != "error") Status = "off";
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _thread.Join(1000);
        _stopping.Dispose();
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    /// <summary>
    /// This PC's active playback devices, minus virtual cables (the one Play everywhere captures,
    /// and any other: playing into a cable is never what anyone means) and Bluetooth hands-free
    /// endpoints (the phone-call profile: mono, 8–16 kHz, and opening it drops a headset out of
    /// its music profile).
    /// </summary>
    public static List<PcOutputDevice> ListOutputs()
    {
        var list = new List<PcOutputDevice>();
        foreach (var (id, name, enumerator) in Wasapi.Endpoints())
        {
            if (PcMicAudio.IsCableInput(name) || name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) continue;
            if (enumerator.Equals("BTHHFENUM", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new PcOutputDevice(id, name, enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    // ---- WASAPI interop for rendering -----------------------------------------------------

    internal static class Wasapi
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_IAudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        private static readonly PROPERTYKEY PKEY_Device_EnumeratorName = new() { fmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 24 };
        private const int CLSCTX_ALL = 0x17;

        public readonly record struct Format(int SampleRate, int Channels, int BitsPerSample, bool Float, int BlockAlign);

        public static IAudioClient Activate(string id)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            try
            {
                Check(enumerator.GetDevice(id, out IMMDevice device), "GetDevice");
                try
                {
                    var iid = IID_IAudioClient;
                    Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object unknown), "Activate");
                    return (IAudioClient)unknown;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        /// <summary>A device's own Windows volume (0..1, the same scale as Windows' slider) and mute.</summary>
        public static (float Level, bool Muted)? GetEndpointVolume(string id)
        {
            try
            {
                var volume = ActivateVolume(id);
                try
                {
                    if (volume.GetMasterVolumeLevelScalar(out float level) != 0) return null;
                    volume.GetMute(out bool muted);
                    return (level, muted);
                }
                finally { Marshal.ReleaseComObject(volume); }
            }
            catch { return null; }
        }

        /// <summary>Set a device's own Windows volume and/or mute. False if Windows refused.</summary>
        public static bool SetEndpointVolume(string id, float? level, bool? muted)
        {
            try
            {
                var volume = ActivateVolume(id);
                try
                {
                    var context = Guid.Empty;
                    bool ok = true;
                    if (level is { } l) ok &= volume.SetMasterVolumeLevelScalar(Math.Clamp(l, 0f, 1f), ref context) >= 0;
                    if (muted is { } m) ok &= volume.SetMute(m, ref context) >= 0;
                    return ok;
                }
                finally { Marshal.ReleaseComObject(volume); }
            }
            catch { return false; }
        }

        private static IAudioEndpointVolume ActivateVolume(string id)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            try
            {
                Check(enumerator.GetDevice(id, out IMMDevice device), "GetDevice");
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object unknown), "Activate");
                    return (IAudioEndpointVolume)unknown;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        public static T Service<T>(IAudioClient client, Guid iid) where T : class
        {
            Check(client.GetService(ref iid, out object service), "GetService");
            return (T)service;
        }

        public static Format ReadFormat(IntPtr wfx)
        {
            ushort tag = (ushort)Marshal.ReadInt16(wfx, 0);
            int channels = Marshal.ReadInt16(wfx, 2);
            int rate = Marshal.ReadInt32(wfx, 4);
            int blockAlign = Marshal.ReadInt16(wfx, 12);
            int bits = Marshal.ReadInt16(wfx, 14);
            bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.PtrToStructure<Guid>(wfx + 24) == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
            return new Format(rate, channels, bits, isFloat, blockAlign);
        }

        /// <summary>
        /// Stereo float → the device's own format. Left goes to even channels and right to odd
        /// ones (front, then rear, of a surround layout); a mono device gets the two mixed.
        /// </summary>
        public static void WriteFrames(IntPtr buffer, ReadOnlySpan<float> stereo, int frames, Format format)
        {
            int bytes = format.BitsPerSample / 8;
            for (int f = 0; f < frames; f++)
            {
                float l = Math.Clamp(stereo[f * 2], -1f, 1f), r = Math.Clamp(stereo[f * 2 + 1], -1f, 1f);
                IntPtr frame = buffer + f * format.BlockAlign;
                for (int c = 0; c < format.Channels; c++)
                {
                    float v = format.Channels == 1 ? (l + r) / 2 : (c % 2 == 0 ? l : r);
                    IntPtr at = frame + c * bytes;
                    if (format.Float && bytes == 4) Marshal.WriteInt32(at, BitConverter.SingleToInt32Bits(v));
                    else if (bytes == 2) Marshal.WriteInt16(at, (short)(v * 32767));
                    else if (bytes == 3)
                    {
                        int s = (int)(v * 8_388_607);
                        Marshal.WriteByte(at, (byte)s); Marshal.WriteByte(at, 1, (byte)(s >> 8)); Marshal.WriteByte(at, 2, (byte)(s >> 16));
                    }
                    else if (bytes == 4) Marshal.WriteInt32(at, (int)(v * 2_147_483_000));
                }
            }
        }

        /// <summary>Active playback endpoints: id, friendly name, and the bus that enumerated them (BTHENUM = Bluetooth).</summary>
        public static List<(string Id, string Name, string Enumerator)> Endpoints()
        {
            var list = new List<(string, string, string)>();
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            try
            {
                Check(enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection collection), "EnumAudioEndpoints");
                try
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out IMMDevice device) != 0) continue;
                        try
                        {
                            device.GetId(out string id);
                            list.Add((id, Property(device, PKEY_Device_FriendlyName), Property(device, PKEY_Device_EnumeratorName)));
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(collection); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
            return list;
        }

        private static string Property(IMMDevice device, PROPERTYKEY key)
        {
            if (device.OpenPropertyStore(0, out IPropertyStore store) != 0) return "";
            try
            {
                if (store.GetValue(ref key, out PROPVARIANT value) != 0) return "";
                try { return value.vt == 31 ? Marshal.PtrToStringUni(value.pointer) ?? "" : ""; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pointer; }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
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
        public interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint channelCount);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClock
        {
            [PreserveSig] int GetFrequency(out ulong frequency);
            [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
            [PreserveSig] int GetCharacteristics(out uint characteristics);
        }
    }
}
