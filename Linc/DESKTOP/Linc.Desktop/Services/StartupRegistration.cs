using System.IO;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

/// <summary>
/// Reads and writes the HKCU Run key that makes Linc start when the user signs in (M12e Part B).
/// Per-user, no admin, works for an unpackaged app — the mechanism Windows Settings → Startup apps
/// and Task Manager both surface, so the user can always turn it off outside our UI.
///
/// Deliberately does not reference <see cref="DeviceRegistry"/> in either direction (D-036/D-057
/// harness decoupling — see M12e task B3): <see cref="DeviceRegistry"/> stores the
/// StartWithWindows preference, this class writes the registry, and the Settings view model is
/// the only thing that calls both. The optional <see cref="ILogService"/> dependency is the same
/// kind tools\devicesim already compiles in alongside DeviceRegistry.cs, so a harness can still
/// link this file with at most one small extra file, and it defaults to null so a harness that
/// only wants the registry round-trip never touches the real app log.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunKeySubPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Linc";

    /// <summary>
    /// The Run value: the exe path wrapped in double quotes, followed by a space and
    /// <c>--startup</c>. The quotes are load-bearing — this app ships as a copied folder that may
    /// land anywhere, including a path with spaces, and an unquoted path with a space is silently
    /// ignored by Windows at sign-in.
    /// </summary>
    public static string BuildRunCommand(string exePath) => $"\"{exePath}\" --startup";

    /// <summary>Ordinal, case-insensitive: true when the stored value differs from the desired one or is missing.</summary>
    public static bool NeedsRewrite(string? existing, string desired) =>
        string.IsNullOrEmpty(existing) || !string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when any argument equals <c>--startup</c>, case-insensitively.</summary>
    public static bool IsStartupLaunch(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));

    private const string DataRootSwitch = "--data-root";
    private const string DataRootPrefix = "--data-root=";

    /// <summary>
    /// The diagnostic <c>--data-root &lt;path&gt;</c> / <c>--data-root=&lt;path&gt;</c> switch
    /// (M12g Part A): lets an out-of-process launch redirect <see cref="DeviceRegistry"/>'s store
    /// root, for a clean-room test launch that must never touch the owner's real
    /// %LOCALAPPDATA%\Linc/. Pure and static so a harness can test it without launching anything.
    /// Returns null in every normal launch — production passes no root, so behaviour is
    /// byte-for-byte unchanged. A malformed switch (missing/empty/whitespace value) also returns
    /// null rather than a half-parsed path, and the result is always resolved to an absolute path
    /// (<see cref="Path.GetFullPath(string)"/>) so the store never scatters relative to whatever
    /// directory happened to be current at launch.
    /// </summary>
    public static string? ParseDataRoot(IEnumerable<string> args)
    {
        var list = args.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            var arg = list[i];
            if (arg.StartsWith(DataRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveOrNull(arg[DataRootPrefix.Length..]);
            }
            if (string.Equals(arg, DataRootSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveOrNull(i + 1 < list.Length ? list[i + 1] : null);
            }
        }
        return null;
    }

    private static string? ResolveOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

    private readonly string _subKeyPath;
    private readonly ILogService? _log;

    /// <param name="subKeyPath">
    /// The Run key's subkey path under HKEY_CURRENT_USER. Defaults to <see cref="RunKeySubPath"/>,
    /// the D-057 pattern: production passes nothing, a harness passes a throwaway subkey and can
    /// therefore never touch the real Run key.
    /// </param>
    /// <param name="log">Optional; when supplied, a failed registry operation is logged at Error instead of thrown.</param>
    public StartupRegistration(string subKeyPath = RunKeySubPath, ILogService? log = null)
    {
        _subKeyPath = subKeyPath;
        _log = log;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            _log?.Log(LogLevel.Error, $"StartupRegistration: IsEnabled failed. ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    /// <summary>The raw stored Run value, or null if missing/unreadable — what <see cref="NeedsRewrite"/> compares against on every launch (App.OnLaunched reconcile, M12e B5).</summary>
    public string? CurrentValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
            return key?.GetValue(RunValueName) as string;
        }
        catch (Exception ex)
        {
            _log?.Log(LogLevel.Error, $"StartupRegistration: CurrentValue failed. ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public void Enable(string exePath)
    {
        try
        {
            var command = BuildRunCommand(exePath);
            using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath);
            key.SetValue(RunValueName, command);
            _log?.Log(LogLevel.Info, $"StartupRegistration: Enable succeeded. Wrote \"{RunValueName}\"=\"{command}\" under HKCU\\{_subKeyPath}.");
        }
        catch (Exception ex)
        {
            _log?.Log(LogLevel.Error, $"StartupRegistration: Enable failed. ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            _log?.Log(LogLevel.Info, $"StartupRegistration: Disable succeeded. Removed \"{RunValueName}\" under HKCU\\{_subKeyPath}.");
        }
        catch (Exception ex)
        {
            _log?.Log(LogLevel.Error, $"StartupRegistration: Disable failed. ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
