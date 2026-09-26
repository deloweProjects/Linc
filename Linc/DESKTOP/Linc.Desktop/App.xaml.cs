using Linc.Desktop.Services;
using Linc.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Linc.Desktop;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>The single top-level Window, for pages that need a handle (file pickers etc.).</summary>
    public static Window MainWindowInstance { get; private set; } = null!;

    private Window? _window;

    // M12g Part A: null on every normal launch. Set once in the constructor so both the DI
    // factory below and OnLaunched's startup log (A3) agree on the same parse of the same
    // command line.
    private static readonly string? _parsedDataRoot = StartupRegistration.ParseDataRoot(Environment.GetCommandLineArgs());

    public App()
    {
        // M12b Part A: subscribe FIRST — before InitializeComponent(), before any service is
        // touched — so a crash anywhere below (including inside DI construction) is caught.
        // These three cover every unhandled-exception surface a WinUI desktop app has: a
        // synchronous throw on any thread (AppDomain), a faulted Task nobody awaited
        // (TaskScheduler), and one that escapes the XAML framework's own dispatch (Application).
        // tools\startupsim asserts by source text that all three are here, in this order,
        // above the ServiceCollection below — do not reorder this without checking that harness.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnXamlUnhandledException;

        InitializeComponent();
        // M15a A4: claim Linc's own ADB server port BEFORE the ServiceCollection below, because
        // the AdbClient fields on these singletons capture their endpoint the moment they are
        // constructed. Anything after this line would leave half of Linc on port 5037.
        AdbServerHost.UseIsolatedServerPort();
        Services = new ServiceCollection()
            .AddSingleton<ILogService, LogService>()
            .AddSingleton<IAdbServerHost, AdbServerHost>()
            // M12g Part A: --data-root <path> / --data-root=<path> redirects the store root for
            // a clean-room test launch. Every normal launch has no such switch, so _parsedDataRoot
            // is null and DeviceRegistry(null) resolves DefaultRootPath exactly as before —
            // production behaviour is unchanged.
            .AddSingleton<IDeviceRegistry>(_ => new DeviceRegistry(_parsedDataRoot))
            .AddSingleton<ITlsTransportService, TlsTransportService>()
            .AddSingleton<IConnectionManager, ConnectionManager>()
            .AddSingleton<IDiscoveryService, DiscoveryService>()
            .AddSingleton<IUsbWatcherService, UsbWatcherService>()
            .AddSingleton<IThemeSyncService, ThemeSyncService>()
            .AddSingleton<IPairingService, PairingService>()
            .AddSingleton<IBlePresenceService, BlePresenceService>()
            .AddSingleton<IConnectionSupervisor, ConnectionSupervisor>()
            .AddSingleton<FileService>()
            .AddSingleton<TlsFileService>()
            .AddSingleton<IFileService, FileServiceRouter>()
            .AddSingleton<IMirrorService, MirrorService>()
            .AddSingleton<IClipboardSyncService, ClipboardSyncService>()
            .AddSingleton<INotificationSyncService, NotificationSyncService>()
            .AddSingleton<IMediaSyncService, MediaSyncService>()
            .AddSingleton<IBatteryAlertService, BatteryAlertService>()
            .AddSingleton<ISyncEngine, SyncEngine>()
            .AddSingleton<IDeviceCacheService, DeviceCacheService>()
            // M12c-amend Part C: cross-cutting "is the onboarding wizard open" flag, read by
            // PhoneSetupService's install-confirmation gate without depending on any ViewModel.
            .AddSingleton<IOnboardingState, OnboardingState>()
            .AddSingleton<IPhoneSetupService, PhoneSetupService>()
            .AddSingleton<IDesktopModeService, DesktopModeService>()
            .AddSingleton<IDesktopLaunchService, DesktopLaunchService>()
            // M12e: HKCU Run key reader/writer for "start Linc when I sign in". Default subkey
            // path (real Run key) — production is unaffected; only tools\autostartsim passes a
            // throwaway one.
            .AddSingleton(sp => new StartupRegistration(log: sp.GetRequiredService<ILogService>()))
            // Per-app window geometry (M7b). Its root comes from the registry, never from
            // Environment.GetFolderPath — the D-057/D-058 rule every store under %LOCALAPPDATA%\Linc
            // now inherits. Registered before AppLaunchService because that is what consumes it.
            .AddSingleton(sp => new AppWindowStore(
                sp.GetRequiredService<IDeviceRegistry>().RootPath,
                sp.GetRequiredService<ILogService>()))
            // M9a SQLite foundation (D-044). Same root rule as AppWindowStore: the store takes its
            // root from the registry, never from Environment.GetFolderPath. Non-authoritative this
            // session — nothing reads from it yet; EnsureSchemaAsync + ImportFromRegistryAsync are
            // fired once at startup without blocking and without throwing (D-044 2.6).
            .AddSingleton(sp => new LincStore(
                sp.GetRequiredService<IDeviceRegistry>().RootPath,
                sp.GetRequiredService<ILogService>()))
            // M9c (§2.9): the offline outbox flush belongs on the shell, never a page's VM. So
            // OutboxService is a singleton started from AppShellViewModel's constructor alongside
            // clipboardSync / share / pcMedia — same reason: it must run regardless of the open page.
            .AddSingleton<OutboxService>()
            // M17b B2/B3, M19 C1: one shared checker, so the 6-hour gate and the in-flight flag are
            // process-wide rather than per-caller. Its inputs are delegates so the same class
            // links into tools\updatesim with no WinUI (GUIDE 4.1). The manifest ADDRESS is not a
            // delegate any more - it is UpdateChannel.ManifestUrl, baked in at build time.
            .AddSingleton(sp =>
            {
                var registry = sp.GetRequiredService<IDeviceRegistry>();
                var log = sp.GetRequiredService<ILogService>();
                return new UpdateCheckService(
                    updatesEnabled: () => registry.UpdatesEnabled,
                    installedVersion: () =>
                        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                    skippedVersion: () => registry.SkippedUpdateVersion,
                    http: new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
                    // This codebase's LogLevel has no Debug member (Info/Warn/Error only), so the
                    // task's "debug log line" lands at Info. It is never surfaced as an error, and
                    // with checking off the gate returns before any Log call is reached.
                    debugLog: message => log.Log(LogLevel.Info, message));
            })
            .AddSingleton<IAppLaunchService, AppLaunchService>()
            .AddSingleton<IPcMediaService, PcMediaService>()
            .AddSingleton<IPcInputService, PcInputService>()
            .AddSingleton<IPcMirrorService, PcMirrorService>()
            .AddSingleton<IPcControlService, PcControlService>()
            // v18 (M13b): the phone announces which port adbd is accepting on; the desktop
            // already knows where the phone is from its own control socket. HotspotConnector is
            // the adb disconnect/connect/verify sequence M13a built, consumed here for the
            // first time.
            .AddSingleton<IHotspotConnector, HotspotConnector>()
            // M13c §2.3: promotion to the fixed port 5555. An optimisation layered on top of the
            // announce path, never a replacement for it — it does not survive a phone reboot.
            .AddSingleton<IHotspotPromotion, HotspotPromotion>()
            .AddSingleton<IHotspotLinkService, HotspotLinkService>()
            .AddSingleton<IShareService, ShareService>()
            .AddSingleton<Linc.Desktop.QuickShare.IQuickShareService, Linc.Desktop.QuickShare.QuickShareService>()
            .AddSingleton<QuickShareViewModel>()
            .AddSingleton<HomeViewModel>()
            .AddSingleton<SyncViewModel>()
            .AddSingleton<FilesViewModel>()
            .AddSingleton<MirrorViewModel>()
            .AddSingleton<MirrorSettingsViewModel>()
            .AddSingleton<DesktopModeViewModel>()
            .AddSingleton<DesktopModeSettingsViewModel>()
            .AddSingleton<DisplayControlViewModel>()
            .AddSingleton<AppShellViewModel>()
            .AddSingleton<DeviceTabsViewModel>()
            .AddSingleton<DeviceViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<LogsViewModel>()
            .AddSingleton<DetailsViewModel>()
            // Transient: the onboarding wizard gets a fresh view model per open so re-runs start
            // clean and each tears down its own service subscriptions on close (M2b).
            .AddTransient<OnboardingViewModel>()
            .BuildServiceProvider();
    }

    // Held for the process lifetime; a second instance can't acquire it and exits.
    // Prevents two copies fighting over the Direct-TLS listener port (a real bug that
    // silently disabled direct connections when a stray instance held :46001).
    private static System.Threading.Mutex? _singleInstance;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // M12b Part A: on a clean machine with no debugger, an exception here used to kill the
        // process with no window, no dialog, no log (exactly what the owner hit on the second
        // PC). Now it is caught, logged beside the exe, surfaced via a plain Win32 MessageBox
        // (not a WinUI dialog — the framework may be the thing that failed), and the process
        // exits cleanly instead of hanging or vanishing.
        try
        {
            _singleInstance = new System.Threading.Mutex(initiallyOwned: true, @"Local\LincDesktopSingleInstance", out var isFirst);
            var quickSharePaths = QuickShare.QsShellIntegration.PathsFrom(Environment.GetCommandLineArgs());
            if (!isFirst)
            {
                // Explorer's "Send with Quick Share" starts a second copy; hand its files to the
                // running one, which opens its Share page with them ready to send.
                if (quickSharePaths.Count > 0)
                {
                    QuickShare.QsShellIntegration.TryForward(quickSharePaths);
                }
                Exit(); // another Linc is already running; let it own the tray and the port
                return;
            }
            try
            {
                // Toast buttons (Quick Share's Accept/Decline) arrive here. Must be subscribed
                // before Register(), or presses made while the app was starting are lost.
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.NotificationInvoked += (_, invoked) =>
                    Services.GetRequiredService<Linc.Desktop.QuickShare.IQuickShareService>()
                        .HandleNotificationArguments(invoked.Arguments);
                // Required once per process before toasts can be shown (unpackaged app).
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
            }
            catch (Exception)
            {
                // Toasts unavailable; the in-app notification center still works.
            }
            // M12g Part A3: a redirected data root must never be silent — a future session
            // debugging "my devices vanished" needs this line (same reasoning as D-057's guard).
            if (_parsedDataRoot is not null)
            {
                Services.GetRequiredService<ILogService>().Log(
                    LogLevel.Info, $"Store root redirected via --data-root: {_parsedDataRoot}");
            }
            _window = new MainWindow();
            MainWindowInstance = _window;
            // M9e: view models are constructed synchronously as part of Activate() — always call
            // it, even on a silent startup launch, or AppShellViewModel never starts and the app
            // sits in the tray connected to nothing. A startup launch just hides the window right
            // after, via the same AppWindow.Hide() the close-to-tray path already uses (see
            // MainWindow.OnAppWindowClosing) — no second hiding mechanism.
            _window.Activate();
            var commandLineArgs = Environment.GetCommandLineArgs();
            if (StartupRegistration.IsStartupLaunch(commandLineArgs) && quickSharePaths.Count == 0)
            {
                _window.AppWindow.Hide();
            }
            var quickShare = Services.GetRequiredService<QuickShare.IQuickShareService>();
            var window = (MainWindow)_window;
            QuickShare.QsShellIntegration.Listen(
                paths =>
                {
                    quickShare.QueueFiles(paths);
                    window.ShowSharePage();
                },
                message => Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, message),
                CancellationToken.None);
            if (quickSharePaths.Count > 0)
            {
                quickShare.QueueFiles(quickSharePaths);
                window.ShowSharePage();
            }

            // M13a (§3.3): pre-warm the ADB server. Starting it costs 200-500 ms, and every
            // caller of EnsureRunningAsync pays that on its FIRST connect — i.e. exactly when a
            // phone has just appeared and the link is being raced for. AdbServerHost already
            // no-ops after the first success, so this only moves the cost off the hot path.
            // Its own Task.Run with its own catch: a PC with no adb.exe must log one line and
            // carry on, not disturb the store/startup work below.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Services.GetRequiredService<IAdbServerHost>().EnsureRunningAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Services.GetRequiredService<ILogService>().Log(
                        LogLevel.Info, $"ADB pre-warm skipped: {ex.Message}");
                }
            });

            // M9a: ensure the SQLite store exists and import known devices once at startup.
            // Fire-and-forget on purpose — it must never block startup or throw into the launch path
            // (D-044 2.6). A corrupt or unreadable linc.db degrades to "no store" inside LincStore;
            // this wrapper only adds the catch-all so a transient failure cannot escape either.
            //
            // M9b: prune stored notifications past the retention window on startup (§2.3 / §3.5),
            // alongside the ensure/import and equally fire-and-forget. The retention window comes from
            // the registry (app-wide preference, D-045); a missing/null window is 30 days default.
            //
            // M12e (B5): reconcile the Run key here too. The app ships as a copied publish\ folder,
            // so its exe path changes whenever the owner moves or re-copies it, leaving a stale Run
            // entry pointing at nothing — fixed up on the next launch rather than left broken.
            _ = Task.Run(async () =>
            {
                try
                {
                    var store = Services.GetRequiredService<LincStore>();
                    var registry = Services.GetRequiredService<IDeviceRegistry>();
                    await store.EnsureSchemaAsync();
                    await store.ImportFromRegistryAsync(registry);
                    await store.PruneNotificationsAsync(registry.NotificationHistoryRetentionDays, serial: null);

                    if (registry.StartWithWindows)
                    {
                        var startup = Services.GetRequiredService<StartupRegistration>();
                        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
                        var desired = StartupRegistration.BuildRunCommand(exePath);
                        if (StartupRegistration.NeedsRewrite(startup.CurrentValue(), desired))
                        {
                            startup.Enable(exePath);
                            Services.GetRequiredService<ILogService>().Log(
                                LogLevel.Info, "StartupRegistration: rewrote a stale Run key entry to the current exe path.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Services.GetRequiredService<ILogService>().Log(
                        LogLevel.Error, $"LincStore: startup ensure/import failed; running without the store. ({ex.GetType().Name}: {ex.Message})");
                }
            });
        }
        catch (Exception ex)
        {
            HandleFatalStartupException(ex);
        }
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            StartupCrashLogger.AppendCrash(AppContext.BaseDirectory, ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupCrashLogger.AppendCrash(AppContext.BaseDirectory, e.Exception);
        e.SetObserved();
    }

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupCrashLogger.AppendCrash(AppContext.BaseDirectory, e.Exception);
    }

    /// <summary>
    /// A synchronous throw inside OnLaunched itself: log it, tell the person in plain language
    /// where the log is, then exit — never hang, never leave the process silently gone.
    /// </summary>
    private static void HandleFatalStartupException(Exception ex)
    {
        StartupCrashLogger.AppendCrash(AppContext.BaseDirectory, ex);
        var logPath = Path.Combine(AppContext.BaseDirectory, StartupCrashLogger.LogFileName);
        var message =
            "Linc couldn't start.\n\n" +
            $"Details were written to:\n{logPath}\n\n" +
            "Please send that file to the developer so this can be fixed.";
        MessageBoxW(IntPtr.Zero, message, "Linc", MB_ICONERROR | MB_OK);
        Environment.Exit(1);
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
