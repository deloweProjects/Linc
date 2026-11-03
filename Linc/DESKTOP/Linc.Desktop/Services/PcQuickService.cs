using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Linc.Desktop.Services;

/// <summary>
/// The PC's quick settings and shortcuts as tiles on the phone's Home › Control (2026-11-03; the
/// owner: "a section for those quick tiles from the PC and its quick settings too", to "make the
/// user feel as if their phone really is here"). The PC owns the list (<see cref="Catalog"/>);
/// the phone draws what <c>pc.quick.state</c> carries and sends back an id.
///
/// <para>As <see cref="PcControlService"/>: the phone is trusted to ask, never to have checked —
/// an id that isn't in the catalog is refused here, and every setting is read back from Windows
/// after it is written, so the phone shows what actually happened.</para>
/// </summary>
public sealed class PcQuickService(IConnectionManager connection, ILogService log, PcHotspot hotspot)
{
    private bool _attached;

    /// <summary>Every id the phone may send, in the order the phone shows them.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, string Group, string Kind)> Catalog =
    [
        ("dark", "Dark mode", "setting", "toggle"),
        ("nightlight", "Night light", "setting", "toggle"),
        ("wifi", "Wi-Fi", "setting", "toggle"),
        ("bluetooth", "Bluetooth", "setting", "toggle"),
        ("dnd", "Do not disturb", "setting", "toggle"),
        ("hotspot", "PC hotspot", "setting", "toggle"),
        ("lock", "Lock", "shortcut", "action"),
        ("desktop", "Show desktop", "shortcut", "action"),
        ("snip", "Snip", "shortcut", "action"),
        ("taskview", "Task view", "shortcut", "action"),
        ("clipboard", "Send PC clipboard", "shortcut", "action"),
        ("openlinc", "Open Linc", "shortcut", "action"),
    ];

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        connection.CompanionMessageReceived += OnMessage;
        // The hotspot also turns on and off by itself (the phone near, the 3-minute give-up) and
        // from the bubble: the phone's tile follows.
        hotspot.Changed += () => { if (connection.Current is { Companion.V: >= 27 }) _ = Task.Run(() => SendStateAsync(null)); };
    }

    private void OnMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.PcQuickGet:
                _ = Task.Run(() => SendStateAsync(null));
                break;
            case MessageType.PcQuickSet:
                string? id = (string?)envelope.Payload["id"];
                bool? on = envelope.Payload["on"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
                _ = Task.Run(async () => await SendStateAsync(await RunAsync(id, on)));
                break;
        }
    }

    /// <summary>Runs one validated id. Returns null on success, else what to tell the phone.</summary>
    private async Task<string?> RunAsync(string? id, bool? on)
    {
        if (id is null || Catalog.All(c => c.Id != id)) return "unknown";
        try
        {
            string? error = id switch
            {
                "dark" => SetDark(on ?? !IsDark()),
                "nightlight" => PcNightLight.Set(on ?? !(PcNightLight.IsOn() ?? false)),
                "wifi" => await SetRadioAsync(RadioKind.WiFi, on),
                "bluetooth" => await SetRadioAsync(RadioKind.Bluetooth, on),
                "dnd" => SetDnd(on ?? !IsDnd()),
                "hotspot" => await SetHotspotAsync(on ?? hotspot.Current == PcHotspot.Mode.Off),
                "lock" => LockWorkStation() ? null : "denied",
                "desktop" => Keys(VkLWin, 0x44),
                "snip" => Keys(VkLWin, VkShift, 0x53),
                "taskview" => Keys(VkLWin, 0x09),
                "clipboard" => OnLinc(m => m.SendClipboardToPhone()),
                "openlinc" => OnLinc(m => m.ShowPage(null)),
                _ => "unknown",
            };
            log.Log(error is null ? LogLevel.Info : LogLevel.Warn,
                $"Quick tiles: '{id}'{(on is { } o ? (o ? " on" : " off") : "")} from the phone{(error is null ? "" : $" failed ({error})")}");
            return error;
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Quick tiles: '{id}' failed: {ex.Message}");
            return ex.Message;
        }
    }

    private async Task SendStateAsync(string? error)
    {
        var items = new JsonArray();
        foreach (var (id, label, group, kind) in Catalog)
        {
            var item = new JsonObject { ["id"] = id, ["label"] = label, ["group"] = group, ["kind"] = kind, ["available"] = true };
            try
            {
                switch (id)
                {
                    case "dark": item["on"] = IsDark(); break;
                    case "nightlight":
                        if (PcNightLight.IsOn() is { } night) item["on"] = night;
                        else { item["available"] = false; item["note"] = "Not on this PC"; }
                        break;
                    case "wifi":
                    case "bluetooth":
                        var radio = await FindRadioAsync(id == "wifi" ? RadioKind.WiFi : RadioKind.Bluetooth);
                        if (radio is null) { item["available"] = false; item["note"] = "No radio"; }
                        else item["on"] = radio.State == RadioState.On;
                        break;
                    case "dnd": item["on"] = IsDnd(); break;
                    case "hotspot":
                        item["on"] = hotspot.Current != PcHotspot.Mode.Off;
                        if (hotspot.Network is { } net && hotspot.Current != PcHotspot.Mode.Off) item["note"] = net.Ssid;
                        break;
                }
            }
            catch (Exception ex)
            {
                item["available"] = false;
                log.Log(LogLevel.Warn, $"Quick tiles: couldn't read '{id}' ({ex.Message})");
            }
            items.Add(item);
        }
        var payload = new JsonObject { ["items"] = items };
        if (error is not null) payload["error"] = error;
        try { await connection.SendToPhoneAsync(Envelope.Create(MessageType.PcQuickState, payload), CancellationToken.None); }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"Quick tiles: couldn't answer the phone ({ex.Message})"); }
    }

    // ---- dark mode: the same values as the phone's PC › Look (v27 pc.look) ----------------------

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return (key?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
    }

    private string? SetDark(bool dark)
    {
        // Apps and Windows together, as the phone's "Dark theme" means the whole PC.
        PcLook.Apply(new JsonObject { ["appsDark"] = dark, ["systemDark"] = dark }, log);
        return null;
    }

    // ---- Do not disturb ---------------------------------------------------------------------------

    private const string NotificationSettings = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>
    /// Windows has no public switch for Do not disturb. This is the "Notifications" master switch
    /// (banners off), the documented registry value behind it; on some Windows 11 builds the
    /// notification centre only picks it up after Explorer restarts (see TEST-NOTES).
    /// </summary>
    private static bool IsDnd()
    {
        using var key = Registry.CurrentUser.OpenSubKey(NotificationSettings);
        return key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int value && value == 0;
    }

    private static string? SetDnd(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(NotificationSettings))
            key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", on ? 0 : 1, RegistryValueKind.DWord);
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Notifications", 0x0002, 2000, out _);
        return null;
    }

    // ---- radios -----------------------------------------------------------------------------------

    private static async Task<Radio?> FindRadioAsync(RadioKind kind)
    {
        var radios = await Radio.GetRadiosAsync();
        return radios.FirstOrDefault(r => r.Kind == kind);
    }

    private static async Task<string?> SetRadioAsync(RadioKind kind, bool? on)
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return "denied";
        var radio = await FindRadioAsync(kind);
        if (radio is null) return "unavailable";
        bool want = on ?? radio.State != RadioState.On;
        var result = await radio.SetStateAsync(want ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed ? null : "denied";
    }

    // ---- hotspot ------------------------------------------------------------------------------------

    private async Task<string?> SetHotspotAsync(bool on)
    {
        if (on == (hotspot.Current != PcHotspot.Mode.Off)) return null;
        if (!on)
        {
            await hotspot.StopAsync();
            return null;
        }
        await hotspot.StartAsync();
        return hotspot.Current == PcHotspot.Mode.Off ? hotspot.LastError ?? "unavailable" : null;
    }

    // ---- shortcuts ----------------------------------------------------------------------------------

    private static string? OnLinc(Action<MainWindow> run)
    {
        if (App.MainWindowInstance is not MainWindow main) return "unavailable";
        run(main); // both dispatch to the UI thread themselves
        return null;
    }

    private const byte VkLWin = 0x5B;
    private const byte VkShift = 0x10;

    /// <summary>A key chord, pressed in order and released in reverse, as the keyboard would.</summary>
    private static string? Keys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 0x0002, UIntPtr.Zero);
        return null;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>
