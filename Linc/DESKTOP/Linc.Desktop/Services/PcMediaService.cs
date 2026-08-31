using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Windows.Media.Control;

namespace Linc.Desktop.Services;

public interface IPcMediaService
{
    void Start();
}

/// <summary>
/// Mirrors the PC's own media to the phone's Home widget (M19, D-028): reads Windows'
/// <c>GlobalSystemMediaTransportControlsSessionManager</c>, pushes `pc.media.state` on every
/// change (and on connect), and executes `pc.media.control` coming back from the phone.
/// No album art in v1 — the bulk channel is phone-served, so there is no reverse path.
/// </summary>
public sealed class PcMediaService(
    IConnectionManager connection, IConnectionSupervisor supervisor, ILogService log) : IPcMediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _lastLoggedSummary;
    private string? _lastPublishedSummary;
    private DateTime _lastPublishAt = DateTime.MinValue;

    // The Winamp-API fallback has no change events, so it has to be polled. Cheap: two window
    // messages, and only while no SMTC session exists.
    private Timer? _fallbackPoll;

    public async void Start()
    {
        connection.CompanionMessageReceived += OnCompanionMessage;
        supervisor.StateChanged += () =>
        {
            if (supervisor.State == LinkState.Connected)
            {
                _ = PublishAsync(); // the phone just arrived — tell it what's playing
            }
        };
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            // A player can appear in the session list without ever becoming the "current"
            // session, so watch the list too — otherwise its media is never noticed.
            _manager.SessionsChanged += OnSessionsChanged;
            AttachSession();
        }
        catch (Exception)
        {
            // No media session service on this machine; the Winamp-API fallback may still work.
            log.Log(LogLevel.Warn, "This PC didn't expose a media session; the phone's PC-media widget will stay empty.");
        }

        _fallbackPoll = new Timer(_ => PollFallback(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Publishes Winamp-API players (AIMP and friends) that never register with SMTC. Only
    /// republishes when the visible state changes, so this is quiet when nothing is happening.
    /// </summary>
    private void PollFallback()
    {
        var smtcPlaying = _session is { } session && IsPlaying(session);

        // Republish periodically while something is playing, not only when the track changes.
        // Two reasons: the phone's progress would otherwise sit frozen between tracks, and a
        // phone that reopens its Home screen (or whose widget missed an update) converges
        // within seconds instead of showing a stale track indefinitely.
        if (DateTime.UtcNow - _lastPublishAt > TimeSpan.FromSeconds(6)
            && (smtcPlaying || WinampRemote.TryGetState()?.Playing == true))
        {
            _lastPublishAt = DateTime.UtcNow;
            _ = PublishAsync();
            return;
        }

        // Otherwise only react to a Winamp-API change; SMTC raises its own events.
        if (smtcPlaying)
        {
            return;
        }
        var state = WinampRemote.TryGetState();
        var summary = state is null ? "none" : $"{state.Title}|{state.Playing}";
        if (summary == _lastPublishedSummary)
        {
            return;
        }
        _lastPublishedSummary = summary;
        _lastPublishAt = DateTime.UtcNow;
        _ = PublishAsync();
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => AttachSession();

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => AttachSession();

    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _session = PickSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        _ = PublishAsync();
    }

    /// <summary>
    /// Windows can report no "current" session while players are in fact registered (a player
    /// that never claims focus, or a stale entry holding the slot). Falling back to the session
    /// list — preferring one that is actually playing — mirrors the sticky primary election the
    /// phone side needed in M13, where the same assumption caused the media widget to sit empty.
    /// </summary>
    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false; // the session vanished mid-call
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
        {
            return null;
        }
        if (_manager.GetCurrentSession() is { } current)
        {
            return current;
        }
        var sessions = _manager.GetSessions();
        if (sessions is null || sessions.Count == 0)
        {
            return null;
        }
        return sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions[0];
    }

    /// <summary>Reduces the live players to the four booleans <see cref="PcMediaRouting"/> decides on.</summary>
    private static PcMediaTarget PickTarget(
        GlobalSystemMediaTransportControlsSession? session, WinampState? winamp) =>
        PcMediaRouting.PickTarget(
            hasSmtcSession: session is not null,
            smtcPlaying: session is not null && IsPlaying(session),
            hasWinamp: winamp is not null,
            winampPlaying: winamp?.Playing == true);

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = PublishAsync();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = PublishAsync();

    private async Task PublishAsync()
    {
        var payload = await BuildPayloadAsync();
        // Playback events fire many times a second, so log only when what the phone would
        // show actually changes. Without this the service was silent and a "PC media never
        // appears on the phone" report left nothing to diagnose from.
        var summary = payload["none"] is not null ? "nothing playing" : $"{payload["title"]} ({payload["app"]})";
        if (summary != _lastLoggedSummary)
        {
            _lastLoggedSummary = summary;
            log.Log(LogLevel.Info, $"PC media for the phone: {summary}");
        }
        try
        {
            await connection.SendToPhoneAsync(
                Envelope.Create(MessageType.PcMediaState, payload), CancellationToken.None);
        }
        catch (Exception)
        {
            // Cosmetic push; never let it surface.
        }
    }

    private async Task<JsonObject> BuildPayloadAsync()
    {
        var session = _session;
        var winamp = WinampRemote.TryGetState();
        // One rule decides who the phone sees AND who its controls reach (PcMediaRouting):
        // a Winamp-API player that is actually making sound outranks an SMTC session that is
        // not, but otherwise a paused SMTC session is still the player to show.
        switch (PickTarget(session, winamp))
        {
            case PcMediaTarget.Winamp when winamp is not null:
                return new JsonObject
                {
                    ["title"] = winamp.Title,
                    ["artist"] = "",
                    ["playing"] = winamp.Playing,
                    ["positionMs"] = winamp.PositionMs,
                    ["durationMs"] = winamp.DurationMs,
                    ["app"] = "winamp-api",
                };
            case PcMediaTarget.None:
                return new JsonObject { ["none"] = true };
        }
        if (session is null)
        {
            return new JsonObject { ["none"] = true };
        }
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            return new JsonObject
            {
                ["title"] = properties?.Title ?? "",
                ["artist"] = properties?.Artist ?? "",
                ["playing"] = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                ["positionMs"] = (long)timeline.Position.TotalMilliseconds,
                ["durationMs"] = (long)timeline.EndTime.TotalMilliseconds,
                ["app"] = session.SourceAppUserModelId,
            };
        }
        catch (Exception)
        {
            return new JsonObject { ["none"] = true };
        }
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (envelope.Type != MessageType.PcMediaControl)
        {
            return;
        }
        var action = (string?)envelope.Payload["action"];
        if (action is null)
        {
            return;
        }
        // Route the control to whichever player the phone is actually showing — the SAME rule
        // BuildPayloadAsync publishes with (PcMediaRouting.PickTarget). Gating this on
        // IsPlaying instead is what made a paused SMTC player pausable but never resumable.
        var target = PickTarget(_session, WinampRemote.TryGetState());
        if (target == PcMediaTarget.Smtc && _session is { } session)
        {
            _ = ExecuteAsync(session, action);
        }
        else if (target == PcMediaTarget.Winamp && WinampRemote.Control(action))
        {
            // Reflect the new play/pause state on the phone without waiting for the poll.
            _ = PublishAsync();
        }
    }

    private static async Task ExecuteAsync(GlobalSystemMediaTransportControlsSession session, string action)
    {
        try
        {
            switch (action)
            {
                case "play": await session.TryPlayAsync(); break;
                case "pause": await session.TryPauseAsync(); break;
                case "next": await session.TrySkipNextAsync(); break;
                case "prev": await session.TrySkipPreviousAsync(); break;
            }
        }
        catch (Exception)
        {
            // The player refused or vanished; nothing useful to surface on the phone.
        }
    }
}
