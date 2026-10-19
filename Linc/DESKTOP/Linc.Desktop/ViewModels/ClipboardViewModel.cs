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
    /// <summary>The item, its text repaired if it was once misread as Windows-1252 (TextRepair):
    /// shown, copied back and sent as the repaired text.</summary>
    public ClipEntry Entry { get; } = entry with { Text = TextRepair.FixMojibake(entry.Text) };
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

/// <summary>A day (or "Pinned") and its rows. Observable, so a long day can be filled in after
/// the first screenful has drawn (ClipboardViewModel.RebuildAsync).</summary>
public sealed class ClipGroupVm(string header)
{
    public string Header { get; } = header;
    public ObservableCollection<ClipRowVm> Items { get; } = [];
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

    // ---- Building the list (owner concerns 2026-10-19 §3: "Clipboard page loads slower") ----
    //
    // The history is up to 2000 items, and every row is a card with four buttons, all built at
    // once on the UI thread (and, before, each animated in by an AddDeleteThemeTransition). Now
    // the filtering, grouping and row view models are made off the UI thread, the first
    // screenful goes on screen at once, and the rest follows in batches at low priority, so the
    // page is usable while the tail is still being added. A newer rebuild (a keystroke in the
    // search box, a new clip) abandons an older one.

    /// <summary>Rows put on screen straight away; enough to fill a maximised window.</summary>
    private const int FirstScreenRows = 30;

    /// <summary>Rows added per low-priority pass after that.</summary>
    private const int BatchRows = 60;

    private int _generation;

    private void Rebuild() => _ = RebuildAsync();

    private async Task RebuildAsync()
    {
        int generation = ++_generation;
        var q = Query.Trim();
        var history = _clipboard.History; // an immutable snapshot, safe to read off the UI thread
        List<(string Header, List<ClipRowVm> Rows)> built;
        try
        {
            built = await Task.Run(() => Build(history, q));
        }
        catch (Exception)
        {
            return; // cosmetic: the next change rebuilds
        }
        if (generation != _generation)
        {
            return;
        }

        Groups.Clear();
        var rest = new List<(ClipGroupVm Group, List<ClipRowVm> Rows, int From)>();
        int budget = FirstScreenRows;
        foreach (var (header, rows) in built)
        {
            var group = new ClipGroupVm(header);
            int now = Math.Min(budget, rows.Count);
            for (int i = 0; i < now; i++) group.Items.Add(rows[i]);
            budget -= now;
            if (now < rows.Count) rest.Add((group, rows, now));
            Groups.Add(group);
        }
        RaiseListState();

        foreach (var (group, rows, from) in rest)
        {
            for (int i = from; i < rows.Count; i += BatchRows)
            {
                await Yield();
                if (generation != _generation) return;
                for (int j = i; j < Math.Min(rows.Count, i + BatchRows); j++) group.Items.Add(rows[j]);
            }
        }
    }

    /// <summary>Lets the page draw, then continues at low priority on the UI thread.</summary>
    private Task Yield()
    {
        var done = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => done.SetResult()))
        {
            done.SetResult();
        }
        return done.Task;
    }

    private List<(string Header, List<ClipRowVm> Rows)> Build(IReadOnlyList<ClipEntry> history, string q)
    {
        var shown = history
            .Where(c => q.Length == 0 || TextRepair.FixMojibake(c.Text).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var groups = new List<(string, List<ClipRowVm>)>();
        var pinned = shown.Where(c => c.Pinned).Select(c => new ClipRowVm(c, this)).ToList();
        if (pinned.Count > 0)
        {
            groups.Add(("Pinned", pinned));
        }
        foreach (var day in shown.Where(c => !c.Pinned).GroupBy(c => c.At.LocalDateTime.Date).OrderByDescending(g => g.Key))
        {
            groups.Add((DayLabel(day.Key), day.OrderByDescending(c => c.At).Select(c => new ClipRowVm(c, this)).ToList()));
        }
        return groups;
    }

    private void RaiseListState()
    {
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
