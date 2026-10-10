using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public sealed class LogEntryVm(LogEntry entry)
{
    public string TimeText => entry.Timestamp.LocalDateTime.ToString("HH:mm:ss");
    public string Message => entry.Message;
    public LogLevel Level => entry.Level;
}

public partial class LogsViewModel : ObservableObject
{
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<LogEntryVm> Entries { get; } = [];

    public LogsViewModel(ILogService log)
    {
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _log.EntriesChanged += () => _dispatcher.TryEnqueue(Rebuild);
        Rebuild();
    }

    [RelayCommand]
    private void Clear() => _log.Clear();

    /// <summary>
    /// A plain-text report someone else can read: this PC and Linc's version, then the last two
    /// days of the on-disk log (it outlives restarts, unlike the list on screen).
    /// </summary>
    [RelayCommand]
    private void CopyLog()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Linc PC log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"App: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        text.AppendLine($"Windows: {Environment.OSVersion.VersionString}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        text.AppendLine();
        bool fromDisk = false;
        foreach (var day in new[] { DateTime.Now.AddDays(-1), DateTime.Now })
        {
            var path = Path.Combine(_log.LogFolderPath, $"linc-{day:yyyy-MM-dd}.ndjson");
            if (!File.Exists(path)) continue;
            fromDisk = true;
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                    var level = (int?)node?["Level"] ?? 0;
                    text.AppendLine($"{(string?)node?["Timestamp"]} {((LogLevel)level).ToString().ToUpperInvariant(),-5} {(string?)node?["Message"]}");
                }
                catch (System.Text.Json.JsonException) { text.AppendLine(line); }
            }
        }
        if (!fromDisk) foreach (var e in _log.Entries) text.AppendLine($"{e.Timestamp:O} {e.Level} {e.Message}");
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        _log.Log(LogLevel.Info, "Log copied to the clipboard");
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_log.LogFolderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.LogFolderPath}\"") { UseShellExecute = true });
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var entry in _log.Entries.Reverse())
        {
            Entries.Add(new LogEntryVm(entry));
        }
    }
}
