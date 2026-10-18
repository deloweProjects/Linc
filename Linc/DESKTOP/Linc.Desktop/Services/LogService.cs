using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private readonly LogLimiter _limiter = new();

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
        // D-081: no line more than three times a minute — a reconnect loop or a notification
        // burst used to write the same sentence 80-229 times a minute and bury everything else.
        if (_limiter.Admit(message, DateTime.UtcNow) is not { } admitted)
        {
            return;
        }
        var entry = new LogEntry(DateTimeOffset.Now, level, admitted);
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

/// <summary>
/// Keeps repeating lines out of the log (D-081): a message — with its numbers normalised, so
/// "probe 1/3" and "probe 2/3" are one message — is written at most <see cref="PerMinute"/> times a
/// minute. The first suppressed one is replaced by a single "repeating" note; when the minute is
/// over, the next line says how many were held back.
/// </summary>
public sealed class LogLimiter
{
    public const int PerMinute = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    private readonly Dictionary<string, (DateTime WindowStart, int Count)> _keys = [];
    private readonly object _lock = new();

    /// <summary>
    /// What to write for <paramref name="message"/> now: the message itself, the message with a
    /// note about how many were held back, or null to write nothing.
    /// </summary>
    public string? Admit(string message, DateTime nowUtc)
    {
        var key = Digits.Replace(message, "#");
        lock (_lock)
        {
            if (!_keys.TryGetValue(key, out var entry) || nowUtc - entry.WindowStart >= Window)
            {
                var held = entry.Count > PerMinute && nowUtc - entry.WindowStart < Window * 2
                    ? entry.Count - PerMinute
                    : 0;
                _keys[key] = (nowUtc, 1);
                if (_keys.Count > 512)
                {
                    foreach (var stale in _keys.Where(k => nowUtc - k.Value.WindowStart > Window * 2).Select(k => k.Key).ToList())
                    {
                        _keys.Remove(stale);
                    }
                }
                return held > 0 ? $"{message} (held back {held} similar line(s) in the last minute)" : message;
            }
            var count = entry.Count + 1;
            _keys[key] = (entry.WindowStart, count);
            if (count <= PerMinute)
            {
                return message;
            }
            return null;
        }
    }
}
