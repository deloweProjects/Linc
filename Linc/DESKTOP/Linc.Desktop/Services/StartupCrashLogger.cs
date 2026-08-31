using System.IO;

namespace Linc.Desktop.Services;

/// <summary>
/// Last-resort crash logging for failures that happen before <see cref="LogService"/> (or any
/// other DI-built service) exists — an unhandled exception in <c>App</c>'s constructor or
/// <c>OnLaunched</c>. Writes next to the executable, not %LOCALAPPDATA%, because a confused
/// beta tester on a machine with no debugger will never find the latter (M12b Part A).
///
/// Deliberately has no WinUI/WindowsAppSDK dependency so tools\startupsim can compile this file
/// directly and call <see cref="FormatLogLine"/>/<see cref="AppendCrash"/> for real, rather than
/// re-implementing the format as a model of it (see GUIDE.md §4.1).
/// </summary>
public static class StartupCrashLogger
{
    public const string LogFileName = "linc-startup-error.log";

    /// <summary>
    /// Pure formatter: timestamp + exception type + message + stack trace as one appendable
    /// log block. No I/O, no secrets — callers must not pass clipboard/notification content.
    /// </summary>
    public static string FormatLogLine(DateTimeOffset timestamp, string exceptionType, string message, string? stackTrace)
    {
        return $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] {exceptionType}: {message}"
            + Environment.NewLine
            + (stackTrace ?? "(no stack trace)")
            + Environment.NewLine
            + new string('-', 60)
            + Environment.NewLine;
    }

    /// <summary>Appends one formatted crash block to <see cref="LogFileName"/> under <paramref name="logDirectory"/>.</summary>
    public static void AppendCrash(string logDirectory, Exception ex)
    {
        var line = FormatLogLine(DateTimeOffset.Now, ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ex.StackTrace);
        var path = Path.Combine(logDirectory, LogFileName);
        try
        {
            File.AppendAllText(path, line);
        }
        catch
        {
            // The process is already dying from an unhandled exception; a failed log write
            // must never throw a second exception on top of the first.
        }
    }
}