/// Windows' Night light, which has no API: its state is one binary value in the CloudStore, and
/// writing it applies at once. Layout (from the sem-reg crate's notes, checked against this PC's
/// own value): a header <c>43 42 01 00 0a 02 01 00 2a 06</c>, a 5-byte VLQ of Unix seconds (must
/// grow, or Windows undoes the write), <c>2a 2b 0e</c>, a length byte, <c>43 42 01 00</c>, then
/// <c>10 00</c> only while on, <c>d0 0a 02</c> when switched by hand, then the rest. Anything else
/// is left alone and the tile shows as not available.
/// </summary>
public static class PcNightLight
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    private static readonly byte[] Header = [0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06];
    private static readonly byte[] Inner = [0x43, 0x42, 0x01, 0x00];
    private static readonly byte[] Manual = [0xD0, 0x0A, 0x02];
    private const int StampAt = 10, LengthAt = 18, InnerAt = 19, ActiveAt = 23;

    public static bool? IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("Data") is byte[] data ? IsOn(data) : null;
    }

    public static string? Set(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (key?.GetValue("Data") is not byte[] data) return "unavailable";
        var next = With(data, on, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (next is null) return "unavailable";
        key.SetValue("Data", next, RegistryValueKind.Binary);
        return null;
    }

    /// <summary>Null when the value isn't the shape described above.</summary>
    public static bool? IsOn(byte[] data)
    {
        if (!Shaped(data)) return null;
        return data[ActiveAt] == 0x10 && data[ActiveAt + 1] == 0x00;
    }

    /// <summary>The value with Night light on or off and a newer stamp; null when it isn't understood.</summary>
    public static byte[]? With(byte[] data, bool on, long unixNow)
    {
        if (IsOn(data) is not { } was) return null;
        var rest = data.AsSpan(ActiveAt + (was ? 2 : 0)).ToArray();
        var body = new List<byte>();
        if (on) body.AddRange([0x10, 0x00]);
        // Switched by hand, as Settings marks it; a scheduled change leaves the marker out.
        if (!rest.AsSpan().StartsWith(Manual)) body.AddRange(Manual);
        body.AddRange(rest);

        if (ReadVlq(data, StampAt, out long stamp) != StampAt + 5) return null;
        var output = new List<byte>(data.AsSpan(0, ActiveAt).ToArray());
        var newStamp = WriteVlq(Math.Max(unixNow, stamp + 2));
        if (newStamp.Length != 5) return null;
        for (int i = 0; i < 5; i++) output[StampAt + i] = newStamp[i];
        int length = data[LengthAt] + (body.Count - rest.Length - (was ? 2 : 0));
        if (length is < 0 or > 0x7F) return null;
        output[LengthAt] = (byte)length;
        output.AddRange(body);
        return [.. output];
    }

    private static bool Shaped(byte[] data) =>
        data.Length > ActiveAt + 4
        && data.AsSpan(0, Header.Length).SequenceEqual(Header)
        && data[15] == 0x2A && data[16] == 0x2B && data[17] == 0x0E
        && data.AsSpan(InnerAt, Inner.Length).SequenceEqual(Inner);

    /// <summary>Little-endian base-128; returns the index after the last byte.</summary>
    private static int ReadVlq(byte[] data, int at, out long value)
    {
        value = 0;
        int shift = 0;
        while (at < data.Length && shift < 63)
        {
            byte b = data[at++];
            value |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) return at;
        }
        return -1;
    }

    private static byte[] WriteVlq(long value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return [.. bytes];
    }
}
