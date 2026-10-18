using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>One history row on the Clipboard page.</summary>
public sealed partial class ClipRowVm(ClipEntry entry, ClipboardViewModel owner)
{
    public ClipEntry Entry { get; } = entry;
    public string Text => Entry.Text;
    public string Glyph => Entry.FromPhone ? "" : ""; // phone / PC
    public string SourceName => Entry.FromPhone ? "From the phone" : "From this PC";
    public string Caption => $"{(Entry.FromPhone ? "Phone" : "This PC")} · {Entry.At.LocalDateTime:t}{(Entry.Sent ? " · Sent" : "")}";
    public bool IsPinned => Entry.Pinned;
    public string PinLabel => Entry.Pinned ? "Unpin" : "Pin";
    public string PinGlyph => Entry.Pinned ? "" : "";

    [RelayCommand] private void Copy() => owner.CopyHere(Entry);
    [RelayCommand] private Task SendAsync() => owner.SendItemAsync(Entry);
    [RelayCommand] private void Pin() => owner.TogglePin(Entry);
    [RelayCommand] private void Delete() => owner.DeleteItem(Entry);
}

/// <summary>A day (or "Pinned") and its rows.</summary>
public sealed class ClipGroupVm(string header, IReadOnlyList<ClipRowVm> items)
{
    public string Header { get; } = header;
    public IReadOnlyList<ClipRowVm> Items { get; } = items;
}

/// <summary>
/// The Clipboard page (1.0.0.11, D-083): send text to the phone's clipboard, Sync now, and the
/// searchable history grouped by day with pinned items first.
/// </summary>
public sealed partial class ClipboardViewModel : ObservableObject
{
    private readonly IClipboardSyncService _clipboard;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ClipGroupVm> Groups { get; } = [];

    public ClipboardViewModel(IClipboardSyncService clipboard, IConnectionSupervisor supervisor, IDeviceRegistry registry)
    {
        _clipboard = clipboard;
        _supervisor = supervisor;
        _registry = registry;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Draft = "";
        Query = "";
        Message = "";
        clipboard.HistoryChanged += () => _dispatcher.TryEnqueue(Rebuild);
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsNotConnected));
            OnPropertyChanged(nameof(CanSend));
        });
        Rebuild();
    }

    [ObservableProperty]
    public partial string Draft { get; set; }

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));

    [ObservableProperty]
    public partial string Query { get; set; }

    partial void OnQueryChanged(string value) => Rebuild();

    [ObservableProperty]
    public partial string Message { get; set; }

    [ObservableProperty]
    public partial bool HasMessage { get; set; }

    public bool IsConnected => _supervisor.State == LinkState.Connected;
    public bool IsNotConnected => !IsConnected;
    public bool CanSend => IsConnected && Draft.Trim().Length > 0;
    public bool KeepHistory => _registry.ClipboardHistoryEnabled;
    public bool HistoryOff => !KeepHistory;
    public bool HasHistory => _clipboard.History.Count > 0;
    public bool IsEmpty => Groups.Count == 0;

    public string EmptyTitle => HasHistory ? "No matches" : "Nothing copied yet";

    public string EmptyHint => HasHistory
        ? $"Nothing in the history contains \"{Query.Trim()}\"."
        : "Text you copy on either device shows up here.";

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (await _clipboard.SendToPhoneAsync(text))
        {
            Draft = "";
            Say("Sent — it's on your phone's clipboard.");
        }
        else
        {
            Say("Your phone isn't connected.");
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync() =>
        Say(await _clipboard.SyncNowAsync()
            ? "Synced. The phone sends its clipboard back only while Linc is open on it."
            : "Your phone isn't connected.");

    public void CopyHere(ClipEntry entry)
    {
        _clipboard.Recopy(entry);
        Say("Copied to this PC's clipboard.");
    }

    public async Task SendItemAsync(ClipEntry entry) =>
        Say(await _clipboard.SendToPhoneAsync(entry.Text) ? "Sent to your phone's clipboard." : "Your phone isn't connected.");

    public void TogglePin(ClipEntry entry) => _clipboard.SetPinned(entry, !entry.Pinned);

    public void DeleteItem(ClipEntry entry) => _clipboard.Delete(entry);

    /// <summary>Called by the page after the user confirmed.</summary>
    public void ClearHistory()
    {
        _clipboard.Clear();
        Say("History cleared. Pinned items were kept.");
    }

    private void Say(string message)
    {
        Message = message;
        HasMessage = message.Length > 0;
    }

    private void Rebuild()
    {
        var q = Query.Trim();
        var shown = _clipboard.History
            .Where(c => q.Length == 0 || c.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        Groups.Clear();
        var pinned = shown.Where(c => c.Pinned).ToList();
        if (pinned.Count > 0)
        {
            Groups.Add(new ClipGroupVm("Pinned", pinned.Select(c => new ClipRowVm(c, this)).ToList()));
        }
        foreach (var day in shown.Where(c => !c.Pinned).GroupBy(c => c.At.LocalDateTime.Date).OrderByDescending(g => g.Key))
        {
            Groups.Add(new ClipGroupVm(DayLabel(day.Key), day.OrderByDescending(c => c.At).Select(c => new ClipRowVm(c, this)).ToList()));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(KeepHistory));
        OnPropertyChanged(nameof(HistoryOff));
    }

    internal static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("d MMMM", CultureInfo.CurrentCulture)
            : day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}
