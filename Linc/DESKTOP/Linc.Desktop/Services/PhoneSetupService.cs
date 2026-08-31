using System.IO;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public interface IPhoneSetupService
{
    /// <summary>True when the companion package is present on the device.</summary>
    Task<bool> IsInstalledAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Installs the companion if missing and grants everything ADB is allowed to grant — but
    /// only when <see cref="CompanionInstallGate"/> says to install outright (already installed,
    /// or the onboarding wizard is driving this connect). On an ordinary, non-wizard connect to
    /// a phone that doesn't have the companion, this asks first instead
    /// (<see cref="InstallConfirmationNeeded"/>) and returns false without installing —
    /// M12c-amend C2.1/C2.5: the connection is never blocked on the answer.
    /// Returns true when something was actually set up.
    /// </summary>
    Task<bool> EnsureReadyAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Raised (off the UI thread) when a non-wizard connect found the companion missing and is
    /// asking before installing it (M12c-amend C2.1). The subscriber owns showing the
    /// confirmation and must call <see cref="ConfirmInstallAsync"/> or <see cref="DeclineInstall"/>.
    /// </summary>
    event Action<DeviceData>? InstallConfirmationNeeded;

    /// <summary>The user said yes to the <see cref="InstallConfirmationNeeded"/> prompt — installs now.</summary>
    Task<bool> ConfirmInstallAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// The user said no. Remembered in memory only, for this phone, for the rest of the process
    /// (M12c-amend C2.3) — never persisted to disk, and never asked twice in the same run.
    /// </summary>
    void DeclineInstall(DeviceData device);

    /// <summary>Re-applies every grant (for the Settings "fix permissions" action).</summary>
    Task GrantAllAsync(DeviceData device, CancellationToken ct);

    /// <summary>
    /// Whether the last grant pass left the companion able to arm wireless debugging by itself
    /// (M13c §2.1). Null until onboarding has run against a phone in this process.
    /// </summary>
    bool? SelfArmGranted { get; }

    /// <summary>Where the companion APK was found, or null when it isn't available.</summary>
    string? LocateApk();

    /// <summary>
    /// Installs an arbitrary local .apk (M10 Part A) — not the bundled companion. Reuses the
    /// same <see cref="AdbClient.InstallAsync"/> primitive as <see cref="EnsureReadyAsync"/>.
    /// Returns the raw success/failure so the caller can translate it into plain language
    /// (A2.5) without this service knowing about UI wording; the raw string is never itself
    /// shown to the user. <paramref name="progress"/> reports upload/install sub-states so the
    /// UI can show honest staged progress (A2.4).
    /// </summary>
    Task<(bool Success, string? RawError)> InstallApkAsync(
        DeviceData device, string apkPath, Action<InstallProgressEventArgs>? progress, CancellationToken ct);
}

/// <summary>
/// PC-driven onboarding (M01, D-035): the desktop installs the companion and grants its
/// permissions over ADB, so the user's phone-side work is developer options plus a QR scan.
///
/// Every grant here was verified against the Pixel 7 — the notification listener via
/// `cmd notification allow_listener`, the dangerous runtime permissions via `pm grant`, and
/// All-files access via `appops set`. None of them require a tap. What ADB genuinely cannot
/// do (enabling wireless debugging, the pairing itself) stays with the user.
/// </summary>
public sealed class PhoneSetupService(ILogService log, IOnboardingState onboarding) : IPhoneSetupService
{
    private readonly AdbClient _adb = new();

    // M12c-amend C2.3: in-memory only, for this process's lifetime — never persisted to disk,
    // and never re-asked for a phone that already got an answer this session.
    private readonly HashSet<string> _declinedThisSession = new(StringComparer.Ordinal);
    private readonly HashSet<string> _awaitingAnswer = new(StringComparer.Ordinal);

    public event Action<DeviceData>? InstallConfirmationNeeded;

    private const string ListenerComponent =
        ProtocolConstants.CompanionPackage + "/" + ProtocolConstants.CompanionPackage + ".service.LincNotificationListener";

    // Dangerous runtime permissions the companion asks for. Granting is idempotent, and a
    // permission the build doesn't declare simply reports an error we ignore.
    private static readonly string[] RuntimePermissions =
    [
        "android.permission.POST_NOTIFICATIONS",
        "android.permission.READ_SMS",
        "android.permission.SEND_SMS",
        "android.permission.RECEIVE_SMS",
        "android.permission.READ_CALL_LOG",
        "android.permission.CALL_PHONE",
        "android.permission.READ_PHONE_STATE",
        "android.permission.ANSWER_PHONE_CALLS",
        // Advertise-only, for the BLE presence beacon (M04, D-034) — no scanning, so no
        // location permission is involved and nothing is ever read over Bluetooth.
        "android.permission.BLUETOOTH_ADVERTISE",
    ];

    public async Task<bool> IsInstalledAsync(DeviceData device, CancellationToken ct)
    {
        var output = await ShellAsync(device, $"pm list packages {ProtocolConstants.CompanionPackage}", ct);
        return output.Contains(ProtocolConstants.CompanionPackage, StringComparison.Ordinal);
    }

