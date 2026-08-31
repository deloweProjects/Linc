using System.IO;
using System.Text.Json;

namespace Linc.Desktop.Services;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message);

public interface ILogService
{
    IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>May fire on background threads.</summary>
    event Action? EntriesChanged;

    string LogFolderPath { get; }

    void Log(LogLevel level, string message);
    void Clear();
}

/// <summary>
/// A bounded in-memory activity log (connection/transfer/sync events — never raw
/// adb/scrcpy output or message contents) with best-effort persistence to a
/// rolling daily NDJSON file under %LOCALAPPDATA%\Linc/logs. File I/O failures
/// are swallowed the same way DeviceRegistry.Persist() does — logging must never
/// crash the app.
/// </summary>
public sealed class LogService : ILogService
{
    private const int MaxEntries = 500;

    private readonly object _lock = new();
    private readonly Queue<LogEntry> _entries = new();

    public event Action? EntriesChanged;

    public string LogFolderPath { get; }

    public LogService(IDeviceRegistry registry)
    {
        LogFolderPath = Path.Combine(registry.RootPath, "logs");
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }
        AppendToFile(entry);
        EntriesChanged?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        EntriesChanged?.Invoke();
    }

    private void AppendToFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogFolderPath);
            var path = Path.Combine(LogFolderPath, $"linc-{entry.Timestamp:yyyy-MM-dd}.ndjson");
            var json = JsonSerializer.Serialize(entry);
            File.AppendAllText(path, json + Environment.NewLine);
        }
        catch (IOException)
        {
            // Non-fatal: the in-memory log still has this entry for the current session.
        }
    }
}
