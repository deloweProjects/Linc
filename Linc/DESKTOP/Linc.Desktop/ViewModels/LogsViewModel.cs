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
