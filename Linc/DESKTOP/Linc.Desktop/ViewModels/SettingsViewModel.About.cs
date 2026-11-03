using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Linc.Desktop.ViewModels;

/// <summary>One of the cards on the Settings home: an icon, a name, and how it's set right now.</summary>
public sealed partial class SettingsCategoryVm(string key, string glyph, string title, Action<string> open) : ObservableObject
{
    public string Key => key;
    public string Glyph => glyph;
    public string Title => title;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public RelayCommand OpenCommand { get; } = new(() => open(key));
}

/// <summary>A tile under Settings › About › Links.</summary>
public sealed record AboutLink(string Glyph, string Title, string Subtitle, IRelayCommand Command);

/// <summary>A line under Settings › About › Built with (the repository's NOTICE).</summary>
public sealed record AboutCredit(string Name, string Role, string Licence);

/// <summary>
/// Settings › About and the Settings home's cards (the owner, 2026-11-03: "especially the About
/// section bro, it's ugly"). The version, the build channel, what this build brought, the phone's
/// Linc, links, credits, and a copy of what a bug report needs.
/// </summary>
public partial class SettingsViewModel
{
    // ---- the Settings home's cards ----

    /// <summary>
    /// The one line on each home card. Keys are SettingsPage's category keys; a category this
    /// doesn't know shows no line rather than a wrong one.
    /// </summary>
    public string SummaryFor(string key) => key switch
    {
        "connection" => Join(LinkSummary, LincLinkEnabled ? "New engine" : "Classic engine", EncryptWifiLinks ? "Wi-Fi encrypted" : null),
        "finding" => Views.Settings.FindingSettings.Summary(
            App.Services.GetRequiredService<IBlePresenceService>(), App.Services.GetRequiredService<NearbyRules>()),
        "appearance" => Join(_registry.FloatingNav ? "Floating bar" : "Side rail", FontName, UsePhoneColours ? "Phone's colours" : "Black and white",
            LiveBubble ? "Bubble on" : null),
        "mirror" => ForceSoftwareEncoding ? "Software encoding · Phone as microphone" : "Hardware encoding · Phone as microphone",
        "notifications" => Join(NotificationSyncEnabled ? "Relay on" : "Relay off",
            NotificationHistoryEnabled ? $"History kept {_registry.NotificationHistoryRetentionDays} days" : "No history"),
        "clipboard" => Join(ClipboardSyncEnabled ? "Sync on" : "Sync off",
            ClipboardHistoryEnabled ? $"Keeps {_registry.ClipboardHistorySize:N0} clips" : "No history"),
        "devices" => KnownDevices.Count switch
        {
            0 => "No phones paired yet",
            1 => $"{KnownDevices[0].Model} · {KnownDevices[0].StateText}",
            var n => $"{n} phones paired",
        },
        "startup" => StartWithWindows ? "Starts when you sign in · Stays in the tray" : "Starts when you open it · Stays in the tray",
        "advanced" => Join(_adbServerHost.AdbPath is null ? "ADB not found" : "ADB ready", "Shell", "Logs"),
        "about" => Join($"{BuildFlavor.AppName} {Provenance.Version}", BuildFlavor.IsTest ? BuildFlavor.TestLabel : null,
            UpdatesEnabled ? null : "Updates off"),
        _ => "",
    };

    private string LinkSummary => _supervisor.State switch
    {
        LinkState.Connected => $"Linked to {_supervisor.Device?.Model ?? "your phone"}" + (_supervisor.Device?.Transport switch
        {
            LinkTransport.AdbUsb => " by cable",
            LinkTransport.DirectTls => " directly",
            LinkTransport.AdbWireless => " over Wi-Fi",
            _ => "",
        }),
        LinkState.Paused => "Paused",
        LinkState.NoDevice => "No phone paired",
        _ => "Looking for your phone",
    };

