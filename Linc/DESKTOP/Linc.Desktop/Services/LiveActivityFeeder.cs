using System.ComponentModel;
using System.Diagnostics;
using Linc.Desktop.ViewModels;

namespace Linc.Desktop.Services;

/// <summary>
/// Turns what Linc already knows into Linc Island activities: a share going to or arriving from
/// the phone, a ringing call, music playing, mirroring, connecting / reconnecting and a low
/// battery. It only listens: every source's events are read as they are, nothing about them
/// changes. Each handler hops to the UI thread before doing anything, so a slow or failing island
/// can never hold up the feature that raised the event.
/// </summary>
public sealed class LiveActivityFeeder(
    ILiveActivityService island,
    IShareService share,
    IMediaSyncService media,
    IMirrorService mirror,
    IConnectionSupervisor supervisor,
    HomeViewModel home,
    ILogService log)
{
    private const int LowBattery = 15;
    private const int BatteryRearm = 20;

    /// <summary>How long after the link drops (or Linc starts) "Connecting…" is worth showing;
    /// past it a phone that is simply away would keep the island busy for nothing.</summary>
    private static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(90);

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private bool _started;
    private bool _batteryShown;
    private bool _mediaShown;
    private DateTimeOffset? _pausedAt;
    private static readonly TimeSpan MediaLinger = TimeSpan.FromSeconds(30);
    private LinkState _lastLink = LinkState.NoDevice;
    private DateTimeOffset _linkWindowStart = DateTimeOffset.UtcNow;

    /// <summary>Must be called once from the UI thread.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        island.Start();

        share.TransferChanged += t => OnUi(() => OnTransfer(t));
        share.ShareReceived += s => OnUi(() => OnReceived(s));
        media.Changed += () => OnUi(OnMedia);
        mirror.StateChanged += () => OnUi(OnMirror);
        supervisor.StateChanged += () => OnUi(OnLink);
        supervisor.StatusUpdated += s => OnUi(() => OnStatus(s));
        home.PropertyChanged += OnHomeChanged;

        OnLink();
        OnMedia();
        OnMirror();
    }

    private string Phone => supervisor.Device?.Model ?? "your phone";

    // ---- Shares (IShareService.TransferChanged / ShareReceived, read only) ----

    private void OnTransfer(PhoneTransfer t)
    {
        var id = $"share:{t.Id}";
        var openShare = new LiveAction("Open Share", () => island.RequestNavigate("Share"));
        switch (t.State)
        {
            case PhoneTransferState.Sending:
                var pct = (int)Math.Clamp(t.Progress, 0, 100);
                island.Post(new LiveActivity(id, LiveActivityKind.Share, t.ToPhone ? "\uE898" : "\uE896", t.Name,
                    t.ToPhone ? $"Sending to {Phone} · {pct}%" : $"Receiving from {Phone} · {pct}%", $"{pct}%", pct,
                    [new LiveAction("Cancel", () => share.Cancel(t.Id), Danger: true), openShare]));
                break;
            case PhoneTransferState.Done when t.ToPhone:
                island.Post(new LiveActivity(id, LiveActivityKind.Share, "\uE73E", t.Name,
                    $"Saved on {Phone}", "✓", 100, [openShare]));
                island.EndAfter(id, TimeSpan.FromSeconds(4));
                break;
            case PhoneTransferState.Done:
                island.End(id); // OnReceived shows the received file, with Open
                break;
            case PhoneTransferState.Cancelled:
                island.Post(new LiveActivity(id, LiveActivityKind.Share, "\uE711", t.Name, "Cancelled", null, null, [openShare]));
                island.EndAfter(id, TimeSpan.FromSeconds(3));
                break;
            case PhoneTransferState.Failed:
                island.Post(new LiveActivity(id, LiveActivityKind.Share, "\uE783", t.Name,
                    t.Message ?? (t.ToPhone ? "Couldn't send it" : "Couldn't receive it"), "!", null, [openShare]));
                island.EndAfter(id, TimeSpan.FromSeconds(8));
                break;
        }
    }

    private void OnReceived(ReceivedShare s)
    {
        var id = $"received:{s.LocalPath}";
        island.Post(new LiveActivity(id, LiveActivityKind.Share, "\uE896", s.Name,
            $"Received from {Phone}", "✓", 100,
            [
                new LiveAction("Open", () => Shell(s.LocalPath, null)),
                new LiveAction("Show in folder", () => Shell("explorer.exe", $"/select,\"{s.LocalPath}\"")),
            ]));
        island.EndAfter(id, TimeSpan.FromSeconds(8));
    }

    // ---- Calls and music (Home already listens for both; the island reads its state) ----

    private void OnHomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.IncomingRinging):
            case nameof(HomeViewModel.IncomingText):
                OnUi(OnCall);
                break;
            case nameof(HomeViewModel.MediaArt):
                OnUi(OnMedia);
                break;
        }
    }

    private void OnCall()
    {
        if (!home.IncomingRinging)
        {
            island.End("call");
            return;
        }
        island.Post(new LiveActivity("call", LiveActivityKind.Call, "\uE717", "Incoming call",
            home.IncomingText, "ringing", null,
            [
                new LiveAction("Decline", () => home.DeclineCommand.Execute(null), Danger: true),
                new LiveAction("Open Calls", () => island.RequestNavigate("Home")),
            ],
            AutoExpand: true));
    }

    private void OnMedia()
    {
        var state = media.Current;
        // Paused: it stays a while so Play is still there, then tucks away.
        var paused = state is { Playing: false };
        _pausedAt = paused ? _pausedAt ?? DateTimeOffset.UtcNow : null;
        var pausedFor = DateTimeOffset.UtcNow - (_pausedAt ?? DateTimeOffset.UtcNow);
        if (state is null || (paused && (!_mediaShown || pausedFor >= MediaLinger)))
        {
            _mediaShown = false;
            island.End("media");
            return;
        }
        _mediaShown = true;
        double? progress = state.DurationMs is > 0 && state.PositionMs is { } pos
            ? Math.Clamp(100.0 * pos / state.DurationMs.Value, 0, 100) : null;
        island.Post(new LiveActivity("media", LiveActivityKind.Media, "\uE8D6",
            string.IsNullOrEmpty(state.Title) ? "Now playing" : state.Title,
            state.Artist, null, progress,
            [
                new LiveAction("Previous", () => _ = media.ControlAsync("prev"), Glyph: "\uE892"),
                state.Playing
                    ? new LiveAction("Pause", () => _ = media.ControlAsync("pause"), Glyph: "\uE769")
                    : new LiveAction("Play", () => _ = media.ControlAsync("play"), Glyph: "\uE768"),
                new LiveAction("Next", () => _ = media.ControlAsync("next"), Glyph: "\uE893"),
            ],
            Art: home.MediaArt));
        if (paused)
        {
            island.EndAfter("media", MediaLinger - pausedFor);
        }
    }

    // ---- Mirroring ----

    private void OnMirror()
    {
        if (mirror.State != MirrorState.Running)
        {
            island.End("mirror");
            return;
        }
        island.Post(new LiveActivity("mirror", LiveActivityKind.Mirror, "\uE7F4", "Mirroring",
            $"{Phone}'s screen is on this PC", null, null,
            [new LiveAction("Stop", () => _ = mirror.StopAsync(), Danger: true)]));
    }

    // ---- The link ----

    private void OnLink()
    {
        var state = supervisor.State;
        if (_lastLink == LinkState.Connected && state != LinkState.Connected)
        {
            _linkWindowStart = DateTimeOffset.UtcNow; // the link just dropped
        }
        var wasConnected = _lastLink == LinkState.Connected;
        _lastLink = state;

        bool recent = DateTimeOffset.UtcNow - _linkWindowStart < LinkWindow;
        if (state is LinkState.Connected or LinkState.NoDevice or LinkState.Paused || !recent)
        {
            island.End("link");
            return;
        }
        var model = supervisor.Device?.Model ?? "your phone";
        var reconnecting = wasConnected || state == LinkState.Searching;
        island.Post(new LiveActivity("link", LiveActivityKind.Link, "\uE895",
            reconnecting ? "Reconnecting…" : "Connecting…",
            reconnecting ? $"The link to {model} dropped; trying again" : $"Connecting to {model}",
            null, null,
            [new LiveAction("Details", () => island.RequestNavigate("Device"))]));
        island.EndAfter("link", LinkWindow);
    }

    // ---- Battery ----

    private void OnStatus(DeviceStatus status)
    {
        if (status.Charging || status.Battery > BatteryRearm)
        {
            _batteryShown = false;
            island.End("battery");
            return;
        }
        if (_batteryShown || status.Battery is < 0 or > LowBattery)
        {
            return;
        }
        _batteryShown = true; // once per low spell, like the Windows alert
        island.Post(new LiveActivity("battery", LiveActivityKind.Battery, "\uEBA0",
            $"Battery {status.Battery}%", $"{Phone} is not charging", $"{status.Battery}%", null,
            [new LiveAction("Dismiss", () => island.End("battery"))]));
        island.EndAfter("battery", TimeSpan.FromMinutes(1));
    }

    // ---- Helpers ----

    private void OnUi(Action action) => _dispatcher?.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Linc Island: {ex.GetType().Name}: {ex.Message}");
        }
    });

    private void Shell(string file, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Linc Island couldn't open {file}: {ex.Message}");
        }
    }
}
