namespace Linc.Desktop.Services;

// M12g-amend Part A0: LogService.cs's constructor now depends on IDeviceRegistry.RootPath, but
// this harness only needs LogService.cs for the ILogService/LogLevel/LogEntry types (StartupRegistration
// takes an optional ILogService and this harness never constructs a LogService). Minimal stand-in
// so the file still compiles without pulling in the real DeviceRegistry.cs and its KnownDevice
// dependency chain (DesktopModeSettings.cs, MirrorSettings.cs, HomeLayout.cs).
public interface IDeviceRegistry
{
    string RootPath { get; }
}

// M12h: records every Log() call so the harness can assert Enable()/Disable() log on SUCCESS,
// not only on the existing failure path (the observability gap M12h closes).
public sealed class SpyLogService : ILogService
{
    private readonly List<LogEntry> _entries = [];
    public IReadOnlyList<LogEntry> Entries => _entries;
    public event Action? EntriesChanged;
    public string LogFolderPath => "";
    public void Log(LogLevel level, string message)
    {
        _entries.Add(new LogEntry(DateTimeOffset.UtcNow, level, message));
        EntriesChanged?.Invoke();
    }
    public void Clear() => _entries.Clear();
}