    private string FontName => _registry.AppFont switch
    {
        "inter" => "Inter",
        "jakarta" => "Plus Jakarta Sans",
        "manrope" => "Manrope",
        "outfit" => "Outfit",
        _ => "Segoe UI",
    };

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Settings › Phones shows a line instead of an empty card.</summary>
    public bool HasNoKnownDevices => KnownDevices.Count == 0;

    // ---- About: who and what this is ----

    /// <summary>"Linc" or "Linc Test".</summary>
    public string AppName => BuildFlavor.AppName;

    /// <summary>"Release" or "Test build 42 (linclink-b)", for the chip beside the version.</summary>
    public string ChannelText => BuildFlavor.IsTest ? BuildFlavor.TestLabel : "Release";

    public bool IsTestBuild => BuildFlavor.IsTest;

    /// <summary>"Built 3 November 2026", from the exe's own date; empty when Windows won't say.</summary>
    public string BuildDateText
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                return exe is null ? "" : $"Built {File.GetLastWriteTime(exe):d MMMM yyyy}";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    public string FingerprintText => Provenance.Fingerprint;

    /// <summary>The connected phone's own Linc, which About shows beside the PC's.</summary>
    public string PhoneAboutTitle => _supervisor.State == LinkState.Connected && _supervisor.Device is { } live
        ? live.Model
        : "No phone connected";

    public string PhoneAboutText => _supervisor.State == LinkState.Connected && _supervisor.Device is { } live
        ? $"Linc {live.Companion.AppVersion} on the phone · protocol v{live.Companion.V}" + (live.Companion.AppVersion == Provenance.Version
            ? " · the same version as this PC" : "")
        : "Connect your phone to see its Linc version here.";

    public bool HasPhoneConnected => _supervisor.State == LinkState.Connected && _supervisor.Device is not null;

    // ---- About: what's new ----

    private readonly ReleaseNotes.Notes? _notes =
        ReleaseNotes.ForThisBuild(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, BuildFlavor.IsTest);

    /// <summary>How many lines What's new shows before "Show all".</summary>
    private const int NotesShown = 6;

    public bool HasWhatsNew => _notes is not null;

    public string WhatsNewTitle => _notes?.Title ?? "What's new";

    public string WhatsNewLead => _notes?.Lead ?? "";

    public bool HasWhatsNewLead => !string.IsNullOrEmpty(_notes?.Lead);

    public ObservableCollection<string> WhatsNewItems { get; } = [];

