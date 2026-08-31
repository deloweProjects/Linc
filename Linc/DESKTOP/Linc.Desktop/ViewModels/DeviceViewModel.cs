using System.IO;
using AdvancedSharpAdbClient.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Pickers;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Presentation for the Device page: connection lifecycle lives in
/// <see cref="IConnectionSupervisor"/>; this class renders its state and drives
/// the pairing flow. Files/Notifications live on their own pages now; Mirror is
/// resolved directly by DevicePage via DI rather than through this VM.
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    private readonly IConnectionSupervisor _supervisor;
    private readonly IConnectionManager _connection;
    private readonly IDeviceRegistry _registry;
    private readonly IPairingService _pairing;
    private readonly IPhoneSetupService _phoneSetup;
    private readonly DispatcherQueue _dispatcher;

    private PairingSession? _qrSession;
    private DiscoveredService? _pairingPhone;
    private DeviceData? _pendingUsbDevice;
    private readonly DispatcherTimer _durationTimer;

    public DeviceViewModel(
        IConnectionSupervisor supervisor,
        IConnectionManager connection,
        IDeviceRegistry registry,
        IDiscoveryService discovery,
        IBlePresenceService blePresence,
        IPairingService pairing,
        IPhoneSetupService phoneSetup)
    {
        _supervisor = supervisor;
        _connection = connection;
        _registry = registry;
        _pairing = pairing;
        _phoneSetup = phoneSetup;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        HostPort = registry.LastHostPort ?? "";
        BatteryText = "—";
        StorageText = "—";
        PairingPhoneText = "";
        PairingCode = "";

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(OnLinkStateChanged);
        _supervisor.StatusUpdated += status => _dispatcher.TryEnqueue(() => OnStatusUpdated(status));
        _supervisor.ErrorRaised += message => _dispatcher.TryEnqueue(() => ErrorMessage = message);
        _supervisor.UsbDeviceNeedsConfirmation += device =>
            _dispatcher.TryEnqueue(() => OnUsbDeviceNeedsConfirmation(device));
        discovery.PairingServiceSeen += service =>
            _dispatcher.TryEnqueue(() => OnPairingServiceSeen(service));
        // Bluetooth says the phone is physically here even when its Wi-Fi is off, so the page
        // can say "nearby" instead of an unqualified "looking for it" (M04, D-034).
        blePresence.DeviceSighted += serial =>
        {
            if (serial != _registry.PairedSerial)
            {
                return;
            }
            _lastBleSightingUtc = DateTimeOffset.UtcNow;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SearchingText)));
        };

        _selectedConnectionMethod = MethodFor(registry.ConnectionPreference);
        // A tab switch repoints the registry at another phone, whose preference may differ.
        registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(RefreshConnectionMethod);

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => OnPropertyChanged(nameof(ConnectedDurationText));

        // Started eagerly by AppShellViewModel at launch — starting it here too would
        // double-subscribe the watcher's events every time this page is constructed.
    }

    [ObservableProperty]
    public partial string HostPort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy), nameof(CanPairWithCode))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string BatteryText { get; set; }

    [ObservableProperty]
    public partial string StorageText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearching), nameof(ShowGetStarted), nameof(ShowPaused), nameof(ShowPairing), nameof(ShowConnectionMethod), nameof(ShowDisconnectedArea), nameof(ShowConnectedArea))]
    public partial bool PairingActive { get; set; }

    [ObservableProperty]
    public partial ImageSource? QrImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPairWithCode))]
    public partial string PairingCode { get; set; }

    [ObservableProperty]
    public partial string PairingPhoneText { get; set; }

    // ---- Connection method picker (Auto / USB / Wireless / Direct), per device ----

    /// <summary>One row in the connection-method dropdown.</summary>
    public sealed record ConnectionMethodOption(string Label, ConnectionPreference Preference);

    public IReadOnlyList<ConnectionMethodOption> ConnectionMethods { get; } =
    [
        new("Automatic — best available", ConnectionPreference.Auto),
        new("USB cable only", ConnectionPreference.UsbOnly),
        new("Wireless debugging only", ConnectionPreference.WirelessOnly),
        new("Direct Wi-Fi only (no ADB)", ConnectionPreference.DirectOnly),
    ];

    private ConnectionMethodOption _selectedConnectionMethod;
    private bool _syncingConnectionMethod;

    public ConnectionMethodOption SelectedConnectionMethod
    {
        get => _selectedConnectionMethod;
        set
        {
            if (value is null || !SetProperty(ref _selectedConnectionMethod, value) || _syncingConnectionMethod)
            {
                return;
            }
            // No-ops in the registry when unchanged; otherwise raises ConnectionPreferenceChanged,
            // which the supervisor turns into a live transport switch.
            _registry.SaveConnectionPreference(value.Preference);
        }
    }

    /// <summary>The method dropdown is only meaningful once a phone is paired.</summary>
    public bool ShowConnectionMethod => _registry.PairedSerial is not null && !PairingActive;

    private ConnectionMethodOption MethodFor(ConnectionPreference preference) =>
        ConnectionMethods.FirstOrDefault(m => m.Preference == preference) ?? ConnectionMethods[0];

    private void RefreshConnectionMethod()
    {
        _syncingConnectionMethod = true;
        SelectedConnectionMethod = MethodFor(_registry.ConnectionPreference);
        _syncingConnectionMethod = false;
        OnPropertyChanged(nameof(ShowConnectionMethod));
    }

    public bool IsNotBusy => !IsBusy;
    public bool HasError => ErrorMessage is not null;
    public bool IsConnected => _supervisor.State == LinkState.Connected;
    public bool IsDisconnected => !IsConnected;
    public string DeviceModel => _supervisor.Device?.Model ?? "";
    public string DeviceSerial => _supervisor.Device?.Serial ?? "";
    public int ProtocolVersion => _supervisor.Device?.Companion.V ?? 0;
    public int ReconnectCount => _supervisor.ReconnectCount;
    public string? LastErrorMessage => _supervisor.LastErrorMessage;
    public bool HasLastError => LastErrorMessage is not null;

    public string TransportText => _supervisor.Device?.Transport switch
    {
        LinkTransport.AdbUsb => "USB cable",
        LinkTransport.AdbWireless => "Wireless debugging",
        LinkTransport.DirectTls => "Direct (no ADB)",
        _ => "—",
    };

    // ---- Manual transport override (M15a Part C) ----

    /// <summary>
    /// Whether the live link is the one the user asked for or the one ranking picked. Shown next
    /// to the link itself: "it says wireless" is only useful alongside "and who decided that".
    /// </summary>
    public string TransportChoiceText => _supervisor.Device is null
        ? ""
        : _supervisor.TransportChosenByHand ? "chosen by you" : "chosen automatically";

    public bool ShowTransportOverride => _registry.PairedSerial is not null && !PairingActive;

    /// <summary>True while a transport is pinned, so the "back to automatic" button has a job.</summary>
    public bool HasManualTransport => _supervisor.ManualTransport is not null;

    public bool CanUseWirelessNow => _supervisor.ManualTransport != LinkTransport.AdbWireless;
    public bool CanUseUsbNow => _supervisor.ManualTransport != LinkTransport.AdbUsb;

    [RelayCommand]
    private void UseWirelessNow() => _supervisor.SetManualTransport(LinkTransport.AdbWireless);

    [RelayCommand]
    private void UseUsbNow() => _supervisor.SetManualTransport(LinkTransport.AdbUsb);

    [RelayCommand]
    private void UseAutomaticTransport() => _supervisor.SetManualTransport(null);

    private void NotifyTransportOverrideChanged()
    {
        OnPropertyChanged(nameof(TransportChoiceText));
        OnPropertyChanged(nameof(HasManualTransport));
        OnPropertyChanged(nameof(CanUseWirelessNow));
        OnPropertyChanged(nameof(CanUseUsbNow));
        OnPropertyChanged(nameof(ShowTransportOverride));
    }

    public string LatencyText => _supervisor.LastRttMs is { } rtt ? $"{rtt} ms" : "—";

    public string ConnectedDurationText
    {
        get
        {
            if (_supervisor.ConnectedAtUtc is not { } since)
            {
                return "";
            }
            var elapsed = DateTimeOffset.UtcNow - since;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : elapsed.TotalMinutes >= 1
                    ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
                    : $"{elapsed.Seconds}s";
        }
    }

    public string StateText => _supervisor.State switch
    {
        LinkState.Connected => "Connected",
        LinkState.Connecting => "Connecting…",
        LinkState.Searching => "Searching…",
        LinkState.Paused => "Paused",
        _ => "Not connected",
    };

    public bool ShowSearching =>
        _supervisor.State is LinkState.Searching or LinkState.Connecting && !PairingActive && !ShowUsbDeviceDetected;
    public bool ShowGetStarted => _supervisor.State == LinkState.NoDevice && !PairingActive && !ShowUsbDeviceDetected;
    public bool ShowPaused => _supervisor.State == LinkState.Paused && !PairingActive;

    // Pairing shows whenever it's active, even over a live connection: the "+" tab pairs ANOTHER
    // phone (M2a), so the QR/code card must appear on top of the connected summary rather than
    // being gated out by IsDisconnected the way it was in Era 1.
    public bool ShowPairing => PairingActive;

    /// <summary>The disconnected column (searching / get-started / pairing / paused).</summary>
    public bool ShowDisconnectedArea => IsDisconnected || PairingActive;

    /// <summary>The connected device summary; steps aside while the pairing card is up.</summary>
    public bool ShowConnectedArea => IsConnected && !PairingActive;
    /// <summary>
    /// M15c A1: this used to require <c>IsDisconnected</c>, which is exactly why plugging a second
    /// phone in while one was live did nothing visible — the card was suppressed in the one case
    /// the user most needed it. It now shows over a live link too, as a switch offer.
    /// </summary>
    public bool ShowUsbDeviceDetected => !PairingActive && _pendingUsbDevice is not null;

    public string UsbDeviceText => _pendingUsbDevice is { } device ? $"{device.Model} ({device.Serial})" : "";

    /// <summary>The live phone as the user knows it, or null when nothing is connected.</summary>
    private string? LiveDeviceName =>
        _supervisor.State == LinkState.Connected ? _supervisor.Device?.Model ?? _supervisor.Device?.Serial : null;

    /// <summary>
    /// The card's body while a phone is live: the supervisor's own sentence for this switch, from
    /// the same pure static it decides with — no second copy of the rule (M15c A1).
    /// </summary>
    public string UsbDeviceMessage => LiveDeviceName is { } live && _pendingUsbDevice is { } device
        ? DeviceAdmission.DecideDeviceSwitch(live, device.Serial ?? "", LinkActivity.Connected).Message
        : "Connect once and Linc will remember this phone for next time.";

    /// <summary>The confirming button's label — it names both phones when this is a switch.</summary>
    public string UsbPrimaryActionText =>
        DeviceAdmission.SwitchActionLabel(LiveDeviceName, _pendingUsbDevice?.Model ?? _pendingUsbDevice?.Serial ?? "")
            ?? "Connect";

    /// <summary>M15c A2: the standing "one phone at a time" line, shown while a phone is live.</summary>
    public string OneAtATimeText => DeviceAdmission.OneAtATimeNotice(LiveDeviceName) ?? "";

    public bool ShowOneAtATimeNote => OneAtATimeText.Length > 0;

    /// <summary>When the paired phone's Bluetooth beacon was last heard (M04).</summary>
    private DateTimeOffset _lastBleSightingUtc = DateTimeOffset.MinValue;

    private bool IsNearby => DateTimeOffset.UtcNow - _lastBleSightingUtc < TimeSpan.FromSeconds(45);

    public string SearchingText => _supervisor.State == LinkState.Connecting
        ? "Connecting to your phone…"
        : IsNearby
            ? $"{_registry.PairedModel ?? "Your phone"} is nearby — connecting…"
            : $"Looking for {_registry.PairedModel ?? "your phone"}…";

    public bool CanPairWithCode => _pairingPhone is not null && PairingCode.Trim().Length >= 6 && !IsBusy;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _supervisor.ConnectManualAsync(HostPort);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Disconnect() => _supervisor.RequestDisconnect();

    [RelayCommand]
    private void Resume() => _supervisor.ResumeAutomatic();

    [RelayCommand]
    private void StartPairing()
    {
        _qrSession = _pairing.CreateQrSession();
        QrImage = CreateQrImage(_qrSession.QrText);
        _pairingPhone = null;
        PairingCode = "";
        PairingPhoneText = "Waiting for the phone… open the pairing dialog to be discovered.";
        ErrorMessage = null;
        PairingActive = true;
    }

    [RelayCommand]
    private void CancelPairing()
    {
        _qrSession = null;
        PairingActive = false;
    }

    [RelayCommand]
    private async Task ConnectUsbAsync()
    {
        if (_pendingUsbDevice is not { } device)
        {
            return;
        }
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _supervisor.ConnectUsbManualAsync(device);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancel (M15c A1). It must be a genuine no-op on the live link — it dismisses the card and
    /// nothing else — and it must not come straight back on the next 3 s USB poll, so the
    /// supervisor is told to stop offering this serial until the cable is pulled and replugged.
    /// </summary>
    [RelayCommand]
    private void DismissUsbDevice()
    {
        if (_pendingUsbDevice?.Serial is { Length: > 0 } serial)
        {
            _supervisor.DeclineUsbDevice(serial);
        }
        _pendingUsbDevice = null;
        NotifyUsbCardChanged();
    }

    private void OnUsbDeviceNeedsConfirmation(DeviceData device)
    {
        _pendingUsbDevice = device;
        NotifyUsbCardChanged();
    }

    private void NotifyUsbCardChanged()
    {
        OnPropertyChanged(nameof(ShowUsbDeviceDetected));
        OnPropertyChanged(nameof(UsbDeviceText));
        OnPropertyChanged(nameof(UsbDeviceMessage));
        OnPropertyChanged(nameof(UsbPrimaryActionText));
        OnPropertyChanged(nameof(ShowSearching));
        OnPropertyChanged(nameof(ShowGetStarted));
    }

    [RelayCommand]
    private async Task PairWithCodeAsync()
    {
        if (_pairingPhone is not null)
        {
            await PairCoreAsync(_pairingPhone, PairingCode.Trim());
        }
    }

    private void OnLinkStateChanged()
    {
        if (IsConnected)
        {
            PairingActive = false;
            // A connect resolves whatever the card was offering: either this phone came up, or the
            // switch the card proposed has happened. Either way there is nothing left to confirm.
            _pendingUsbDevice = null;
            _durationTimer.Start();
        }
        else
        {
            _durationTimer.Stop();
        }
        OnPropertyChanged(nameof(ShowUsbDeviceDetected));
        OnPropertyChanged(nameof(UsbDeviceText));
        OnPropertyChanged(nameof(UsbDeviceMessage));
        OnPropertyChanged(nameof(UsbPrimaryActionText));
        OnPropertyChanged(nameof(OneAtATimeText));
        OnPropertyChanged(nameof(ShowOneAtATimeNote));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ShowDisconnectedArea));
        OnPropertyChanged(nameof(ShowConnectedArea));
        NotifyTransportOverrideChanged();
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DeviceModel));
        OnPropertyChanged(nameof(DeviceSerial));
        OnPropertyChanged(nameof(ProtocolVersion));
        OnPropertyChanged(nameof(ReconnectCount));
        OnPropertyChanged(nameof(LastErrorMessage));
        OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(ConnectedDurationText));
        OnPropertyChanged(nameof(TransportText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(ShowSearching));
        OnPropertyChanged(nameof(ShowGetStarted));
        OnPropertyChanged(nameof(ShowPaused));
        OnPropertyChanged(nameof(ShowPairing));
        OnPropertyChanged(nameof(SearchingText));
        OnPropertyChanged(nameof(ShowConnectionMethod)); // pairing/connecting can create the record
        if (IsConnected)
        {
            ErrorMessage = null;
        }
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        BatteryText = status.Charging ? $"{status.Battery}% (charging)" : $"{status.Battery}%";
        StorageText = $"{Gb(status.StorageFreeBytes)} GB free of {Gb(status.StorageTotalBytes)} GB";
        OnPropertyChanged(nameof(LatencyText)); // fresh RTT rides every health probe
    }

    private void OnPairingServiceSeen(DiscoveredService service)
    {
        if (!PairingActive || IsBusy)
        {
            return;
        }
        _pairingPhone = service;
        PairingPhoneText = $"Phone found at {service.IpAddress}:{service.Port}.";
        OnPropertyChanged(nameof(CanPairWithCode));

        // QR flow: the phone advertises the instance name from the QR we showed,
        // so pairing completes hands-free with the QR's password.
        if (_qrSession is not null && service.InstanceName == _qrSession.ServiceName)
        {
            _ = PairCoreAsync(service, _qrSession.Password);
        }
    }

    private async Task PairCoreAsync(DiscoveredService phone, string code)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _pairing.PairAsync(phone.IpAddress, phone.Port, code, CancellationToken.None);
            _qrSession = null;
            PairingActive = false;
            _supervisor.ExpectNextDevice(); // connect to the first advert this phone sends
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Install app (M10 Part A) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallApk))]
    public partial bool IsInstallingApk { get; set; }

    public bool CanInstallApk => !IsInstallingApk;

    [ObservableProperty]
    public partial string ApkInstallStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApkInstallError))]
    public partial string? ApkInstallErrorMessage { get; set; }

    public bool HasApkInstallError => ApkInstallErrorMessage is not null;

    [RelayCommand]
    private async Task InstallApkAsync()
    {
        // In-flight guard (A2.4): one install at a time. The button's IsEnabled is bound to
        // CanInstallApk, so this also disables it for the run's whole duration.
        IsInstallingApk = true;
        ApkInstallErrorMessage = null;
        ApkInstallStatusText = "";
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".apk");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return; // user cancelled the picker — no error to show
            }

            ApkInstallStatusText = ApkInstall.StageText(ApkInstall.Stage.Validating);
            var validation = ApkInstall.ValidateApkPath(file.Path, IsConnected);
            if (!validation.IsValid)
            {
                ApkInstallStatusText = "";
                ApkInstallErrorMessage = validation.Message;
                return;
            }

            if (_connection.RawDevice is not { } device)
            {
                ApkInstallErrorMessage = "No phone is connected.";
                return;
            }

            ApkInstallStatusText = ApkInstall.StageText(ApkInstall.Stage.CopyingToPhone);
            var (success, rawError) = await _phoneSetup.InstallApkAsync(
                device, file.Path, OnInstallProgress, CancellationToken.None);

            if (success)
            {
                ApkInstallStatusText = ApkInstall.StageText(ApkInstall.Stage.Done);
            }
            else
            {
                ApkInstallStatusText = "";
                ApkInstallErrorMessage = ApkInstall.TranslateInstallFailure(rawError ?? "");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never throw into the UI thread (A2.7) — fall back to the generic message.
            ApkInstallStatusText = "";
            ApkInstallErrorMessage = ApkInstall.TranslateInstallFailure("");
        }
        finally
        {
            IsInstallingApk = false;
        }
    }

    /// <summary>
    /// Maps the ADB client's real upload/install sub-states onto our two mid-run stages, so
    /// "Copying to phone" / "Installing" reflect what is actually happening rather than being
    /// two labels shown back-to-back with nothing between them. Fires off the calling thread.
    /// </summary>
    private void OnInstallProgress(InstallProgressEventArgs e)
    {
        var stage = e.State switch
        {
            PackageInstallProgressState.Installing or PackageInstallProgressState.PostInstall
                => ApkInstall.Stage.Installing,
            _ => ApkInstall.Stage.CopyingToPhone,
        };
        _dispatcher.TryEnqueue(() => ApkInstallStatusText = ApkInstall.StageText(stage));
    }

    private static BitmapImage CreateQrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.SetSource(stream.AsRandomAccessStream());
        return image;
    }

    private static string Gb(long bytes) => (bytes / 1_000_000_000.0).ToString("0.0");
}