    public async Task<bool> EnsureReadyAsync(DeviceData device, CancellationToken ct)
    {
        var installed = await IsInstalledAsync(device, ct);
        var declined = device.Serial is not null && _declinedThisSession.Contains(device.Serial);
        var decision = CompanionInstallGate.Decide(installed, onboarding.IsActive, declined);

        switch (decision)
        {
            case CompanionInstallDecision.Skip:
                return false; // already set up, or already declined this session
            case CompanionInstallDecision.Ask:
                // M12c-amend C2.5: never block the connect on the answer — fire the request and
                // let the connection carry on without the companion installed. Dedup so a burst
                // of reconnect attempts for the same phone (discovery re-adverts, USB re-polls)
                // can't stack multiple prompts before the first is answered.
                if (device.Serial is not null && _awaitingAnswer.Add(device.Serial))
                {
                    log.Log(LogLevel.Info, "Companion install offered.");
                    InstallConfirmationNeeded?.Invoke(device);
                }
                return false;
            default: // InstallSilently — the wizard is already the user's explicit consent (C2.4)
                return await InstallCompanionAsync(device, ct);
        }
    }

    public async Task<bool> ConfirmInstallAsync(DeviceData device, CancellationToken ct)
    {
        if (device.Serial is not null)
        {
            _awaitingAnswer.Remove(device.Serial);
        }
        log.Log(LogLevel.Info, "Companion install accepted.");
        return await InstallCompanionAsync(device, ct);
    }

    public void DeclineInstall(DeviceData device)
    {
        if (device.Serial is not null)
        {
            _awaitingAnswer.Remove(device.Serial);
            _declinedThisSession.Add(device.Serial);
        }
        log.Log(LogLevel.Info, "Companion install declined.");
    }

    private async Task<bool> InstallCompanionAsync(DeviceData device, CancellationToken ct)
    {
        var apk = LocateApk();
        if (apk is null)
        {
            log.Log(LogLevel.Warn, "The Linc app isn't on the phone and no copy was found to install.");
            return false;
        }

        log.Log(LogLevel.Info, "Installing the Linc app on your phone…");
        try
        {
            await using var stream = File.OpenRead(apk);
            // Third argument is a progress callback, not a token (AdvancedSharpAdbClient's
            // signatures drift from intuition — see BRAIN.md).
            await _adb.InstallAsync(device, stream, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"Couldn't install the Linc app on the phone: {ex.Message}");
            return false;
        }

        await GrantAllAsync(device, ct);
        log.Log(LogLevel.Info, "Installed the Linc app on your phone and set up its permissions.");
        return true;
    }

    /// <summary>
    /// Runs the whole post-install grant plan. A failed grant is logged and skipped, never
    /// rethrown: onboarding has to complete on a phone whose OEM blocks one of these
    /// (M13c §2.1). The plan and the never-stop-early loop live in
    /// <see cref="PhoneSetupCommands"/> so both are provable without a phone.
    /// </summary>
    public async Task GrantAllAsync(DeviceData device, CancellationToken ct)
    {
        var plan = PhoneSetupCommands.GrantPlan(
            ProtocolConstants.CompanionPackage, ListenerComponent, RuntimePermissions);

        var failed = await PhoneSetupCommands.RunPlanAsync(
            plan,
            (command, token) => ShellAsync(device, command, token),
            (command, ex) => log.Log(LogLevel.Info, $"A phone setup step didn't apply ({command}): {ex.Message}"),
            ct);

        // M13c §2.1: surface the self-arm capability honestly rather than silently degrading.
        SelfArmGranted = !failed.Any(PhoneSetupCommands.IsSelfArmGrant);
        if (SelfArmGranted != true)
        {
            log.Log(LogLevel.Info, PhoneSetupCommands.SelfArmUnavailableReason);
        }
    }

    /// <summary>
    /// Whether the last grant pass left the companion able to arm wireless debugging itself
    /// (M13c §2.1). Null until onboarding has run against a phone in this process.
    /// <para>
    /// `pm grant` reports a refusal by writing to stderr rather than by failing, so a phone whose
    /// OEM blocks WRITE_SECURE_SETTINGS can still leave this true. It is an optimistic signal for
    /// the UI to explain a missing feature with, not a proof the permission is held — the phone
    /// checks that for real before it writes anything.
    /// </para>
    /// </summary>
    public bool? SelfArmGranted { get; private set; }

    /// <summary>
    /// The bundled APK beside the executable, falling back to this repo's build output during
    /// development. The path logic lives in <see cref="ApkResolver"/> so it can be unit-checked
    /// without WinUI (tools/apkprobe); this is a side-effect-free lookup — EnsureReadyAsync logs
    /// the plain-language "no copy was found" line when it comes back null.
    /// </summary>
    public string? LocateApk() => ApkResolver.Resolve(AppContext.BaseDirectory, File.Exists);

    public async Task<(bool Success, string? RawError)> InstallApkAsync(
        DeviceData device, string apkPath, Action<InstallProgressEventArgs>? progress, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(apkPath);
            await _adb.InstallAsync(device, stream, progress, ct);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Raw adb text is logged here only — the UI only ever sees TranslateInstallFailure's
            // plain-language mapping of it (A2.5).
            log.Log(LogLevel.Warn, $"Install failed for {apkPath}: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private async Task<string> ShellAsync(DeviceData device, string command, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(command, device, receiver, ct);
            return receiver.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"error: {ex.Message}";
        }
    }
}