    public bool CanShowAllNotes => _notes is { } notes && notes.Items.Count > NotesShown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllNotesText))]
    public partial bool ShowAllNotes { get; set; }

    public string ShowAllNotesText => ShowAllNotes ? "Show fewer" : $"Show all {_notes?.Items.Count ?? 0}";

    partial void OnShowAllNotesChanged(bool value) => FillNotes();

    [RelayCommand]
    private void ToggleAllNotes() => ShowAllNotes = !ShowAllNotes;

    private void FillNotes()
    {
        WhatsNewItems.Clear();
        if (_notes is null) return;
        foreach (var item in ShowAllNotes ? _notes.Items : _notes.Items.Take(NotesShown))
        {
            WhatsNewItems.Add(item);
        }
    }

    // ---- About: links and credits ----

    public List<AboutLink> AboutLinks => _aboutLinks ??=
    [
        new("", "Telegram channel", "t.me/LincApp · news and help", new RelayCommand(() => Open("https://t.me/LincApp"))),
        new("", "Source code", "github.com/deloweProjects/Linc", new RelayCommand(() => Open(Provenance.Source))),
        new("", "Report a problem", "Open an issue on GitHub", new RelayCommand(() => Open($"{Provenance.Source}/issues"))),
        new("", "All releases", "Every version and its notes", new RelayCommand(() => Open(UpdateChannel.ReleasesUrl))),
    ];

    private List<AboutLink>? _aboutLinks;

    /// <summary>The repository's NOTICE, one line each.</summary>
    public List<AboutCredit> AboutCredits { get; } =
    [
        new("scrcpy", "Screen mirroring", "Apache 2.0 · © Genymobile and Romain Vimont"),
        new("Android Debug Bridge (adb)", "Talking to the phone over USB and Wi-Fi", "Apache 2.0 · © The Android Open Source Project"),
        new("FFmpeg, SDL2 and libusb", "Used by scrcpy", "LGPL-2.1 · zlib · LGPL-2.1"),
        new("Quick Share protocol definitions", "Sharing with nearby devices", "Apache 2.0 · © Google LLC"),
        new("AdvancedSharpAdbClient", "ADB from .NET", "Apache 2.0"),
        new("Windows App SDK (WinUI 3) and .NET 8", "The app itself", "MIT"),
        new("CommunityToolkit.Mvvm", "View models", "MIT"),
        new("Microsoft.Data.Sqlite and SQLite", "History on this PC", "MIT · Apache 2.0 · public domain"),
        new("H.NotifyIcon", "The tray icon", "MIT"),
        new("QRCoder", "Pairing and hotspot codes", "MIT"),
        new("Zeroconf and Makaretu.Dns.Multicast", "Finding the phone on the network", "MIT"),
        new("Vortice", "Mirror PC's capture and encoder", "MIT"),
        new("Google.Protobuf", "Quick Share's wire format", "BSD-3-Clause"),
        new("DM Serif Display, Inter, Manrope, Outfit, Plus Jakarta Sans", "Fonts", "SIL Open Font License 1.1"),
    ];

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Info, $"Couldn't open {target} ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- About: copy diagnostic info ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticsResult))]
    public partial string DiagnosticsResult { get; set; } = "";

    public bool HasDiagnosticsResult => DiagnosticsResult.Length > 0;

    /// <summary>
    /// What a bug report needs, on the clipboard: versions, Windows, the link and the switches that
    /// change how Linc connects. No serials, no network names, no paths with the user's name.
    /// </summary>
    public string DiagnosticText()
    {
        var text = new StringBuilder();
        text.AppendLine($"{BuildFlavor.AppName} {AppVersionText}");
        if (BuildDateText.Length > 0) text.AppendLine(BuildDateText);
        text.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"Link: {_supervisor.State}" + (_supervisor.Route is { } route ? $" via {route}" : ""));
        if (_supervisor.State == LinkState.Connected && _supervisor.Device is { } live)
        {
            text.AppendLine($"Phone: {live.Model}, Linc {live.Companion.AppVersion}, protocol v{live.Companion.V}, {live.Transport}");
        }
        text.AppendLine($"Phones paired: {KnownDevices.Count}");
        text.AppendLine($"ADB: {(_adbServerHost.AdbPath is { } adb ? Path.GetFileName(adb) : "not found")}");
        text.AppendLine($"New connection engine: {OnOff(LincLinkEnabled)}, encrypt Wi-Fi links: {OnOff(EncryptWifiLinks)}, " +
            $"background connection: {OnOff(BackgroundConnectionEnabled)}, USB: {OnOff(AllowUsbConnections)}");
        text.AppendLine($"PC hotspot when near: {OnOff(AutoHotspot)}, Bluetooth at home: {OnOff(_registry.BluetoothAtHome)}");
        text.AppendLine($"Updates: {OnOff(UpdatesEnabled)}, software encoding: {OnOff(ForceSoftwareEncoding)}, Linc Bubble: {OnOff(LiveBubble)}");
        return text.ToString();

        static string OnOff(bool on) => on ? "on" : "off";
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(DiagnosticText());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            DiagnosticsResult = "Copied. Paste it into your message or issue.";
        }
        catch (Exception ex)
        {
            DiagnosticsResult = $"Couldn't copy: {ex.Message}";
        }
    }

    /// <summary>The About and home lines that follow the link; the constructor's StateChanged calls it.</summary>
    private void RaisePhoneAbout()
    {
        OnPropertyChanged(nameof(PhoneAboutTitle));
        OnPropertyChanged(nameof(PhoneAboutText));
        OnPropertyChanged(nameof(HasPhoneConnected));
    }
}
