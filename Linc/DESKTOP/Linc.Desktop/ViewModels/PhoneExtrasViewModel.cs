using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Protocol;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Linc.Desktop.ViewModels;

/// <summary>One app in the phone's screen time today, with its share of the longest bar.</summary>
public sealed record ScreenTimeApp(string Name, string Time, double Fraction);

/// <summary>A colour the phone's Material You palette can be built from.</summary>
public sealed record PaletteSwatch(string Hex, string Name)
{
    public Windows.UI.Color Color => Windows.UI.Color.FromArgb(255,
        Convert.ToByte(Hex[..2], 16), Convert.ToByte(Hex[2..4], 16), Convert.ToByte(Hex[4..6], 16));
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush => new(Color);
}

/// <summary>
/// The Device page reaching into the phone (v28): today's screen time, a wallpaper from this PC,
/// the phone's Material You colours and dark theme, and the phone's sound played here.
///
/// <para>Screen time and wallpaper go through the Linc app on the phone. Colours and dark theme
/// are the phone's own system settings, which only a USB-debugging (adb) session may change, so
/// they need the adb link (USB or Wireless debugging) and say so without one.</para>
/// </summary>
public sealed partial class PhoneExtrasViewModel : ObservableObject
{
    private const string Package = "app.linc.android";
    private readonly IConnectionManager _connection;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly AdbClient _adb = new();
    private readonly PhoneSoundOnPc _bluetooth;

    public PhoneExtrasViewModel(IConnectionManager connection, ILogService log, PhoneSoundOnPc bluetooth)
    {
        _connection = connection;
        _log = log;
        _bluetooth = bluetooth;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        connection.CompanionMessageReceived += envelope =>
        {
            if (envelope.Type != MessageType.PhoneUsage) return;
            var payload = envelope.Payload.DeepClone().AsObject();
            _dispatcher.TryEnqueue(() => OnUsage(payload));
        };
    }

    public ObservableCollection<ScreenTimeApp> ScreenTimeApps { get; } = [];

    public IReadOnlyList<PaletteSwatch> Swatches { get; } =
    [
        new("6750A4", "Purple"), new("1B6EF3", "Blue"), new("006A6A", "Teal"), new("386A20", "Green"),
        new("8C5000", "Amber"), new("B3261E", "Red"), new("984061", "Rose"), new("5D5F5F", "Grey"),
    ];

    public IReadOnlyList<string> Styles { get; } = ["Tonal spot", "Vibrant", "Expressive", "Spritz", "Rainbow", "Fruit salad", "Monochrome"];

