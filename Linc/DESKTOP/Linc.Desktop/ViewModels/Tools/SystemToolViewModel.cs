using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P = Linc.Desktop.Services.Tools.HealthSystemParsers;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › System (TOOLS.md §6): six everyday settings as tiles, then the radios and a restart
/// group under More. Values are read from the phone when the section opens and again after each
/// change, so the tiles show what the phone really did; a refused change says so.
/// </summary>
public partial class SystemToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private bool _reading;
    private CancellationTokenSource? _brightnessApply;

    // ---- tiles -------------------------------------------------------------------------------
    [ObservableProperty] public partial bool DarkMode { get; set; }
    [ObservableProperty] public partial bool StayAwake { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimOff), nameof(AnimHalf), nameof(AnimNormal))]
    public partial double Animations { get; set; } = 1;
    public bool AnimOff => Animations == 0;
    public bool AnimHalf => Animations == 0.5;
    public bool AnimNormal => Animations == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Timeout30s), nameof(Timeout1m), nameof(Timeout5m), nameof(Timeout10m), nameof(Timeout30m))]
    public partial int TimeoutMs { get; set; }
    public bool Timeout30s => TimeoutMs == 30_000;
    public bool Timeout1m => TimeoutMs == 60_000;
    public bool Timeout5m => TimeoutMs == 300_000;
    public bool Timeout10m => TimeoutMs == 600_000;
    public bool Timeout30m => TimeoutMs == 1_800_000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrightnessText))]
    public partial double Brightness { get; set; } = 128;
    public string BrightnessText => $"{Math.Round(Brightness / 2.55)}%";
    [ObservableProperty] public partial bool AutoBrightness { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextSmall), nameof(TextDefault), nameof(TextLarge), nameof(TextLarger))]
    public partial double FontScale { get; set; } = 1;
    public bool TextSmall => Math.Abs(FontScale - 0.85) < 0.01;
    public bool TextDefault => Math.Abs(FontScale - 1.0) < 0.01;
    public bool TextLarge => Math.Abs(FontScale - 1.15) < 0.01;
    public bool TextLarger => Math.Abs(FontScale - 1.3) < 0.01;

    // ---- More --------------------------------------------------------------------------------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WifiText))] public partial bool Wifi { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MobileDataText))] public partial bool MobileData { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BluetoothText))] public partial bool Bluetooth { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AirplaneText))] public partial bool Airplane { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LocationText))] public partial bool Location { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BatterySaverText))] public partial bool BatterySaver { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NightLightText))] public partial bool NightLight { get; set; }

    public string WifiText => OnOff(Wifi);
    public string MobileDataText => OnOff(MobileData);
    public string BluetoothText => OnOff(Bluetooth);
    public string AirplaneText => OnOff(Airplane);
    public string LocationText => OnOff(Location);
    public string BatterySaverText => OnOff(BatterySaver);
    public string NightLightText => OnOff(NightLight);

    private static string OnOff(bool on) => on ? "On" : "Off";

    private bool Wireless => Ctx.Device is { Usb: false };

    public override Task ShownAsync() => ReadAllAsync();

    public override void Hidden() => _brightnessApply?.Cancel();

    private async Task<string> Get(string command)
    {
        var r = await Ctx.Shell.RunAsync(Ctx.Serial, command);
        return r.Ok ? r.Text : "";
    }

    /// <summary>Reads every value the section shows. Quietly: no caption for reads.</summary>
    private async Task ReadAllAsync()
    {
        var night = Get("cmd uimode night");
        var stay = Get("settings get global stay_on_while_plugged_in");
        var anim = Get("settings get global window_animation_scale");
        var timeout = Get("settings get system screen_off_timeout");
        var bright = Get("settings get system screen_brightness");
        var brightMode = Get("settings get system screen_brightness_mode");
        var font = Get("settings get system font_scale");
        var wifi = Get("cmd wifi status");
        var data = Get("settings get global mobile_data");
        var bt = Get("settings get global bluetooth_on");
        var air = Get("settings get global airplane_mode_on");
        var loc = Get("cmd location is-location-enabled");
        var saver = Get("settings get global low_power");
        var nightLight = Get("settings get secure night_display_activated");
        await Task.WhenAll(night, stay, anim, timeout, bright, brightMode, font, wifi, data, bt, air, loc, saver, nightLight);

        _reading = true;
        try
        {
            DarkMode = P.ParseNightMode(night.Result) ?? false;
            StayAwake = P.SettingOn(stay.Result) ?? false;
            Animations = P.SettingNumber(anim.Result) ?? 1;
            TimeoutMs = (int)(P.SettingNumber(timeout.Result) ?? 0);
            if (P.SettingNumber(bright.Result) is { } b) Brightness = Math.Clamp(b, 0, 255);
            AutoBrightness = P.SettingOn(brightMode.Result) ?? false;
            FontScale = P.SettingNumber(font.Result) ?? 1;
            Wifi = P.ParseWifi(wifi.Result) ?? false;
            MobileData = P.SettingOn(data.Result) ?? false;
            Bluetooth = P.SettingOn(bt.Result) ?? false;
            Airplane = P.SettingOn(air.Result) ?? false;
            Location = P.ParseTrueFalse(loc.Result) ?? false;
            BatterySaver = P.SettingOn(saver.Result) ?? false;
            NightLight = P.SettingOn(nightLight.Result) ?? false;
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Sends a change, reads everything back, and says so if the phone didn't take it.</summary>
    private async Task ChangeAsync(string command, Func<bool> applied)
    {
        if (!await Ctx.DoAsync(command)) return;
        await ReadAllAsync();
        if (!applied()) Ctx.Say("The phone didn't take that change. It may not allow it over adb.");
    }

    // ---- tile commands -----------------------------------------------------------------------

    [RelayCommand]
    private Task ToggleDarkMode()
    {
        var want = !DarkMode;
        return ChangeAsync("cmd uimode night " + (want ? "yes" : "no"), () => DarkMode == want);
    }

    [RelayCommand]
    private Task ToggleStayAwake()
    {
        var want = !StayAwake;
        return ChangeAsync("settings put global stay_on_while_plugged_in " + (want ? "7" : "0"), () => StayAwake == want);
    }

    [RelayCommand]
    private Task SetAnimations(string scale)
    {
        if (!double.TryParse(scale, NumberStyles.Float, Inv, out var v) || v is not (0 or 0.5 or 1)) return Task.CompletedTask;
        var s = v.ToString(Inv);
        return ChangeAsync(
            $"settings put global window_animation_scale {s}; settings put global transition_animation_scale {s}; settings put global animator_duration_scale {s}",
            () => Animations == v);
    }

    [RelayCommand]
    private Task SetTimeout(string ms)
    {
        if (!int.TryParse(ms, out var v) || v is not (30_000 or 60_000 or 300_000 or 600_000 or 1_800_000)) return Task.CompletedTask;
        return ChangeAsync("settings put system screen_off_timeout " + v, () => TimeoutMs == v);
    }

    [RelayCommand]
    private Task SetTextSize(string scale)
    {
        if (!double.TryParse(scale, NumberStyles.Float, Inv, out var v) || v is not (0.85 or 1.0 or 1.15 or 1.3)) return Task.CompletedTask;
        return ChangeAsync("settings put system font_scale " + v.ToString(Inv), () => Math.Abs(FontScale - v) < 0.01);
    }

    [RelayCommand]
    private Task ToggleAutoBrightness()
    {
        var want = !AutoBrightness;
        return ChangeAsync("settings put system screen_brightness_mode " + (want ? "1" : "0"), () => AutoBrightness == want);
    }

    // The slider sends its value once it has rested for a moment, not on every step of a drag.
    partial void OnBrightnessChanged(double value)
    {
        if (_reading) return;
        _brightnessApply?.Cancel();
        var cts = _brightnessApply = new CancellationTokenSource();
        _ = ApplyBrightnessAsync((int)Math.Round(value), cts.Token);
    }

    private async Task ApplyBrightnessAsync(int level, CancellationToken ct)
    {
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { return; }
        // A manual level only shows with auto brightness off.
        var command = (AutoBrightness ? "settings put system screen_brightness_mode 0; " : "") +
            "settings put system screen_brightness " + Math.Clamp(level, 0, 255);
        if (await Ctx.DoAsync(command) && AutoBrightness)
        {
            _reading = true;
            AutoBrightness = false;
            _reading = false;
        }
    }

    // ---- More commands -----------------------------------------------------------------------

    private Task<bool> WarnDisconnectAsync() => Ctx.Confirm(new ConfirmRequest(
        "This will disconnect Linc from the phone",
        "The phone is connected over Wi-Fi, so Linc loses it until Wi-Fi is back on. Turn it back on from the phone, or connect a USB cable.",
        "Go ahead"));

    [RelayCommand]
    private async Task ToggleWifi()
    {
        var want = !Wifi;
        if (!want && Wireless && !await WarnDisconnectAsync()) return;
        await ChangeAsync("cmd wifi set-wifi-enabled " + (want ? "enabled" : "disabled"), () => Wifi == want);
    }

    [RelayCommand]
    private Task ToggleMobileData()
    {
        var want = !MobileData;
        return ChangeAsync("svc data " + (want ? "enable" : "disable"), () => MobileData == want);
    }

    [RelayCommand]
    private Task ToggleBluetooth()
    {
        var want = !Bluetooth;
        var command = Ctx.Sdk >= 31
            ? "cmd bluetooth_manager " + (want ? "enable" : "disable")
            : "svc bluetooth " + (want ? "enable" : "disable");
        return ChangeAsync(command, () => Bluetooth == want);
    }

    [RelayCommand]
    private async Task ToggleAirplane()
    {
        var want = !Airplane;
        if (want && Wireless && !await WarnDisconnectAsync()) return;
        await ChangeAsync("cmd connectivity airplane-mode " + (want ? "enable" : "disable"), () => Airplane == want);
    }

    [RelayCommand]
    private Task ToggleLocation()
    {
        var want = !Location;
        return ChangeAsync("cmd location set-location-enabled " + (want ? "true" : "false"), () => Location == want);
    }

    [RelayCommand]
    private Task ToggleBatterySaver()
    {
        var want = !BatterySaver;
        return ChangeAsync("cmd power set-mode " + (want ? "1" : "0"), () => BatterySaver == want);
    }

    [RelayCommand]
    private Task ToggleNightLight()
    {
        var want = !NightLight;
        return ChangeAsync("settings put secure night_display_activated " + (want ? "1" : "0"), () => NightLight == want);
    }

    // ---- restart -----------------------------------------------------------------------------

    private async Task RebootAsync(string title, string body, string[] args, string done)
    {
        if (!await Ctx.Confirm(new ConfirmRequest(title, body, title))) return;
        Ctx.ShowCommand("adb " + string.Join(' ', args));
        var r = await Ctx.Shell.AdbAsync(Ctx.Serial, args, TimeSpan.FromSeconds(20));
        Ctx.Say(r.Ok ? done : "The phone didn't restart: " + r.Text);
    }

    [RelayCommand]
    private Task Restart() => RebootAsync("Restart", "The phone restarts now. Linc reconnects when it's back.",
        ["reboot"], "Restarting. Linc reconnects when the phone is back.");

    [RelayCommand]
    private Task Recovery() => RebootAsync("Recovery",
        "The phone restarts into its recovery screen. Use the phone's volume and power buttons there; \"Reboot system now\" brings it back.",
        ["reboot", "recovery"], "Restarting into recovery.");

    [RelayCommand]
    private Task PowerOff() => RebootAsync("Power off",
        "The phone turns off. Linc can't turn it back on: press its power button. This doesn't work on every phone.",
        ["shell", "reboot", "-p"], "Turning off. If the phone stays on, it doesn't allow this over adb.");
}
