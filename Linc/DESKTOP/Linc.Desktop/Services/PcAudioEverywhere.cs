using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// Play everywhere (v26, D-096): the PC's sound on its own speakers, its Bluetooth devices and
/// the phone's speakers at once, heard in step, with a volume for each.
///
/// <para><b>How the PC's own speakers can be delayed at all.</b> Speaker mode (D-093) already
/// routes Windows' sound into the virtual cable, so apps no longer play to the speakers directly.
/// Play everywhere captures the cable once and plays it out again itself: to the phone over
/// pc-audio (as speaker mode does) and to each chosen PC device through a <see cref="PcRenderSink"/>.
/// Because Linc renders every speaker, every speaker can be held back to the same moment.</para>
///
/// <para><b>In step.</b> Each block is stamped with its capture time; every speaker aims to be
/// heard at capture + <see cref="GroupDelay"/>, which is just big enough for the slowest one
/// (usually Bluetooth). What a device's clock can't show — a headset's own buffer, a TV's
/// processing — is measured by the phone's microphone (<c>audio.everywhere.test</c>), one device
/// at a time, and stored per device; the user can nudge any device by ear on top.</para>
///
/// <para>Sound is therefore behind the PC's picture by the group delay; the mixer says by how much.</para>
/// </summary>
public sealed class PcAudioEverywhere(
    IConnectionManager connection,
    ILogService log,
    PcSpeakerMode speaker,
    Func<AudioStreamOptions, Task<PcAudioStreamer?>> startAudio,
    Action stopAudio)
{
    private const long TestLeadUs = 1_500_000;
    private const long CalibrationTimeoutUs = 10_000_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, PcRenderSink> _sinks = new();
    private readonly Dictionary<string, long> _sinkRetryAtUs = new();
    private readonly GroupDelay _group = new();
    private EverywhereSettings? _settings;
    private List<PcOutputDevice> _pcDevices = [];
    private List<PhoneOutput> _phoneOutputs = [];
    private bool _phoneClockReady;
    private long _delayUs = GroupDelay.MinUs;
    private string? _error;
    private string? _note;
    private string? _cableId;
    private (float Level, bool Muted)? _cableRestore;
    private int _sourceRate = 48_000;
    private CancellationTokenSource? _monitorStop;

    private Dictionary<string, List<TestSound>> _soundsById = new();
    private string? _testMode;
    private int _testSeq;
    private long _testUntilUs;
    private bool _saidStateFailed;

    private sealed record PhoneOutput(string Id, string Name, bool Bluetooth, long NeedUs, string Status, double OffByUs);

    public bool On { get; private set; }

    public long DelayUs => Interlocked.Read(ref _delayUs);

    // ---- the phone's requests ---------------------------------------------------------------

    public async Task HandleSetAsync(JsonObject p)
    {
        try
        {
            if (p["on"] is JsonValue onValue && onValue.TryGetValue(out bool on))
            {
                if (on && !On) await EnableAsync();
                else if (!on && On) Disable("turned off on the phone");
            }
            if (p["device"] is JsonObject d && (string?)d["id"] is { Length: > 0 } id)
                UpdateDevice(id, d);
            if (p["marginMs"] is JsonValue margin && margin.TryGetValue(out int marginMs))
            {
                lock (_lock) Settings.MarginMs = Math.Clamp(marginMs, 0, 500);
                Save();
                log.Log(LogLevel.Info, $"Play everywhere: extra safety margin {marginMs} ms");
            }
            if ((bool?)p["calibrate"] == true) StartTest("calibrate");
            if ((bool?)p["clicks"] == true) StartTest("clicks");
            if ((bool?)p["resetAlignment"] == true) ResetAlignment();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Play everywhere: couldn't apply the phone's change: {ex.Message}");
        }
        await SendStateAsync();
    }

    /// <summary>The phone's outputs, what they need, and (after a test) what its microphone heard.</summary>
    public void OnReport(JsonObject p)
    {
        if (p["outputs"] is JsonArray outputs)
        {
            var list = new List<PhoneOutput>();
            foreach (var o in outputs.OfType<JsonObject>())
            {
                if ((string?)o["id"] is not { Length: > 0 } id) continue;
                list.Add(new PhoneOutput(id, (string?)o["name"] ?? id, (bool?)o["bluetooth"] ?? false,
                    ((long?)o["needUs"]) ?? 0, (string?)o["status"] ?? "", ((double?)o["offByUs"]) ?? 0));
            }
            bool added = false;
            lock (_lock)
            {
                _phoneOutputs = list;
                foreach (var o in list)
                {
                    if (Settings.Devices.ContainsKey(o.Id)) { Settings.Devices[o.Id].Name = o.Name; continue; }
                    Settings.Devices[o.Id] = new EverywhereDeviceSettings { Name = o.Name, Enabled = true };
                    added = true;
                    log.Log(LogLevel.Info, $"Play everywhere: the phone offers {o.Name}{(o.Bluetooth ? " (Bluetooth)" : "")}");
                }
            }
            if (added) Save();
        }
        if (p["clockReady"] is JsonValue ready && ready.TryGetValue(out bool clockReady)) _phoneClockReady = clockReady;
        if (p["clock"] is JsonObject clock)
        {
            log.Log(LogLevel.Info, $"Play everywhere: the phone has this PC's clock to within {(long?)clock["uncertaintyUs"] / 1000.0:0.0} ms, drifting {(double?)clock["driftPpm"]:0} ppm");
        }
        if (p["calibration"] is JsonObject calibration) FinishCalibration(calibration);
    }

    // ---- on / off -------------------------------------------------------------------------

    private async Task EnableAsync()
    {
        _error = speaker.Enable();
        if (_error is not null)
        {
            log.Log(LogLevel.Warn, $"Play everywhere: couldn't route the PC's sound through the cable ({_error})");
            return;
        }
        _cableId = PcVoiceAccess.CableRenderId();
        PinCable();
        var streamer = await startAudio(new AudioStreamOptions(_cableId, Clock: true, Tap: OnBlock));
        if (streamer is null)
        {
            RestoreCable();
            speaker.Disable();
            _error = "audio";
            return;
        }
        _sourceRate = streamer.SampleRate;
        On = true;
        _note = null;
        log.Log(LogLevel.Info, $"Play everywhere: on ({_sourceRate} Hz). The PC's own speakers now play through Linc so they can wait for the others");
        RefreshDevices(PcClock.NowUs());
        var stop = new CancellationTokenSource();
        _monitorStop = stop;
        new Thread(() => Monitor(stop.Token)) { IsBackground = true, Name = "linc-everywhere" }.Start();
    }

    /// <summary>Stop every speaker and give Windows its own output back.</summary>
    public void Disable(string why)
    {
        if (!On) return;
        On = false;
        _monitorStop?.Cancel();
        _monitorStop = null;
        PcRenderSink[] sinks;
        lock (_lock)
        {
            sinks = _sinks.Values.ToArray();
            _sinks.Clear();
            _soundsById = new();
            _testMode = null;
        }
        foreach (var sink in sinks) sink.Dispose();
        stopAudio();
        RestoreCable();
        speaker.Disable();
        log.Log(LogLevel.Info, $"Play everywhere: off ({why})");
    }

    /// <summary>
    /// The cable's own Windows volume scales everything Linc captures from it, so a cable left at
    /// 30% made every speaker quiet however far up its own slider went. While Play everywhere is
    /// on the cable sits at 100% (Windows' volume keys still turn it down, as a master for all the
    /// speakers at once) and gets its old level back afterwards.
    /// </summary>
    private void PinCable()
    {
        if (_cableId is null) return;
        var now = PcRenderSink.Wasapi.GetEndpointVolume(_cableId);
        if (now is not { } v || (v.Level >= 0.999f && !v.Muted)) return;
        _cableRestore = v;
        if (PcRenderSink.Wasapi.SetEndpointVolume(_cableId, 1f, false))
            log.Log(LogLevel.Info, $"Play everywhere: the cable was at {v.Level * 100:0}%{(v.Muted ? " (muted)" : "")}; set to 100% so nothing comes out quiet");
        else
            log.Log(LogLevel.Warn, $"Play everywhere: the cable is at {v.Level * 100:0}% and Windows wouldn't raise it; every speaker will be that much quieter");
    }

    private void RestoreCable()
    {
        if (_cableId is null || _cableRestore is not { } v) return;
        _cableRestore = null;
        PcRenderSink.Wasapi.SetEndpointVolume(_cableId, v.Level, v.Muted);
    }

    /// <summary>The phone's sound channel closed: nobody is controlling this any more.</summary>
    public void OnAudioEnded()
    {
        if (On) Disable("the phone went away");
    }

    // ---- the PC's sound, fanned out -------------------------------------------------------------

    private void OnBlock(SoundBlock block)
    {
        PcRenderSink[] sinks;
        lock (_lock) sinks = _sinks.Values.ToArray();
        foreach (var sink in sinks) sink.Enqueue(block);
    }

    private IReadOnlyList<TestSound> SoundsFor(string id) =>
        _soundsById.TryGetValue(id, out var list) ? list : [];

    // ---- devices ---------------------------------------------------------------------------

    private EverywhereSettings Settings => _settings ??= Load();

    private void RefreshDevices(long nowUs)
    {
        List<PcOutputDevice> found;
        try { found = PcRenderSink.ListOutputs(); }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Play everywhere: couldn't list this PC's speakers: {ex.Message}");
            return;
        }
        string? own = speaker.OwnOutputId;
        var start = new List<PcOutputDevice>();
        var stop = new List<PcRenderSink>();
        bool added = false;
        lock (_lock)
        {
            foreach (var d in found.Where(d => _pcDevices.All(k => k.Id != d.Id)))
                log.Log(LogLevel.Info, $"Play everywhere: found {d.Name}{(d.Bluetooth ? " (Bluetooth)" : "")}");
            foreach (var d in _pcDevices.Where(k => found.All(d => d.Id != k.Id)))
                log.Log(LogLevel.Info, $"Play everywhere: {d.Name} is gone");
            _pcDevices = found;
            foreach (var d in found)
            {
                if (!Settings.Devices.TryGetValue(d.Id, out var s))
                {
                    // On by default: the user's own output, and any Bluetooth speaker or headset.
                    // Other outputs (a monitor's speakers, a second sound card) are offered, off.
                    s = new EverywhereDeviceSettings { Name = d.Name, Enabled = d.Id == own || d.Bluetooth };
                    Settings.Devices[d.Id] = s;
                    added = true;
                }
                s.Name = d.Name;
                bool running = _sinks.TryGetValue(d.Id, out var sink);
                if (running && !sink!.Alive)
                {
                    // It died (unplugged, switched off, taken by an exclusive-mode app): retry later.
                    _sinks.Remove(d.Id);
                    stop.Add(sink);
                    _sinkRetryAtUs[d.Id] = nowUs + 10_000_000;
                    running = false;
                }
                if (s.Enabled && !running && (!_sinkRetryAtUs.TryGetValue(d.Id, out long at) || nowUs >= at))
                    start.Add(d);
                if (!s.Enabled && running)
                {
                    _sinks.Remove(d.Id);
                    stop.Add(sink!);
                }
            }
            foreach (var id in _sinks.Keys.Where(id => found.All(d => d.Id != id)).ToList())
            {
                stop.Add(_sinks[id]);
                _sinks.Remove(id);
            }
        }
        foreach (var sink in stop) sink.Dispose();
        foreach (var d in start)
        {
            var sink = new PcRenderSink(d, _sourceRate, () => DelayUs, () => SoundsFor(d.Id), log);
            lock (_lock) _sinks[d.Id] = sink;
        }
        if (added) Save();
        ApplySettings();
    }

    private void UpdateDevice(string id, JsonObject d)
    {
        lock (_lock)
        {
            if (!Settings.Devices.TryGetValue(id, out var s))
            {
                s = new EverywhereDeviceSettings { Name = id };
                Settings.Devices[id] = s;
            }
            if (d["enabled"] is JsonValue e && e.TryGetValue(out bool enabled)) s.Enabled = enabled;
            int? volume = d["volume"] is JsonValue v && v.TryGetValue(out int vol) ? Math.Clamp(vol, 0, 100) : null;
            bool? muted = d["muted"] is JsonValue m && m.TryGetValue(out bool mute) ? mute : null;
            if (volume is { } x) s.Volume = x;
            if (muted is { } y) s.Muted = y;
            // A PC speaker's level is its own Windows volume, so the mixer, Windows' mixer and the
            // speaker's keys all agree, and Windows does the work instead of Linc's sample loop.
            if ((volume is not null || muted is not null) && _pcDevices.Any(p => p.Id == id)
                && !PcRenderSink.Wasapi.SetEndpointVolume(id, volume / 100f, muted))
                log.Log(LogLevel.Warn, $"Play everywhere: Windows wouldn't change {s.Name}'s volume");
            if (d["manualMs"] is JsonValue n && n.TryGetValue(out double manualMs))
            {
                s.ManualUs = (long)Math.Clamp(manualMs * 1000, -500_000, 500_000);
                if (_sinks.TryGetValue(id, out var sink)) sink.Realign();
                log.Log(LogLevel.Info, $"Play everywhere: {s.Name} nudged to {s.ManualUs / 1000.0:+0.0;-0.0;0} ms by hand");
            }
        }
        Save();
        if (On) RefreshDevices(PcClock.NowUs());
    }

    private void ApplySettings()
    {
        lock (_lock)
        {
            bool testing = _testMode is not null;
            foreach (var (id, sink) in _sinks)
            {
                if (!Settings.Devices.TryGetValue(id, out var s)) continue;
                sink.HiddenUs = s.HiddenUs;
                sink.Gain = 1; // the level is the device's own Windows volume (see UpdateDevice)
                sink.Silenced = testing;
            }
        }
    }

    private void ResetAlignment()
    {
        lock (_lock)
        {
            foreach (var s in Settings.Devices.Values) { s.AutoUs = 0; s.ManualUs = 0; s.Calibrated = false; }
            foreach (var sink in _sinks.Values) sink.Realign();
            _note = "Alignment reset. Run \"Align automatically\" to measure each speaker again.";
        }
        Save();
        log.Log(LogLevel.Info, "Play everywhere: alignment reset for every speaker");
    }

    // ---- the monitor --------------------------------------------------------------------------

    private void Monitor(CancellationToken token)
    {
        long lastScan = 0, lastState = 0, lastWatch = 0, lastSummary = PcClock.NowUs();
        while (!token.WaitHandle.WaitOne(500))
        {
            try
            {
                long now = PcClock.NowUs();
                if (now - lastScan >= 5_000_000) { lastScan = now; RefreshDevices(now); }
                if (now - lastWatch >= 2_000_000) { lastWatch = now; KeepCableDefault(); }
                ApplySettings();
                bool changed = UpdateGroupDelay(now);
                EndTestIfDone(now);
                if (changed || now - lastState >= 2_000_000)
                {
                    lastState = now;
                    _ = SendStateAsync();
                }
                if (now - lastSummary >= 30_000_000) { lastSummary = now; LogSummary(); }
            }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"Play everywhere: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Windows moves the default output when a headset connects; apps would then play to it
    /// directly, out of step. Put the cable back, and play the new device in step instead.
    /// </summary>
    private void KeepCableDefault()
    {
        if (_cableId is null) return;
        string? current = PcVoiceAccess.DefaultRenderId();
        if (current is null || current == _cableId) return;
        string name;
        lock (_lock)
        {
            name = _pcDevices.FirstOrDefault(d => d.Id == current)?.Name ?? "another device";
            if (Settings.Devices.TryGetValue(current, out var s) && !s.Enabled)
            {
                s.Enabled = true; // the user just chose it in Windows: they want to hear it
            }
        }
        if (PcVoiceAccess.SetDefaultEndpoint(_cableId))
            log.Log(LogLevel.Info, $"Play everywhere: Windows switched its output to {name}; Linc plays it in step with the others instead");
        else
            log.Log(LogLevel.Warn, $"Play everywhere: Windows switched its output to {name} and wouldn't switch back; that device is out of step until Play everywhere is turned off and on");
        Save();
    }

    private bool UpdateGroupDelay(long now)
    {
        var needs = new List<(string, long)>();
        lock (_lock)
        {
            foreach (var sink in _sinks.Values.Where(s => s.Status == "playing"))
                needs.Add((sink.Name, sink.NeedUs));
            foreach (var o in _phoneOutputs)
                if (Settings.Devices.TryGetValue(o.Id, out var s) && s.Enabled && o.NeedUs > 0)
                    needs.Add((o.Name, o.NeedUs));
        }
        if (!_group.Update(needs, Settings.MarginMs * 1000L, now)) return false;
        long old = Interlocked.Exchange(ref _delayUs, _group.CurrentUs);
        log.Log(LogLevel.Info, $"Play everywhere: every speaker now plays {_group.CurrentUs / 1000} ms after the PC (was {old / 1000}; set by {_group.Reason ?? "the margin"})");
        return true;
    }

    private void LogSummary()
    {
        lock (_lock)
        {
            foreach (var sink in _sinks.Values)
            {
                log.Log(LogLevel.Info, $"Play everywhere: {sink.Name}: {sink.Status}, within {Math.Abs(sink.OffByUs) / 1000:0.0} ms, needs {sink.NeedUs / 1000} ms" +
                    (sink.SkippedFrames + sink.Underruns > 0 ? $", {sink.Underruns} gaps, {sink.SkippedFrames} frames skipped late" : ""));
                sink.SkippedFrames = 0; sink.SilenceFrames = 0; sink.Underruns = 0;
            }
            foreach (var o in _phoneOutputs)
                log.Log(LogLevel.Info, $"Play everywhere: {o.Name} (phone): {o.Status}, within {Math.Abs(o.OffByUs) / 1000:0.0} ms, needs {o.NeedUs / 1000} ms");
        }
    }

    // ---- alignment ---------------------------------------------------------------------------

    private void StartTest(string mode)
    {
        if (!On) return;
        List<string> ids;
        lock (_lock)
        {
            if (_testMode is not null) return; // one at a time
            if (mode == "calibrate" && !_phoneClockReady)
            {
                _note = "The phone is still learning the PC's clock. Try again in a few seconds.";
                return;
            }
            ids = _sinks.Values.Where(s => s.Status == "playing").Select(s => s.Id)
                .Concat(_phoneOutputs.Where(o => Settings.Devices.TryGetValue(o.Id, out var s) && s.Enabled).Select(o => o.Id))
                .ToList();
            if (ids.Count == 0) { _note = "No speaker is playing yet."; return; }
            long start = PcClock.NowUs() + DelayUs + TestLeadUs;
            var sounds = mode == "calibrate" ? Calibration.Schedule(ids, start) : Calibration.Clicks(ids, start);
            _soundsById = sounds.GroupBy(s => s.DeviceId).ToDictionary(g => g.Key, g => g.ToList());
            _testMode = mode;
            _testSeq++;
            _testUntilUs = sounds.Max(s => s.AtUs) + 300_000;
            _note = mode == "calibrate"
                ? "Listening… keep the phone where you'll be, and keep the room quiet for a few seconds."
                : "Listen: every speaker clicks at once. Nudge any that's early or late.";

            var list = new JsonArray();
            foreach (var s in sounds)
                list.Add(new JsonObject { ["id"] = s.DeviceId, ["atUs"] = s.AtUs, ["kind"] = s.Kind == TestSoundKind.Chirp ? "chirp" : "click" });
            var payload = new JsonObject
            {
                ["seq"] = _testSeq,
                ["mode"] = mode,
                ["sounds"] = list,
                ["listenFromUs"] = start - 300_000,
                ["listenToUs"] = _testUntilUs + 700_000,
            };
            _ = SendAsync(MessageType.AudioEverywhereTest, payload);
        }
        ApplySettings(); // program sound off while the test sounds play
        log.Log(LogLevel.Info, mode == "calibrate"
            ? $"Play everywhere: aligning {ids.Count} speakers with the phone's microphone"
            : $"Play everywhere: test clicks on {ids.Count} speakers");
    }

    private void EndTestIfDone(long now)
    {
        lock (_lock)
        {
            if (_testMode is null) return;
            long end = _testMode == "calibrate" ? _testUntilUs + CalibrationTimeoutUs : _testUntilUs;
            if (now < end) return;
            if (_testMode == "calibrate")
            {
                _note = "The phone didn't report what it heard. Check Linc may use the microphone, then try again.";
                log.Log(LogLevel.Warn, "Play everywhere: the phone never reported the alignment");
            }
            else _note = null;
            _testMode = null;
            _soundsById = new();
        }
        ApplySettings();
    }

    private void FinishCalibration(JsonObject c)
    {
        var aligned = new List<string>();
        var unclear = new List<string>();
        lock (_lock)
        {
            if (_testMode != "calibrate" || (int?)c["seq"] != _testSeq) return;
            if ((string?)c["error"] is { Length: > 0 } error)
            {
                _note = error;
                log.Log(LogLevel.Warn, $"Play everywhere: alignment failed on the phone: {error}");
            }
            foreach (var r in (c["results"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string id = (string?)r["id"] ?? "";
                if (!Settings.Devices.TryGetValue(id, out var s)) continue;
                var result = new CalibrationResult(id, (bool?)r["ok"] ?? false, (long?)r["errorUs"] ?? 0, (double?)r["snr"] ?? 0);
                if (Calibration.Apply(s, result))
                {
                    aligned.Add(s.Name);
                    log.Log(LogLevel.Info, $"Play everywhere: {s.Name} was heard {result.ErrorUs / 1000.0:+0.0;-0.0;0} ms off; its hidden latency is now {s.AutoUs / 1000.0:0.0} ms (clarity {result.Snr:0})");
                }
                else
                {
                    unclear.Add(s.Name);
                    log.Log(LogLevel.Warn, $"Play everywhere: couldn't hear {s.Name} clearly (clarity {result.Snr:0}); left as it was");
                }
            }
            foreach (var sink in _sinks.Values) sink.Realign();
            _testMode = null;
            _soundsById = new();
            if (_note is null || !_note.StartsWith("The phone", StringComparison.Ordinal))
                _note = (aligned.Count > 0 ? $"Aligned {string.Join(", ", aligned)}." : "")
                    + (unclear.Count > 0 ? $" Couldn't hear {string.Join(", ", unclear)} clearly: turn it up or bring the phone closer, then try again." : "");
        }
        Save();
        ApplySettings();
        _ = SendStateAsync();
    }

    // ---- state for the phone ------------------------------------------------------------------

    public async Task SendStateAsync()
    {
        // Each PC speaker's real Windows volume, read now: someone may have used its keys.
        List<PcOutputDevice> pcDevices;
        lock (_lock) pcDevices = _pcDevices.ToList();
        var levels = pcDevices.ToDictionary(d => d.Id, d => PcRenderSink.Wasapi.GetEndpointVolume(d.Id));
        JsonObject payload;
        lock (_lock)
        {
            var devices = new JsonArray();
            foreach (var d in _pcDevices)
            {
                var s = Settings.Devices.TryGetValue(d.Id, out var found) ? found : new EverywhereDeviceSettings { Name = d.Name };
                if (levels.TryGetValue(d.Id, out var level) && level is { } l)
                {
                    s.Volume = (int)Math.Round(l.Level * 100);
                    s.Muted = l.Muted;
                }
                _sinks.TryGetValue(d.Id, out var sink);
                devices.Add(Device(d.Id, d.Name, "pc", d.Bluetooth, s,
                    sink?.Status ?? (s.Enabled && On ? "starting" : "off"), sink?.OffByUs ?? 0, sink?.NeedUs ?? 0, sink?.Problem));
            }
            foreach (var o in _phoneOutputs)
            {
                var s = Settings.Devices.TryGetValue(o.Id, out var found) ? found : new EverywhereDeviceSettings { Name = o.Name };
                devices.Add(Device(o.Id, o.Name, "phone", o.Bluetooth, s, o.Status, o.OffByUs, o.NeedUs, null));
            }
            payload = new JsonObject
            {
                ["on"] = On,
                ["delayMs"] = DelayUs / 1000,
                ["marginMs"] = Settings.MarginMs,
                ["testing"] = _testMode,
                ["devices"] = devices,
            };
            if (_error is not null) payload["error"] = _error;
            if (_note is not null) payload["note"] = _note;
        }
        await SendAsync(MessageType.AudioEverywhereState, payload);
    }

    private static JsonObject Device(string id, string name, string kind, bool bluetooth, EverywhereDeviceSettings s,
        string status, double offByUs, long needUs, string? problem)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["kind"] = kind,
            ["bluetooth"] = bluetooth,
            ["enabled"] = s.Enabled,
            ["volume"] = s.Volume,
            ["muted"] = s.Muted,
            ["autoMs"] = Math.Round(s.AutoUs / 1000.0, 1),
            ["manualMs"] = Math.Round(s.ManualUs / 1000.0, 1),
            ["hiddenUs"] = s.HiddenUs,
            ["calibrated"] = s.Calibrated,
            ["status"] = status,
            ["offByMs"] = Math.Round(offByUs / 1000.0, 1),
            ["needMs"] = needUs / 1000,
        };
        if (problem is not null) o["problem"] = problem;
        return o;
    }

    private async Task SendAsync(string type, JsonObject payload)
    {
        try
        {
            await connection.SendToPhoneAsync(Envelope.Create(type, payload), CancellationToken.None);
            _saidStateFailed = false;
        }
        catch (Exception ex)
        {
            if (_saidStateFailed) return; // once, not every two seconds
            _saidStateFailed = true;
            log.Log(LogLevel.Warn, $"Play everywhere: couldn't reach the phone ({ex.Message})");
        }
    }

    // ---- settings file ------------------------------------------------------------------------

    private static string? SettingsPath =>
        PcSpeakerMode.StoreRoot is { } root ? Path.Combine(root, "audio-everywhere.json") : null;

    private EverywhereSettings Load()
    {
        try
        {
            if (SettingsPath is { } path && File.Exists(path))
                return JsonSerializer.Deserialize<EverywhereSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Play everywhere: its saved settings couldn't be read ({ex.Message}); starting fresh");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            if (SettingsPath is not { } path) return;
            string json;
            lock (_lock) json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Play everywhere: couldn't save its settings ({ex.Message})");
        }
    }
}
