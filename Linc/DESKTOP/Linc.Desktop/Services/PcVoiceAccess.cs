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
    private readonly Queue<float> _ring = new();

    /// <summary>
    /// Who wants the phone as this PC's microphone (D-075). The phone mic streams while Voice
    /// Access or the Microphone tool wants it, or — in PC Remote with "Phone" chosen — while some
    /// PC app is actually listening. The cable is the Windows default while anyone wants it.
    /// </summary>
    [Flags]
    private enum Owner { None = 0, Voice = 1, Tool = 2, Auto = 4 }
    private Owner _owners;
    private bool _appListening;
    private System.Threading.Timer? _listenWatch;
    private int _quietTicks;
    private readonly SemaphoreSlim _apply = new(1, 1);

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
        // While the microphone is being set up, nothing else happens: a second tap used to start
        // Voice Access on the PC's own mic halfway through the download.
        if (CableSetup.IsBusy) { await SendStateAsync(); return; }
        string? error = null;
        try
        {
            if (!on)
            {
                await ReleaseAsync(Owner.Voice);
                StopVoiceAccess();
                RestoreVoiceAccessMic(_log);
            }
            else if (!Available)
            {
                error = "This PC doesn't have Voice Access (it needs Windows 11 22H2 or later).";
            }
            else
            {
                bool usePhone = phoneMic && await EnsureCableAsync(installCable, e => error = e);
                bool changed;
                if (usePhone)
                {
                    await AcquireAsync(Owner.Voice);
                    // Voice Access does NOT follow the default microphone: it keeps its own choice
                    // (VoiceAccessMicrophoneId), which pointed at the PC's mic — so the phone's audio
                    // reached the cable and Voice Access never heard it (2026-10-10).
                    changed = FindCable() is { } pair && PointVoiceAccessAt(Release(pair));
                }
                else
                {
                    await ReleaseAsync(Owner.Voice);
                    changed = RestoreVoiceAccessMic(_log);
                }
                // It reads its microphone when it starts, so a change means a restart.
                if (changed && Running) StopVoiceAccess();
                StartVoiceAccess();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Log(LogLevel.Warn, $"Voice Access: {ex.Message}");
        }
        await SendStateAsync(error: error);
    }

    /// <summary>The Microphone tool (<c>pc.mic.set</c>, v23): the phone as this PC's mic for any app.</summary>
    public async Task SetToolAsync(bool on, bool installCable)
    {
        if (CableSetup.IsBusy) { await SendStateAsync(); return; }
        string? error = null;
        try
        {
            if (!on) await ReleaseAsync(Owner.Tool);
            else if (await EnsureCableAsync(installCable, e => error = e)) await AcquireAsync(Owner.Tool);
            else error ??= "This PC needs its virtual microphone set up first.";
        }
        catch (Exception ex) { error = ex.Message; _log.Log(LogLevel.Warn, $"Phone microphone: {ex.Message}"); }
        await SendStateAsync(error: error);
    }

    /// <summary>
    /// PC Remote with "Phone" chosen (<c>pc.mic.auto</c>, v23): the cable is the default for the
    /// session, so whatever a PC app opens is the phone; the phone mic itself only streams while
    /// some app is listening (Windows' own microphone-in-use records, polled once a second).
    /// </summary>
    public async Task SetAutoAsync(bool on, bool installCable = false)
    {
        if (CableSetup.IsBusy) { await SendStateAsync(); return; }
        string? error = null;
        try
        {
            if (on && await EnsureCableAsync(installCable, e => error = e))
            {
                await AcquireAsync(Owner.Auto);
                _listenWatch ??= new System.Threading.Timer(_ => _ = WatchListenersAsync(), null, 1000, 1000);
            }
            else
            {
                _listenWatch?.Dispose();
                _listenWatch = null;
                _appListening = false;
                await ReleaseAsync(Owner.Auto);
            }
        }
        catch (Exception ex) { error = ex.Message; _log.Log(LogLevel.Warn, $"Phone microphone: {ex.Message}"); }
        await SendStateAsync(error: error);
    }

    private async Task WatchListenersAsync()
    {
        bool listening = PcMicAudio.MicInUseByOtherApps();
        if (listening) _quietTicks = 0;
        else if (_appListening && ++_quietTicks < 5) return; // a short pause isn't the end of a call
        if (listening == _appListening) return;
        _appListening = listening;
        _log.Log(LogLevel.Info, listening
            ? "Phone microphone: a PC app started listening — streaming the phone's mic"
            : "Phone microphone: nothing is listening any more — phone mic paused");
        await ApplyAsync();
        await SendStateAsync();
    }

    /// <summary>Make sure a cable exists, setting it up first if the user agreed. False if there is none.</summary>
    private async Task<bool> EnsureCableAsync(bool install, Action<string?> onError)
    {
        if (CablePresent()) return true;
        if (!install) return false;
        // The user agreed on the phone: set the cable up now (D-071). The phone shows the
        // download's progress, then what to click, while Windows asks on the PC.
        _progress = 0;
        await SendStateAsync(setup: "installing", progress: 0);
        int lastSent = 0;
        var progress = new Progress<int>(p =>
        {
            _progress = p;
            if (p - lastSent < 5 && p < 100) return;
            lastSent = p;
            _ = SendStateAsync(setup: "installing", progress: p);
        });
        var result = await CableSetup.InstallAsync(progress);
        _setupState = result.Ok ? null : "failed";
        if (!result.Ok) onError(result.Message);
        return result.Ok && CablePresent();
    }

    private async Task AcquireAsync(Owner owner) { lock (_gate) _owners |= owner; await ApplyAsync(); }
    private async Task ReleaseAsync(Owner owner) { lock (_gate) _owners &= ~owner; await ApplyAsync(); }

    /// <summary>
    /// Bring the PC in line with who wants the phone mic: the cable as the default microphone while
    /// anyone does, and the phone's audio streaming while Voice Access, the tool, or a listening app
    /// needs it. The user's own default goes to disk before it is switched (crash-safe).
    /// </summary>
    private async Task ApplyAsync()
    {
        await _apply.WaitAsync();
        try
        {
            Owner owners;
            lock (_gate) owners = _owners;
            bool wantDefault = owners != Owner.None;
            bool wantStream = (owners & (Owner.Voice | Owner.Tool)) != 0 || ((owners & Owner.Auto) != 0 && _appListening);

            if (wantDefault && FindCable() is { } pair)
            {
                string captureId = Release(pair);
                var current = DefaultCaptureId();
                if (current is not null && current != captureId)
                {
                    if (!File.Exists(RestoreFile)) WriteRestoreFile(current);
                    SetDefaultCapture(captureId, _log);
                }
            }
            if (wantStream && !Streaming) await StartStreamAsync();
            if (!wantStream && Streaming) StopStream();
            if (!wantDefault) RestorePendingDefaultMic(_log);
        }
        finally { _apply.Release(); }
    }

    private bool Streaming { get { lock (_gate) return _channel is not null; } }

    /// <summary>The cable's capture id, releasing the render device FindCable handed back.</summary>
    private static string Release((IMMDevice Render, string CaptureId) pair)
    {
        Marshal.ReleaseComObject(pair.Render);
        return pair.CaptureId;
    }

    /// <summary>The download's percentage while the cable is being set up.</summary>
    private volatile int _progress;

    public Task SendStateAsync(string? error = null, string? setup = null, int? progress = null)
    {
        bool running = Running;
        bool hasCable = CablePresent();
        Owner owners;
        lock (_gate) owners = _owners;
        var payload = new JsonObject
        {
            ["available"] = Available,
            ["on"] = running,
            ["mic"] = (owners & Owner.Voice) != 0 ? "phone" : "pc",
            ["cable"] = hasCable,
            // v21 addition (D-071): whether the PC can set the cable up itself, and how it's going.
            ["setup"] = setup ?? (CableSetup.IsBusy ? "installing" : hasCable ? "ready" : _setupState ?? "needed"),
            // v23 (D-075): the phone as the PC's microphone beyond Voice Access.
            ["micOn"] = Streaming,
            ["tool"] = (owners & Owner.Tool) != 0,
            ["auto"] = (owners & Owner.Auto) != 0,
            ["listening"] = _appListening,
        };
        if (CableSetup.IsBusy || progress is not null) payload["progress"] = progress ?? _progress;
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
                process.WaitForExit(3000);
                _log.Log(LogLevel.Info, "Voice Access: stopped");
            }
            catch { /* already gone */ }
            finally { process.Dispose(); }
        }
    }

    // ---- Voice Access's own microphone choice ------------------------------------

    private const string VoiceAccessKey = @"Software\Microsoft\VoiceAccess";

    private static string VoiceAccessRestoreFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc", "voiceaccess-restore-vamic.txt");

    /// <summary>Point Voice Access at the cable, keeping the user's own choice on disk. True if it changed.</summary>
    private bool PointVoiceAccessAt(string captureId)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(VoiceAccessKey);
        var target = PcMicAudio.VoiceAccessMicId(captureId);
        var current = key.GetValue("VoiceAccessMicrophoneId") as string;
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(VoiceAccessRestoreFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VoiceAccessRestoreFile)!);
            File.WriteAllText(VoiceAccessRestoreFile, current ?? "");
        }
        key.SetValue("VoiceAccessMicrophoneId", target, Microsoft.Win32.RegistryValueKind.String);
        _log.Log(LogLevel.Info, "Voice Access: now listening to the phone (its microphone set to the virtual cable)");
        return true;
    }

    /// <summary>Give Voice Access back the microphone the user had chosen. True if it changed.</summary>
    public static bool RestoreVoiceAccessMic(ILogService log)
    {
        if (!File.Exists(VoiceAccessRestoreFile)) return false;
        try
        {
            var original = File.ReadAllText(VoiceAccessRestoreFile).Trim();
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(VoiceAccessKey);
            if (original.Length > 0) key.SetValue("VoiceAccessMicrophoneId", original, Microsoft.Win32.RegistryValueKind.String);
            else key.DeleteValue("VoiceAccessMicrophoneId", throwOnMissingValue: false);
            File.Delete(VoiceAccessRestoreFile);
            log.Log(LogLevel.Info, "Voice Access: back on this PC's own microphone");
            return true;
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Voice Access: couldn't put its microphone back: {ex.Message}");
            return false;
        }
    }

    // ---- the phone's microphone stream -------------------------------------------

    private async Task StartStreamAsync()
    {
        if (FindCable() is not { } pair) return;
        lock (_gate) { if (_channel is not null) { Marshal.ReleaseComObject(pair.Render); return; } }
        Stream channel;
        try { channel = await _connection.OpenPhoneDialledChannelAsync(Channel, CancellationToken.None); }
        catch { Marshal.ReleaseComObject(pair.Render); throw; }
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _channel = channel;
            _stop = stop;
            _ring.Clear();
        }
        _reader = new Thread(() => Read(channel, stop.Token)) { IsBackground = true, Name = "linc-mic-reader" };
        _renderer = new Thread(() => Render(pair.Render, stop.Token)) { IsBackground = true, Name = "linc-mic-render" };
        _reader.Start();
        _renderer.Start();
        _log.Log(LogLevel.Info, "Phone microphone: streaming the phone's mic into the virtual cable");
    }

    private void StopStream()
    {
        CancellationTokenSource? stop;
        Stream? channel;
        lock (_gate)
        {
            stop = _stop; channel = _channel;
            _stop = null; _channel = null;
        }
        if (stop is null) return;
        stop.Cancel();
        try { channel?.Dispose(); } catch { }
        _reader?.Join(1000);
        _renderer?.Join(1000);
        _log.Log(LogLevel.Info, "Phone microphone: stream stopped");
    }

    /// <summary>Everything off: no stream, the user's own default mic and Voice Access mic back.</summary>
    public void StopMic()
    {
        _listenWatch?.Dispose();
        _listenWatch = null;
        lock (_gate) _owners = Owner.None;
        _appListening = false;
        StopStream();
        RestorePendingDefaultMic(_log);
        RestoreVoiceAccessMic(_log);
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
            _log.Log(LogLevel.Info, $"Phone microphone: the stream ended ({ex.Message})");
            // The phone hung up (or went away): drop the stream; the owners stay, so Mirror's
            // auto mode re-opens it the next time an app listens.
            _ = Task.Run(() => { StopStream(); _ = SendStateAsync(); });
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

    private static string RestoreFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc", "voiceaccess-restore-mic.txt");

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
        string? id;
        try { id = File.Exists(RestoreFile) ? File.ReadAllText(RestoreFile).Trim() : null; }
        catch { return; }
        if (string.IsNullOrEmpty(id)) return;
        SetDefaultCapture(id, log);
        DeleteRestoreFile();
        log.Log(LogLevel.Info, "Phone microphone: this PC's own microphone is the default again");
    }

    /// <summary>On exit and at start: the default mic and Voice Access's mic both put back (D-075).</summary>
    public static void RestoreAllMics(ILogService log)
    {
        RestorePendingDefaultMic(log);
        RestoreVoiceAccessMic(log);
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

    private static string? DefaultCaptureId()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(1 /* eCapture */, 0 /* eConsole */, out IMMDevice device) != 0) return null;
            device.GetId(out string id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    /// <summary>
    /// Make <paramref name="id"/> the default recording device for every role. IPolicyConfig is
    /// the interface Windows' own Sound settings use; it is undocumented but has been stable
    /// since Windows 7, and it is the only way to switch the default device.
    /// </summary>
    private static void SetDefaultCapture(string id, ILogService log)
    {
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_PolicyConfigClient)!)!;
            try { for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role); }
            finally { Marshal.ReleaseComObject(policy); }
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Voice Access: couldn't switch the default microphone: {ex.Message}");
        }
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
