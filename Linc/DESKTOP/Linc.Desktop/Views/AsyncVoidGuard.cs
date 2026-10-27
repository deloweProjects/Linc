using Linc.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Linc.Desktop.Views;

/// <summary>
/// The catch every <c>async void</c> event handler ends in (TIDY-UP B10). An exception that leaves
/// an async void reaches App's UnhandledException, which writes the crash file but lets WinUI close
/// Linc: a failed button press or drop should cost one line in Settings › Logs, not the app.
/// </summary>
internal static class AsyncVoidGuard
{
    public static void Report(Exception ex, string what)
    {
        try
        {
            App.Services?.GetService<ILogService>()?.Log(LogLevel.Warn, $"{what} failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Logging must never be the thing that crashes.
        }
    }
}
