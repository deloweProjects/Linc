using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Linc.Desktop.Services.Tools;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// The Tools page ("ADB with a face", Vibe/claude-notes/linc-tools): which phone, which section,
/// the command caption and the notice line. It finds phones straight from adb, so it works over
/// USB or wireless debugging even when the Linc app isn't running on the phone.
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    public const string TypeName = "Type", RescueName = "Rescue", AppsName = "Apps", CaptureName = "Capture",
        HealthName = "Health", SystemName = "System", CommandsName = "Commands", ConsoleName = "Console";

    private readonly ToolsContext _ctx;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, ToolSection> _sections;
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _noticeClear;
    private bool _active;
    private readonly IConnectionSupervisor _link;
    private readonly IHotspotLinkService _hotspot;
    private readonly SemaphoreSlim _wake = new(0);

    public ToolsViewModel(IConnectionSupervisor link, IHotspotLinkService hotspot, ToolsContext ctx, TypeToolViewModel type, RescueToolViewModel rescue, AppsToolViewModel apps,
        CaptureToolViewModel capture, HealthToolViewModel health, SystemToolViewModel system, CommandsToolViewModel commands,
        ConsoleToolViewModel console)
    {
        _ctx = ctx;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        TypeTool = type; RescueTool = rescue; AppsTool = apps; CaptureTool = capture; HealthTool = health; SystemTool = system; CommandsTool = commands; ConsoleTool = console;
        _sections = new()
        {
            [TypeName] = type, [RescueName] = rescue, [AppsName] = apps, [CaptureName] = capture,
            [HealthName] = health, [SystemName] = system, [CommandsName] = commands, [ConsoleName] = console,
        };
        ctx.CommandShown += command => _dispatcher.TryEnqueue(() => LastCommand = "⌘ " + command);
        ctx.Notice += text => _dispatcher.TryEnqueue(() => ShowNotice(text));
        _link = link;
        _hotspot = hotspot;
        // Tools follows Linc's own connection too, so its card says what Home says: a phone that is
        // connected without ADB is "connected, Tools needs ADB", never "connect your phone". A change
        // also looks for adb devices at once (wireless debugging coming up under the link).
        link.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(CanTurnOnWireless));
            if (_active) _wake.Release();
        });
    }

    public TypeToolViewModel TypeTool { get; }
    public RescueToolViewModel RescueTool { get; }
    public AppsToolViewModel AppsTool { get; }
    public CaptureToolViewModel CaptureTool { get; }
    public HealthToolViewModel HealthTool { get; }
    public SystemToolViewModel SystemTool { get; }
    public CommandsToolViewModel CommandsTool { get; }
    public ConsoleToolViewModel ConsoleTool { get; }

    public ObservableCollection<ToolsDevice> Devices { get; } = [];

    /// <summary>The rail, in order (PLAN.md §3).</summary>
    public IReadOnlyList<ToolsSectionItem> Sections { get; } =
    [
        new(TypeName, "\uE765", "Type on the phone from this keyboard"),
        new(RescueName, "\uE83D", "Screen broken or black? Get in and save your files"),
        new(AppsName, "\uE71D", "Open, stop, remove and back up apps"),
        new(CaptureName, "\uE722", "Screenshots and screen recordings"),
        new(HealthName, "\uE95E", "Battery, temperature, storage and what's busy"),
        new(SystemName, "\uE713", "Dark mode, screen timeout, brightness and more"),
        new(CommandsName, "\uE721", "Find an adb command by what it does, fill it in, run or copy it"),
        new(ConsoleName, "\uE756", "Type adb shell commands yourself", StartsAdvanced: true),
    ];

    /// <summary>The rail's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedSectionItem
    {
        get => Sections.FirstOrDefault(s => s.Name == Section);
        set { if (value is ToolsSectionItem item) SelectSection(item.Name); }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsType), nameof(IsRescue), nameof(IsApps), nameof(IsCapture),
        nameof(IsHealth), nameof(IsSystem), nameof(IsCommands), nameof(IsConsole))]
    public partial string Section { get; set; } = TypeName;

    partial void OnSectionChanged(string value) => OnPropertyChanged(nameof(SelectedSectionItem));

    public bool IsType => Section == TypeName;
    public bool IsRescue => Section == RescueName;
    public bool IsApps => Section == AppsName;
    public bool IsCapture => Section == CaptureName;
    public bool IsHealth => Section == HealthName;
    public bool IsSystem => Section == SystemName;
    public bool IsCommands => Section == CommandsName;
    public bool IsConsole => Section == ConsoleName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhone), nameof(NoPhone), nameof(DeviceLabel), nameof(EmptyTitle), nameof(EmptyText))]
    public partial ToolsDevice? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoPhone), nameof(EmptyTitle), nameof(EmptyText))]
    public partial bool Searched { get; set; }

    public bool HasPhone => SelectedDevice is { Authorized: true };

    /// <summary>The "plug your phone in" card; hidden for the first look so it doesn't flash.</summary>
    public bool NoPhone => Searched && !HasPhone;

    public bool ManyDevices => Devices.Count > 1;

    public bool SingleDevice => !ManyDevices;

    /// <summary>The phone picker's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedDeviceItem
    {
        get => SelectedDevice;
        set { if (value is ToolsDevice d && d != SelectedDevice) SelectedDevice = d; }
    }

    public string DeviceLabel => SelectedDevice?.Label ?? "No phone";

    /// <summary>Linc itself is connected to a phone (any route), whether or not adb can see it.</summary>
    private bool LincConnected => _link.State == LinkState.Connected && _link.Device is not null;

    public string EmptyTitle => SelectedDevice is { Authorized: false }
        ? "Allow USB debugging on the phone"
        : LincConnected
            ? $"{_link.Device!.Model} is connected, but Tools needs ADB"
            : "Connect your phone";

    public string EmptyText => SelectedDevice is { Authorized: false }
        ? "The phone is asking whether to trust this PC. Tap Allow on the phone (tick \"Always allow\"), and Tools opens here."
        : LincConnected
            ? "Tools works through USB debugging or wireless debugging. Plug in the cable, or let Linc turn on wireless debugging on the phone."
            : "Plug it in with a USB cable, or pair it over Wi-Fi on the Device page. Tools needs USB debugging on; the Linc app doesn't have to be running.";

    /// <summary>Linc is connected without ADB: the phone can switch wireless debugging on itself.</summary>
    public bool CanTurnOnWireless => LincConnected && !HasPhone && !_arming;

    private bool _arming;

    /// <summary>Asks the phone (over Linc's own link) to turn wireless debugging on; the PC then
    /// connects to it as it does after a restart, and Tools sees it within a few seconds.</summary>
    [RelayCommand]
    private async Task TurnOnWirelessAsync()
    {
        _arming = true;
        OnPropertyChanged(nameof(CanTurnOnWireless));
        ShowNotice("Asked the phone to turn on wireless debugging…");
        try
        {
            await _hotspot.RequestArmAsync(HotspotArmFallback.Prefer, CancellationToken.None);
            for (var i = 0; i < 15 && !HasPhone; i++)
            {
                _wake.Release();
                await Task.Delay(1000);
            }
            if (!HasPhone)
            {
                ShowNotice("The phone didn't turn it on by itself. On the phone: Settings › Developer options › Wireless debugging.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowNotice("Couldn't ask the phone: " + ex.Message);
        }
        finally
        {
            _arming = false;
            OnPropertyChanged(nameof(CanTurnOnWireless));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastCommand))]
    public partial string LastCommand { get; set; } = "";

    public bool HasLastCommand => LastCommand.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; set; } = "";

    public bool HasNotice => Notice.Length > 0;

    [RelayCommand]
    private void SelectSection(string name)
    {
        if (!_sections.ContainsKey(name) || name == Section) return;
        if (_active && HasPhone) _sections[Section].Hidden();
        Section = name;
        LastCommand = "";
        if (_active && HasPhone) _ = ShowCurrentAsync();
    }

    /// <summary>The page opened: start watching for phones and open the current section.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
        if (HasPhone) _ = ShowCurrentAsync(); // back on the page with the same phone
    }

    /// <summary>The page closed: stop everything live.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _watch?.Cancel();
        _watch = null;
        if (HasPhone) _sections[Section].Hidden();
    }

    partial void OnSelectedDeviceChanged(ToolsDevice? oldValue, ToolsDevice? newValue)
    {
        if (oldValue?.Serial == newValue?.Serial && oldValue?.Authorized == newValue?.Authorized) return;
        if (_active && oldValue is { Authorized: true }) _sections[Section].Hidden();
        _ctx.Device = newValue;
        _ctx.Sdk = 0;
        OnPropertyChanged(nameof(SelectedDeviceItem));
        if (_active && newValue is { Authorized: true }) _ = ShowCurrentAsync(readSdk: true);
    }

    private async Task ShowCurrentAsync(bool readSdk = false)
    {
        try
        {
            if (readSdk || _ctx.Sdk == 0)
            {
                var sdk = await _ctx.Shell.RunAsync(_ctx.Serial, "getprop ro.build.version.sdk");
                _ctx.Sdk = int.TryParse(sdk.Text.Trim(), out var n) ? n : 0;
            }
            await _sections[Section].ShownAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowNotice("Something went wrong talking to the phone: " + ex.Message);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var found = await _ctx.Shell.DevicesAsync(ct);
            if (ct.IsCancellationRequested) return;
            UpdateDevices(found);
            Searched = true;
            OnPropertyChanged(nameof(CanTurnOnWireless));
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void UpdateDevices(IReadOnlyList<ToolsDevice> found)
    {
        if (!found.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var d in found) Devices.Add(d);
            OnPropertyChanged(nameof(ManyDevices));
            OnPropertyChanged(nameof(SingleDevice));
        }
        // Keep the phone in use while it's there; otherwise the first allowed one.
        var keep = SelectedDevice is { } current ? found.FirstOrDefault(d => d.Serial == current.Serial) : null;
        var pick = keep ?? found.FirstOrDefault(d => d.Authorized) ?? found.FirstOrDefault();
        if (pick != SelectedDevice) SelectedDevice = pick;
    }

    private void ShowNotice(string text)
    {
        Notice = text;
        _noticeClear?.Cancel();
        var cts = _noticeClear = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(8), cts.Token).ContinueWith(
            t => { if (!t.IsCanceled) _dispatcher.TryEnqueue(() => { if (!cts.IsCancellationRequested) Notice = ""; }); },
            TaskScheduler.Default);
    }

    [RelayCommand]
    private void ClearNotice() => Notice = "";
}

/// <summary>One entry of the Tools rail. <see cref="ToString"/> is its UI Automation name.</summary>
public sealed record ToolsSectionItem(string Name, string Glyph, string Hint, bool StartsAdvanced = false)
{
    public override string ToString() => "Tools: " + Name;
}
