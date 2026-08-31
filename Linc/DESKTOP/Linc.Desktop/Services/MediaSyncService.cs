using Linc.Desktop.Protocol;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.Services;

public sealed record MediaState(
    string? Title, string? Artist, string? Album, string? ArtId,
    long? PositionMs, long? DurationMs, bool Playing, string? AppPackage);

public interface IMediaSyncService
{
    /// <summary>Must be called once from the UI thread.</summary>
    void Start();

    /// <summary>Raised on the UI thread whenever the media state changes.</summary>
    event Action? Changed;

    /// <summary>The phone's primary media session, or null when there is none.</summary>
    MediaState? Current { get; }

    /// <summary>play / pause / next / prev / seek (docs/PROTOCOL.md v6).</summary>
    Task ControlAsync(string action, long? positionMs = null);
}

/// <summary>
/// Desktop side of the media bridge (docs/PROTOCOL.md v6): mirrors `media.state`
/// events from the phone's primary MediaSession and sends transport controls back.
/// </summary>
public sealed class MediaSyncService(
    IConnectionManager connection,
    IConnectionSupervisor supervisor) : IMediaSyncService
{
    private DispatcherQueue? _dispatcher;

    public event Action? Changed;

    public MediaState? Current { get; private set; }

    public void Start()
    {
        if (_dispatcher is not null)
        {
            return;
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        connection.CompanionMessageReceived += envelope =>
            _dispatcher.TryEnqueue(() => OnCompanionMessage(envelope));
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            if (supervisor.State != LinkState.Connected && Current is not null)
            {
                Current = null;
                Changed?.Invoke();
            }
        });
    }

    public async Task ControlAsync(string action, long? positionMs = null)
    {
        try
        {
            await connection.SendMediaControlAsync(action, positionMs, CancellationToken.None);
        }
        catch (LincException)
        {
            // Link is dying; the supervisor will notice.
        }
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        if (envelope.Type != MessageType.MediaState)
        {
            return;
        }
        Current = (bool?)envelope.Payload["none"] == true
            ? null
            : new MediaState(
                Title: (string?)envelope.Payload["title"],
                Artist: (string?)envelope.Payload["artist"],
                Album: (string?)envelope.Payload["album"],
                ArtId: (string?)envelope.Payload["artId"],
                PositionMs: (long?)envelope.Payload["positionMs"],
                DurationMs: (long?)envelope.Payload["durationMs"],
                Playing: (bool?)envelope.Payload["playing"] ?? false,
                AppPackage: (string?)envelope.Payload["appPackage"]);
        Changed?.Invoke();
    }
}
