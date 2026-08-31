using System.IO;
using AdvancedSharpAdbClient;

namespace Linc.Desktop.Services;

public interface IAdbServerHost
{
    /// <summary>Ensures the ADB server is running, starting the bundled/installed one if needed.</summary>
    Task EnsureRunningAsync(CancellationToken ct);

    /// <summary>Path to the adb executable Linc is using, or null if none was found.</summary>
    string? AdbPath { get; }

    /// <summary>Restarts the ADB server — a safe, bounded recovery action for the Settings page.</summary>
    Task RestartAsync(CancellationToken ct);
}

/// <summary>
/// Owns the ADB server lifecycle so the user never touches a terminal.
/// Searches, in order: an adb/ folder next to the app (the bundled location used by
/// packaged builds, M10), ANDROID_HOME, and the default Android SDK install path.
/// </summary>
public sealed class AdbServerHost : IAdbServerHost
{
    /// <summary>
    /// The port Linc's own ADB server listens on, away from the default 5037 that Android Studio,
    /// scrcpy installs and every other phone tool on the machine share. M13 asked for this and it
    /// was never built: on the shared port an unrelated tool's `adb kill-server` — or its server
    /// deciding to replace ours — takes Linc's link down mid-session, and can strand a pending
    /// USB trust handshake (M15a A4).
    /// </summary>
    public const int PrivateServerPort = 5038;

    /// <summary>The environment variable both adb.exe and AdvancedSharpAdbClient read.</summary>
    private const string ServerPortVariable = "ANDROID_ADB_SERVER_PORT";

    private bool _started;

    /// <summary>
    /// Points every piece of ADB plumbing in this process at <see cref="PrivateServerPort"/>. It
    /// is one call because one process-wide environment variable is genuinely all of it, measured
    /// rather than assumed (M15a A4): AdvancedSharpAdbClient reads
    /// <c>ANDROID_ADB_SERVER_PORT</c> when it builds <c>AdbClient.EndPoint</c>, and every adb.exe
    /// and scrcpy Linc spawns inherits this process's environment and reads the same variable.
    /// <para>
    /// Must run before the first <c>AdbClient</c> is constructed — those are readonly fields on
    /// long-lived singletons, so the endpoint is captured at construction. App's constructor calls
    /// it above the ServiceCollection for exactly that reason.
    /// </para>
    /// </summary>
    public static void UseIsolatedServerPort()
    {
        Environment.SetEnvironmentVariable(ServerPortVariable, PrivateServerPort.ToString());
    }

    public string? AdbPath => ToolLocator.FindAdb();

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }
        var adbPath = AdbPath
            ?? throw new LincException(
                $"Linc couldn't find its ADB engine on this PC. It looked for adb.exe bundled at " +
                $"{Path.Combine(AppContext.BaseDirectory, "SCRCPY", "Linc.scrcpy", "bin", "adb.exe")}, " +
                "then in the Android SDK (the ANDROID_HOME environment variable, and " +
                @"%LOCALAPPDATA%\Android\Sdk\platform-tools). Reinstall Linc so the bundled copy is " +
                "restored, or install Android platform-tools and set ANDROID_HOME.");
        try
        {
            await AdbServer.Instance.StartServerAsync(adbPath, restartServerIfNewer: false, ct);
        }
        catch (Exception ex) when (ex is not LincException and not OperationCanceledException)
        {
            throw new LincException(
                "Linc couldn't start its ADB engine. Another program (like an Android emulator " +
                "or another phone tool) may be blocking it — close it and try again.", ex);
        }
        _started = true;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        var adbPath = AdbPath
            ?? throw new LincException("Linc couldn't find its ADB engine on this PC.");
        try
        {
            await AdbServer.Instance.RestartServerAsync(adbPath, ct);
        }
        catch (Exception ex) when (ex is not LincException and not OperationCanceledException)
        {
            throw new LincException("Linc couldn't restart its ADB engine.", ex);
        }
        _started = true;
    }
}