    [ObservableProperty] public partial string ScreenTimeTotal { get; set; } = "—";
    [ObservableProperty] public partial string ScreenTimeDetail { get; set; } = "";
    [ObservableProperty] public partial bool UsageNeedsAccess { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial string SelectedStyle { get; set; } = "Tonal spot";
    [ObservableProperty] public partial bool PhoneDark { get; set; }

    /// <summary>A v28 phone: Linc on it understands screen time, wallpaper and phone sound.</summary>
    public bool Supported => (_connection.Current?.Companion.V ?? 0) >= 28;

    private bool HasAdb => _connection.RawDevice is not null;

    // ---- screen time -------------------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshScreenTimeAsync()
    {
        if (!Supported) { Message = "Update Linc on the phone to see its screen time here."; return; }
        await SendAsync(MessageType.PhoneUsageGet, new JsonObject());
    }

    /// <summary>Usage access is a special permission; over adb this PC can switch it on itself.</summary>
    [RelayCommand]
    private async Task AllowScreenTimeAsync()
    {
        if (!HasAdb)
        {
            Message = "On the phone: Settings → Apps → Special app access → Usage access → Linc. Or connect over USB and press this again.";
            return;
        }
        await ShellAsync($"appops set {Package} GET_USAGE_STATS allow", "Allowed Linc to read screen time");
        await RefreshScreenTimeAsync();
    }

    private void OnUsage(JsonObject p)
    {
        UsageNeedsAccess = (bool?)p["allowed"] != true;
        ScreenTimeApps.Clear();
        if (UsageNeedsAccess)
        {
            ScreenTimeTotal = "—";
            ScreenTimeDetail = "Linc needs usage access on the phone to show screen time.";
            return;
        }
        long total = (long?)p["totalMin"] ?? 0;
        ScreenTimeTotal = Duration(total);
        int unlocks = (int?)p["unlocks"] ?? 0;
        ScreenTimeDetail = $"Today · unlocked {unlocks} time{(unlocks == 1 ? "" : "s")}";
        var apps = (p["apps"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        double top = apps.Select(a => (double?)(long?)a["minutes"] ?? 0).DefaultIfEmpty(1).Max();
        foreach (var a in apps.Take(8))
        {
            long minutes = (long?)a["minutes"] ?? 0;
            ScreenTimeApps.Add(new ScreenTimeApp((string?)a["name"] ?? "", Duration(minutes), top > 0 ? minutes / top : 0));
        }
    }

    private static string Duration(long minutes) => minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";

    // ---- wallpaper ---------------------------------------------------------------------------

    [RelayCommand]
    private Task WallpaperBothAsync() => PickWallpaperAsync("both");
    [RelayCommand]
    private Task WallpaperHomeAsync() => PickWallpaperAsync("home");
    [RelayCommand]
    private Task WallpaperLockAsync() => PickWallpaperAsync("lock");

    private async Task PickWallpaperAsync(string target)
    {
        if (!Supported) { Message = "Update Linc on the phone to set its wallpaper from here."; return; }
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var jpeg = await ShrinkAsync(file);
            await SendAsync(MessageType.PhoneWallpaperSet, new JsonObject { ["jpeg"] = Convert.ToBase64String(jpeg), ["target"] = target });
            Message = $"Sent {file.Name} to the phone as its wallpaper.";
            _log.Log(LogLevel.Info, $"Device: sent a wallpaper to the phone ({target}, {jpeg.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Message = "That picture couldn't be read.";
            _log.Log(LogLevel.Warn, $"Device: couldn't prepare the wallpaper ({ex.Message})");
        }
    }

    /// <summary>At most 2400 px on the long side and ~640 KB, so it fits one protocol frame as base64.</summary>
    private static async Task<byte[]> ShrinkAsync(Windows.Storage.StorageFile file)
    {
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1.0, 2400.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * scale), ScaledHeight = (uint)(decoder.PixelHeight * scale), InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        for (double quality = 0.9; ; quality -= 0.1)
        {
            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(bytes);
            }
            if (bytes.Length <= 640 * 1024 || quality <= 0.45) return bytes;
        }
    }

    // ---- colours and theme (adb) ---------------------------------------------------------------

    /// <summary>The phone's Material You palette from one colour, as Settings → Wallpaper &amp; style → Colours does.</summary>
    [RelayCommand]
    private async Task SetColourAsync(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || !RequireAdb()) return;
        string style = SelectedStyle switch
        {
            "Vibrant" => "VIBRANT", "Expressive" => "EXPRESSIVE", "Spritz" => "SPRITZ", "Rainbow" => "RAINBOW",
            "Fruit salad" => "FRUIT_SALAD", "Monochrome" => "MONOCHROMATIC", _ => "TONAL_SPOT",
        };
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string json = $"{{\"android.theme.customization.system_palette\":\"FF{hex}\",\"android.theme.customization.accent_color\":\"FF{hex}\"," +
                      $"\"android.theme.customization.theme_style\":\"{style}\",\"android.theme.customization.color_source\":\"preset\"," +
                      $"\"_applied_timestamp\":{stamp}}}";
        await ShellAsync($"settings put secure theme_customization_overlay_packages '{json}'", $"The phone's colours now come from #{hex}");
    }

