using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Linc.Desktop.Services;

public sealed record ClipEntry(string Text, bool FromPhone, DateTimeOffset At);

public interface IClipboardSyncService
{
    bool Enabled { get; set; }

    /// <summary>Must be called once from the UI thread.</summary>
    void Start();

    /// <summary>Last few synced clips, newest first. In-memory only — never persisted or logged.</summary>
    IReadOnlyList<ClipEntry> History { get; }

    /// <summary>Raised on the UI thread when <see cref="History"/> changes.</summary>
    event Action? HistoryChanged;

    /// <summary>Puts a history entry back on the PC clipboard (which also re-syncs it to the phone).</summary>
    void Recopy(ClipEntry entry);
}

/// <summary>
/// Two-way clipboard sync (docs/PROTOCOL.md v1). Desktop copies push to the phone;
/// the phone sends `clipboard.changed` while its Linc app is focused (Android only
/// lets the focused app read the clipboard — a platform restriction, not ours).
/// Nothing is stored beyond the last synced text, which exists purely for echo protection.
/// </summary>
public sealed class ClipboardSyncService(
    IConnectionManager connection,
    IConnectionSupervisor supervisor,
    IDeviceRegistry registry,
    ILogService log) : IClipboardSyncService
{
    private const int HistorySize = 5;

    private DispatcherQueue? _dispatcher;
    private string? _lastReceived;
    private string? _lastSent;
    private readonly List<ClipEntry> _history = [];

    public bool Enabled
    {
        get => registry.ClipboardSyncEnabled;
        set => registry.SaveClipboardSyncEnabled(value);
    }

    public IReadOnlyList<ClipEntry> History => _history;

    public event Action? HistoryChanged;

    public void Recopy(ClipEntry entry)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(entry.Text);
            Clipboard.SetContent(package);
            // The ContentChanged handler takes it from here (push to phone, history).
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; nothing to do.
        }
    }

    /// <summary>UI thread only. Newest first, de-duplicated, capped.</summary>
    private void AddHistory(string text, bool fromPhone)
    {
        _history.RemoveAll(entry => entry.Text == text);
        _history.Insert(0, new ClipEntry(text, fromPhone, DateTimeOffset.Now));
        while (_history.Count > HistorySize)
        {
            _history.RemoveAt(_history.Count - 1);
        }
        HistoryChanged?.Invoke();
    }

    public void Start()
    {
        if (_dispatcher is not null)
        {
            return;
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Clipboard.ContentChanged += (_, _) => _ = OnDesktopClipboardChangedAsync();
        connection.CompanionMessageReceived += envelope =>
            _dispatcher.TryEnqueue(() => OnCompanionMessage(envelope));
    }

    private async Task OnDesktopClipboardChangedAsync()
    {
        if (!Enabled || supervisor.State != LinkState.Connected)
        {
            return;
        }
        // The phone must speak v1 for clipboard messages to mean anything.
        if (connection.Current is not { Companion.V: >= 1 })
        {
            return;
        }
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }
            text = await content.GetTextAsync();
        }
        catch (Exception)
        {
            return; // clipboard owned by a picky app; skip this change
        }
        if (string.IsNullOrEmpty(text) || text == _lastSent || text == _lastReceived)
        {
            return;
        }
        _lastSent = text;
        try
        {
            await connection.SendClipboardAsync(text, CancellationToken.None);
            log.Log(LogLevel.Info, "Clipboard synced to phone");
            AddHistory(text, fromPhone: false);
        }
        catch (LincException)
        {
            // Link is dying; the supervisor's health loop will notice and reconnect.
        }
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (envelope.Type != MessageType.ClipboardChanged || !Enabled)
        {
            return;
        }
        var text = (string?)envelope.Payload["text"];
        if (string.IsNullOrEmpty(text) || text == _lastReceived)
        {
            return;
        }
        _lastReceived = text;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            log.Log(LogLevel.Info, "Clipboard synced from phone");
            AddHistory(text, fromPhone: true);
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; the next change will win.
        }
    }
}
