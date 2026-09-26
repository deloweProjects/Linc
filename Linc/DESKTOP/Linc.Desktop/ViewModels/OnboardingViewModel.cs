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

/// <summary>
/// The guided first-run front door (M2b). It doesn't reinvent connecting — it wraps the machinery
/// that already exists (PairingService for the QR/code, the supervisor's auto-connect, and
/// PhoneSetupService's install+grant+start that ConnectionManager already runs on every connect)
/// with plain-language, step-by-step copy and every ADB state translated.
///
/// Re-runnable: each run arms the supervisor for the next phone it sees, so a second run pairs an
/// <i>additional</i> device (it becomes another tab). A phone that already has the companion skips
/// straight through the install stage because EnsureReadyAsync no-ops in a few milliseconds.
///
/// Transient (one per wizard open); <see cref="Dispose"/> detaches every subscription so a closed
/// wizard leaves no listeners on the singleton services behind.
/// </summary>
public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDiscoveryService _discovery;
    private readonly IUsbWatcherService _usbWatcher;
    private readonly IPairingService _pairing;
    private readonly IPhoneSetupService _setup;
    private readonly IDeviceRegistry _registry;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;

    private PairingSession? _qrSession;
    private DiscoveredService? _pairingPhone;
    private bool _armed;
    private bool _disposed;

    public OnboardingViewModel(
        IConnectionSupervisor supervisor,
        IDiscoveryService discovery,
        IUsbWatcherService usbWatcher,
        IPairingService pairing,
        IPhoneSetupService setup,
        IDeviceRegistry registry,
        ILogService log)
    {
        _supervisor = supervisor;
        _discovery = discovery;
        _usbWatcher = usbWatcher;
        _pairing = pairing;
        _setup = setup;
        _registry = registry;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        PairingCode = "";
        PairingPhoneText = "";
        UsbStatusText = "";

        _supervisor.StateChanged += OnSupervisorStateChanged;
        _discovery.PairingServiceSeen += OnPairingServiceSeen;
        _usbWatcher.UsbDeviceSeen += OnUsbDeviceSeen;
    }

    // ---- Step state machine ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntro), nameof(IsEnableDebugging), nameof(IsDetectPair),
        nameof(IsInstall), nameof(IsDone), nameof(PrimaryText), nameof(ShowPrimary), nameof(ShowBack))]
    public partial OnboardingStep Step { get; set; } = OnboardingStep.Intro;

    public bool IsIntro => Step == OnboardingStep.Intro;
    public bool IsEnableDebugging => Step == OnboardingStep.EnableDebugging;
    public bool IsDetectPair => Step == OnboardingStep.DetectPair;
    public bool IsInstall => Step == OnboardingStep.Install;
    public bool IsDone => Step == OnboardingStep.Done;

    /// <summary>The dialog's own primary button; empty text hides it (detect/install just wait).</summary>
    public string PrimaryText => Step switch
    {
        OnboardingStep.Intro => "Get started",
        OnboardingStep.EnableDebugging => "My phone is ready",
        OnboardingStep.Done => "Finish",
        _ => "",
    };

    public bool ShowPrimary => PrimaryText.Length > 0;
    public bool ShowBack => Step is OnboardingStep.EnableDebugging or OnboardingStep.DetectPair;

    // ---- Bound state ----

    [ObservableProperty]
    public partial ImageSource? QrImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPairWithCode))]
    public partial string PairingCode { get; set; }

    [ObservableProperty]
    public partial string PairingPhoneText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsbStatus))]
    public partial string UsbStatusText { get; set; }

    public bool HasUsbStatus => UsbStatusText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanPairWithCode))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPairWithCode))]
    public partial bool IsBusy { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool CanPairWithCode => _pairingPhone is not null && PairingCode.Trim().Length >= 6 && !IsBusy;

    /// <summary>The model the wizard finished on, for the "You're all set" line.</summary>
    public string ConnectedModel => _supervisor.Device?.Model ?? _registry.PairedModel ?? "your phone";

    /// <summary>
    /// M15c A2: adding a phone from onboarding while one is already connected will disconnect it —
    /// said here, before the user picks up a cable, not after the first link vanishes.
    /// </summary>
    public string OneAtATimeText => DeviceAdmission.OneAtATimeNotice(
        _supervisor.State == LinkState.Connected ? _supervisor.Device?.Model ?? _supervisor.Device?.Serial : null) ?? "";

    public bool ShowOneAtATimeNote => OneAtATimeText.Length > 0;

    /// <summary>Warn up front when there's no APK to install (a desktop-only build).</summary>
    public bool ApkMissing => _setup.LocateApk() is null;

    /// <summary>Raised when the wizard should close (cancel or finish).</summary>
    public event Action? CloseRequested;

    // ---- Navigation commands ----

    [RelayCommand]
    private void Start() => Step = OnboardingFlow.Start(Step);

    [RelayCommand]
    private void Back() => Step = OnboardingFlow.Back(Step);

    /// <summary>
    /// EnableDebugging → DetectPair: arm the supervisor for the next phone it sees and show the QR.
    /// Idempotent, so re-entering the step (Back then forward) doesn't double-arm.
    /// </summary>
    [RelayCommand]
    private void BeginDetection()
    {
        Arm();
        Step = OnboardingFlow.BeginDetection(Step);
    }

    private void Arm()
    {
        if (_armed)
        {
            return;
        }
        _armed = true;
        ErrorMessage = null;
        _log.Log(LogLevel.Info, "Onboarding: watching for a phone to connect (USB or wireless pairing).");
        _qrSession = _pairing.CreateQrSession();
        QrImage = CreateQrImage(_qrSession.QrText);
        _pairingPhone = null;
        PairingPhoneText = "Waiting for your phone…";
        // Trust the next phone that appears (USB or the just-paired wireless one) instead of
        // popping the "unknown USB device" confirmation card the Device page shows.
        _supervisor.ExpectNextDevice();
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
    private void Finish() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ---- Detection / pairing (reuses the Device-page machinery) ----

    private void OnUsbDeviceSeen(DeviceData device)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || Step is OnboardingStep.Intro)
            {
                return; // don't yank the user out of the welcome screen
            }
            // A phone is visible over USB, so debugging is clearly already on: skip the "turn it on"
            // instructions and move to the detect screen (step b's "detect if it's already on and
            // skip ahead"). We never jump to "You're all set" from here — success only comes from a
            // real connection transition the supervisor reports (OnSupervisorStateChanged), so an
            // ALREADY-connected phone can't make the wizard falsely declare a fresh pairing done.
            if (Step is OnboardingStep.EnableDebugging)
            {
                BeginDetection();
            }
            if (Step is OnboardingStep.DetectPair && _supervisor.State != LinkState.Connected)
            {
                // A phone this PC has connected before is a returning phone, not a new one: say
                // so, so nobody thinks Linc is about to add a second copy of it.
                UsbStatusText = DeviceAdmission.RecogniseKnown(device.Serial, _registry.KnownDevices.Select(d => d.Serial)) is { } known
                    ? $"Welcome back — {_registry.KnownDevices.First(d => d.Serial == known).Model} is already known to this PC. Reconnecting it (no new pairing needed)…"
                    : "Phone detected over USB — connecting…";
            }
        });
    }

    private void OnPairingServiceSeen(DiscoveredService service)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !_armed || IsBusy)
            {
                return;
            }
            _pairingPhone = service;
            PairingPhoneText = $"Phone found at {service.IpAddress}:{service.Port}.";
            OnPropertyChanged(nameof(CanPairWithCode));

            // QR flow: the phone advertises the instance name from the QR we showed, so pairing
            // completes hands-free with the QR's password.
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
            PairingPhoneText = "Paired. Connecting to your phone…";
            _supervisor.ExpectNextDevice(); // adopt the first advert this phone now sends
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
        if (_disposed || !_armed)
        {
            return;
        }
        OnPropertyChanged(nameof(OneAtATimeText));
        OnPropertyChanged(nameof(ShowOneAtATimeNote));
        switch (_supervisor.State)
        {
            case LinkState.Connecting when Step is OnboardingStep.DetectPair:
                // The ADB link is up; ConnectionManager is now installing/granting/starting the
                // companion inside this connect (M01). Show that as its own reassuring stage.
                UsbStatusText = "";
                Step = OnboardingFlow.Connecting(Step);
                break;
            case LinkState.Connected:
                MoveToDone();
                break;
        }
    });

    // MoveToDone() routes through OnboardingFlow.Connected too, keeping every transition in one place.

    private void MoveToDone()
    {
        if (Step == OnboardingStep.Done)
        {
            return;
        }
        ErrorMessage = null;
        OnPropertyChanged(nameof(ConnectedModel));
        Step = OnboardingFlow.Connected(Step);
        _log.Log(LogLevel.Info, $"Onboarding complete: {ConnectedModel} is connected and saved.");
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _discovery.PairingServiceSeen -= OnPairingServiceSeen;
        _usbWatcher.UsbDeviceSeen -= OnUsbDeviceSeen;
    }
}
