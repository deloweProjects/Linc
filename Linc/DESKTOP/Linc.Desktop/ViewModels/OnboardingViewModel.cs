using System.Collections.ObjectModel;
using System.IO;
using AdvancedSharpAdbClient.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace Linc.Desktop.ViewModels;

/// <summary>A phone this PC already knows, offered on the Choose screen.</summary>
public sealed class KnownPhoneOption
{
    public KnownPhoneOption(string serial, string model, string detail, Action<KnownPhoneOption> pick)
    {
        Serial = serial;
        Model = model;
        Detail = detail;
        // On the item so the DataTemplate can x:Bind it directly.
        PickCommand = new RelayCommand(() => pick(this));
    }

    public string Serial { get; }
    public string Model { get; }
    public string Detail { get; }
    public RelayCommand PickCommand { get; }
}

/// <summary>
/// The one front door for phones (D-079): first setup, adding another, and bringing back one
/// that was closed. Choose → (Prepare USB | Prepare Wi-Fi) → Checking → Done.
///
/// <list type="bullet">
/// <item><b>Choose</b> lists the phones this PC already knows (reconnect, no pairing) and the ways
/// to add a new one — USB cable, Wi-Fi (QR or code), or "already set up" to skip pairing. If a phone
/// is connected it says plainly that adding another disconnects it.</item>
/// <item><b>Checking</b> watches the real connection and says what is happening; after a while
/// without progress it shows what to check and offers another way.</item>
/// <item><b>Done</b> knows whether the phone is new or was already added (its hardware serial
/// against the phones known when the flow opened) and says so.</item>
/// </list>
/// It wraps the existing machinery (pairing, the supervisor, PhoneSetupService) rather than
/// reinventing it. Transient: <see cref="Dispose"/> detaches everything and, if the flow was left
/// before finishing, puts the supervisor back to normal reconnecting.
/// </summary>
public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDiscoveryService _discovery;
    private readonly IUsbWatcherService _usbWatcher;
    private readonly IPairingService _pairing;
    private readonly IPhoneSetupService _setup;
    private readonly IDeviceRegistry _registry;
    private readonly ITlsTransportService _tls;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<string> _knownBefore;
    private readonly DispatcherQueueTimer _slowTimer;

    private PairingSession? _qrSession;
    private DiscoveredService? _pairingPhone;
    private OnboardingRoute? _route;
    private string? _target;   // the known phone picked, if that was the route
    private bool _armed;
    private bool _finished;
    private bool _disposed;

    public OnboardingViewModel(
        IConnectionSupervisor supervisor,
        IDiscoveryService discovery,
        IUsbWatcherService usbWatcher,
        IPairingService pairing,
        IPhoneSetupService setup,
        IDeviceRegistry registry,
        ITlsTransportService tls,
        ILogService log)
    {
        _supervisor = supervisor;
        _discovery = discovery;
        _usbWatcher = usbWatcher;
        _pairing = pairing;
        _setup = setup;
        _registry = registry;
        _tls = tls;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _knownBefore = new HashSet<string>(registry.KnownDevices.Select(d => d.Serial), StringComparer.OrdinalIgnoreCase);

        PairingCode = "";
        PairingPhoneText = "";
        CheckingTitle = "Looking for your phone…";
        CheckingDetail = "";
        DoneTitle = "";
        DoneDetail = "";

        foreach (var device in registry.KnownDevices)
        {
            // The phone that is connected right now needs no reconnecting.
            if (device.Serial == registry.PairedSerial && supervisor.State == LinkState.Connected)
            {
                continue;
            }
            var detail = device.Hidden ? "Closed — pick it to bring it back" : "Paired — pick it to connect";
            KnownPhones.Add(new KnownPhoneOption(device.Serial, device.Model, detail, PickKnown));
        }
        _slowTimer = _dispatcher.CreateTimer();
        _slowTimer.Interval = TimeSpan.FromSeconds(25);
        _slowTimer.IsRepeating = false;
        _slowTimer.Tick += (_, _) => ShowSlowHints = Step == OnboardingStep.Checking;

        _supervisor.StateChanged += OnSupervisorStateChanged;
        _discovery.PairingServiceSeen += OnPairingServiceSeen;
        _usbWatcher.UsbDeviceSeen += OnUsbDeviceSeen;
        _tls.PhonePaired += OnLincPaired;
    }

    // ---- Step state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsPrepareUsb), nameof(IsPrepareWifi), nameof(IsChecking),
        nameof(IsDone), nameof(ShowBack), nameof(CancelText))]
    public partial OnboardingStep Step { get; set; } = OnboardingStep.Choose;

    public bool IsChoose => Step == OnboardingStep.Choose;
    public bool IsPrepareUsb => Step == OnboardingStep.PrepareUsb;
    public bool IsPrepareWifi => Step == OnboardingStep.PrepareWifi;
    public bool IsChecking => Step == OnboardingStep.Checking;
    public bool IsDone => Step == OnboardingStep.Done;
    public bool ShowBack => Step is OnboardingStep.PrepareUsb or OnboardingStep.PrepareWifi or OnboardingStep.Checking;
    public string CancelText => Step == OnboardingStep.Done ? "Close" : "Cancel";

    // ---- Choose ----

    public ObservableCollection<KnownPhoneOption> KnownPhones { get; } = [];
    public bool HasKnownPhones => KnownPhones.Count > 0;

    /// <summary>"Adding another phone disconnects Pixel 7" — said before anything is chosen.</summary>
    public string OneAtATimeText => _supervisor.State == LinkState.Connected
        ? DeviceAdmission.OneAtATimeNotice(_supervisor.Device?.Model) ?? ""
        : "";

    public bool ShowOneAtATimeNote => OneAtATimeText.Length > 0;

    public bool ApkMissing => _setup.LocateApk() is null && !SkipCompanionInstall;

    /// <summary>"Linc is already on my phone — don't install it" (D-089).</summary>
    public bool SkipCompanionInstall
    {
        get => _setup.SkipCompanionInstall;
        set
        {
            if (_setup.SkipCompanionInstall == value) return;
            _setup.SkipCompanionInstall = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApkMissing));
        }
    }

    // ---- Wi-Fi pairing ----

    /// <summary>
    /// LincLink (v32): Linc's own pairing code, for a phone with Linc installed — no cable and no
    /// debugging. The phone's camera opens the <c>linc://pair</c> link it carries.
    /// </summary>
    [ObservableProperty]
    public partial ImageSource? LincQrImage { get; set; }

    public bool ShowLincCode => _registry.LincLinkEnabled;

    /// <summary>
    /// The six digits the phone shows before it pairs, worked out from the code on both sides: the
    /// same number on both screens means the phone scanned this PC's code and not someone else's.
    /// </summary>
    [ObservableProperty]
    public partial string? LincCheckCode { get; set; }

    private void OnLincPaired(string model, System.Net.IPEndPoint? listener)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || Step != OnboardingStep.PrepareWifi) return;
            CheckingTitle = $"Paired with {model} — connecting…";
            CheckingDetail = "No cable or debugging needed: the two find each other on the Wi-Fi.";
            EnterChecking(OnboardingFlow.PhoneFound(Step));
        });
    }

    [ObservableProperty]
    public partial ImageSource? QrImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPairWithCode))]
    public partial string PairingCode { get; set; }

    [ObservableProperty]
    public partial string PairingPhoneText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanPairWithCode))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPairWithCode))]
    public partial bool IsBusy { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool CanPairWithCode => _pairingPhone is not null && PairingCode.Trim().Length >= 6 && !IsBusy;

    // ---- Checking / Done ----

    [ObservableProperty]
    public partial string CheckingTitle { get; set; }

    [ObservableProperty]
    public partial string CheckingDetail { get; set; }

    [ObservableProperty]
    public partial bool ShowSlowHints { get; set; }

    public string SlowHintsText => _route switch
    {
        OnboardingRoute.Usb => "Taking a while? Check the cable carries data (some are charge-only), unlock the phone, and tap Allow on \"Allow USB debugging?\". Still nothing — try Wi-Fi instead.",
        OnboardingRoute.Wifi => "Taking a while? Keep the phone on the Wireless debugging screen, on the same Wi-Fi as this PC, and scan the QR again — or try a cable.",
        OnboardingRoute.KnownPhone => "Taking a while? Unlock the phone and open Linc on it, check it's on the same Wi-Fi as this PC (or plug it in), and make sure Wireless debugging is still on.",
        _ => "Taking a while? Open Linc on the phone and check it's on the same Wi-Fi as this PC — or plug it in. If it was never paired with this PC, go back and add it as a new phone.",
    };

    [ObservableProperty]
    public partial string DoneTitle { get; set; }

    [ObservableProperty]
    public partial string DoneDetail { get; set; }

    /// <summary>Raised when the flow should close (cancel, finish).</summary>
    public event Action? CloseRequested;

    // ---- Choose commands ----

    [RelayCommand]
    private void ChooseUsb() => Go(OnboardingRoute.Usb);

    [RelayCommand]
    private void ChooseWifi() => Go(OnboardingRoute.Wifi);

    [RelayCommand]
    private void ChooseAlreadySetUp() => Go(OnboardingRoute.AlreadySetUp);

    /// <summary>A phone this PC already knows: un-close it, make it active, and wait for it.</summary>
    private void PickKnown(KnownPhoneOption option)
    {
        _target = option.Serial;
        _route = OnboardingRoute.KnownPhone;
        ErrorMessage = null;
        _registry.SetDeviceHidden(option.Serial, hidden: false);
        if (_registry.PairedSerial != option.Serial)
        {
            _registry.SetActiveDevice(option.Serial); // the supervisor drops the old link and hunts this one
        }
        else
        {
            _supervisor.ResumeAutomatic();
        }
        _supervisor.NudgeReconnect($"Reconnecting {option.Model} from the add-a-phone screen");
        _log.Log(LogLevel.Info, $"Add a phone: reconnecting {option.Model}, a phone this PC already knows.");
        CheckingTitle = $"Connecting {option.Model}…";
        CheckingDetail = "It's already paired, so there's nothing to set up — just finding it on Wi-Fi or USB.";
        EnterChecking(OnboardingFlow.Choose(Step, OnboardingRoute.KnownPhone));
    }

    /// <summary>A new phone (or "already set up"): get the supervisor ready for it.</summary>
    private void Go(OnboardingRoute route)
    {
        _route = route;
        _target = null;
        ErrorMessage = null;
        Arm(newPhoneOnly: route != OnboardingRoute.AlreadySetUp);
        var next = OnboardingFlow.Choose(Step, route);
        if (next == OnboardingStep.PrepareWifi && _registry.LincLinkEnabled)
        {
            var invite = _tls.OpenPairing();
            LincQrImage = CreateQrImage(LinkPairing.ToUri(invite));
            LincCheckCode = $"The phone will show {LinkPairing.CheckCode(invite.PcCertSha256, invite.Token)}. Pair only if it does.";
        }
        if (next == OnboardingStep.Checking)
        {
            CheckingTitle = "Looking for your phone…";
            CheckingDetail = "Open Linc on the phone. Linc connects over Wi-Fi or USB, whichever it finds first.";
            _supervisor.NudgeReconnect("Looking for an already set-up phone");
            EnterChecking(next);
        }
        else
        {
            Step = next;
        }
    }

    private void Arm(bool newPhoneOnly)
    {
        if (!_armed)
        {
            _armed = true;
            _log.Log(LogLevel.Info, "Add a phone: watching for a phone to connect (USB or wireless pairing).");
            _qrSession = _pairing.CreateQrSession();
            QrImage = CreateQrImage(_qrSession.QrText);
            _pairingPhone = null;
            PairingPhoneText = "Waiting for your phone…";
        }
        // Connecting a new phone while another is connected: let go of that one first, so the new
        // one isn't held up behind it or asked about in a card.
        if (newPhoneOnly && _supervisor.State == LinkState.Connected)
        {
            _supervisor.RequestDisconnect();
        }
        _supervisor.ExpectNextDevice(newPhoneOnly);
    }

    private void EnterChecking(OnboardingStep step)
    {
        Step = step;
        ShowSlowHints = false;
        OnPropertyChanged(nameof(SlowHintsText));
        _slowTimer.Stop();
        _slowTimer.Start();
        // Already connected to exactly what was asked for (e.g. it reconnected the moment it was picked).
        if (_supervisor.State == LinkState.Connected) CheckConnected();
    }

    // ---- Prepare commands ----

    /// <summary>"My phone is plugged in" on the USB screen.</summary>
    [RelayCommand]
    private void PhoneReady()
    {
        CheckingTitle = "Looking for your phone…";
        CheckingDetail = "Accept \"Allow USB debugging?\" on the phone if it asks.";
        EnterChecking(OnboardingFlow.PhoneFound(Step));
    }

    [RelayCommand]
    private async Task PairWithCodeAsync()
    {
        if (_pairingPhone is { } phone)
        {
            await PairCoreAsync(phone, PairingCode.Trim());
        }
    }

    [RelayCommand]
    private void Back()
    {
        _slowTimer.Stop();
        ShowSlowHints = false;
        ErrorMessage = null;
        Step = OnboardingFlow.Back(Step);
    }

    [RelayCommand]
    private void Finish() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ---- Events from the machinery ----

    private void OnUsbDeviceSeen(DeviceData device)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (Step == OnboardingStep.PrepareUsb)
            {
                // Debugging is on and the cable is in: no need to press anything.
                CheckingTitle = "Phone found over USB — connecting…";
                CheckingDetail = "Accept \"Allow USB debugging?\" on the phone if it asks.";
                EnterChecking(OnboardingFlow.PhoneFound(Step));
            }
        });
    }

    private void OnPairingServiceSeen(DiscoveredService service)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || Step != OnboardingStep.PrepareWifi || IsBusy) return;
            _pairingPhone = service;
            PairingPhoneText = $"Phone found at {service.IpAddress}:{service.Port}.";
            OnPropertyChanged(nameof(CanPairWithCode));
            // QR: the phone advertises the name from our QR, so pairing finishes hands-free.
            if (_qrSession is not null && service.InstanceName == _qrSession.ServiceName)
            {
                _ = PairCoreAsync(service, _qrSession.Password);
            }
        });
    }

    private async Task PairCoreAsync(DiscoveredService phone, string code)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _pairing.PairAsync(phone.IpAddress, phone.Port, code, CancellationToken.None);
            _supervisor.ExpectNextDevice(newPhoneOnly: true); // adopt the first advert this phone now sends
            CheckingTitle = "Paired — connecting to your phone…";
            CheckingDetail = SkipCompanionInstall
                ? "Connecting to the Linc app on your phone. Open it there if it's closed."
                : "Linc is setting up its companion app on the phone. Nothing to tap.";
            EnterChecking(OnboardingFlow.PhoneFound(Step));
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

    private void OnSupervisorStateChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(OneAtATimeText));
        OnPropertyChanged(nameof(ShowOneAtATimeNote));
        if (Step != OnboardingStep.Checking) return;
        switch (_supervisor.State)
        {
            case LinkState.Connecting:
                CheckingTitle = "Connecting — setting up your phone…";
                CheckingDetail = SkipCompanionInstall
                    ? "Starting the Linc app on your phone. Nothing to tap."
                    : "Installing or starting the Linc companion and turning on its permissions. Nothing to tap.";
                break;
            case LinkState.Connected:
                CheckConnected();
                break;
        }
    });

    /// <summary>Connected: finish — saying whether this phone is new or was already added.</summary>
    private void CheckConnected()
    {
        if (_supervisor.Device is not { } device) return;
        // Picked a known phone but a different one answered: keep waiting for the one asked for.
        if (_target is not null && !string.Equals(device.CacheKey, _target, StringComparison.OrdinalIgnoreCase)) return;

        _slowTimer.Stop();
        ShowSlowHints = false;
        _finished = true;
        _registry.SetDeviceHidden(device.CacheKey, hidden: false); // it's a tab again, whatever route
        bool already = OnboardingFlow.WasAlreadyAdded(device.CacheKey, _knownBefore);
        DoneTitle = already ? $"{device.Model} was already added" : $"Added {device.Model}";
        DoneDetail = already
            ? "It's connected again — nothing new was set up. Its tab is at the top."
            : "It's connected and saved as a tab at the top. Linc reconnects to it by itself from now on — over Wi-Fi, or whenever it's plugged in.";
        Step = OnboardingFlow.Connected(Step);
        _log.Log(LogLevel.Info, already
            ? $"Add a phone: {device.Model} was already added; connected again."
            : $"Add a phone: added {device.Model}.");
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

    public void Dispose()
    {
        _setup.SkipCompanionInstall = false;
        if (_disposed) return;
        _disposed = true;
        _slowTimer.Stop();
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _discovery.PairingServiceSeen -= OnPairingServiceSeen;
        _usbWatcher.UsbDeviceSeen -= OnUsbDeviceSeen;
        _tls.PhonePaired -= OnLincPaired;
        _tls.ClosePairing();
        // Left before finishing: stop expecting a phone and go back to normal reconnecting, so a
        // cancelled "add" can't leave the link paused or waiting for a phone nobody is adding.
        if (_armed && !_finished)
        {
            _supervisor.StopExpecting();
        }
    }
}