    /// <summary>Back to colours picked from the phone's wallpaper.</summary>
    [RelayCommand]
    private async Task WallpaperColoursAsync()
    {
        if (!RequireAdb()) return;
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await ShellAsync($"settings put secure theme_customization_overlay_packages '{{\"android.theme.customization.color_source\":\"home_wallpaper\",\"_applied_timestamp\":{stamp}}}'",
            "The phone's colours follow its wallpaper again");
    }

    partial void OnPhoneDarkChanged(bool value)
    {
        if (_settingDark || !RequireAdb()) return;
        _ = ShellAsync($"cmd uimode night {(value ? "yes" : "no")}", value ? "The phone is in dark theme" : "The phone is in light theme");
    }

    private bool _settingDark;

    /// <summary>Read the phone's current theme, without echoing it back.</summary>
    [RelayCommand]
    private async Task RefreshLookAsync()
    {
        if (!HasAdb) return;
        var output = await ShellAsync("cmd uimode night", null);
        _settingDark = true;
        PhoneDark = output?.Contains("yes", StringComparison.OrdinalIgnoreCase) == true;
        _settingDark = false;
    }

    // ---- the phone's sound here ----------------------------------------------------------------

    // The mirror's own sound with no picture (scrcpy --audio-dup): the phone keeps playing and
    // this PC plays along; Bluetooth only when there's no USB or wireless-debugging link.

    [RelayCommand]
    private async Task PhoneSoundStartAsync()
    {
        Message = "Bringing the phone's sound to this PC…";
        var names = new List<string>();
        if (_connection.Current?.Model is { } model) names.Add(model);
        await _bluetooth.SetAsync(true, names);
        Message = BluetoothMessage();
    }

    [RelayCommand]
    private async Task PhoneSoundStopAsync()
    {
        await _bluetooth.SetAsync(false, []);
        Message = "The phone's sound plays on the phone again.";
    }

    private string BluetoothMessage() => _bluetooth.Error switch
    {
        null when _bluetooth.IsOn => $"{_bluetooth.Device}'s sound plays on this PC now, and on the phone too.",
        "needs-android-13" => "Playing the phone's sound here and on the phone at once needs Android 13 or later.",
        "not-paired" => "Pair the phone with this PC over Bluetooth once first: Windows Settings › Bluetooth & devices › Add device, with the phone's Bluetooth settings open. Then press Play here again.",
        "unsupported" => "This version of Windows can't be a Bluetooth speaker (it needs Windows 10 version 2004 or later).",
        { } error => $"Windows couldn't connect to the phone over Bluetooth ({error}). Check Bluetooth is on on both, then try again.",
        _ => "",
    };

    // ---- plumbing ------------------------------------------------------------------------------

    private bool RequireAdb()
    {
        if (HasAdb) return true;
        Message = "Changing the phone's colours and theme needs USB or Wireless debugging. Plug the phone in, then try again.";
        return false;
    }

    private async Task<string?> ShellAsync(string command, string? done)
    {
        if (_connection.RawDevice is not { } device) return null;
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(command, device, receiver, CancellationToken.None);
            var output = receiver.ToString();
            if (done is not null)
            {
                Message = done;
                _log.Log(LogLevel.Info, $"Device: {done}");
            }
            return output;
        }
        catch (Exception ex)
        {
            Message = "The phone didn't accept that.";
            _log.Log(LogLevel.Warn, $"Device: adb command failed ({ex.Message})");
            return null;
        }
    }

    private async Task SendAsync(string type, JsonObject payload)
    {
        try { await _connection.SendToPhoneAsync(Envelope.Create(type, payload), CancellationToken.None); }
        catch (Exception ex)
        {
            Message = "Couldn't reach the phone.";
            _log.Log(LogLevel.Warn, $"Device: couldn't send {type} ({ex.Message})");
        }
    }
}
