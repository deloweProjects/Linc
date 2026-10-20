using System.IO;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// The phone's Sync page (v30): the same lanes and folder pairs as the PC's, read and changed
/// from either side. The PC owns the setup (it runs the engine); the phone asks for it, edits
/// it, and is told whenever it changes or a sync pass finishes.
/// </summary>
public sealed class SyncRemote(IConnectionManager connection, IDeviceRegistry registry, ISyncEngine engine, ILogService log)
{
    private static readonly string[] Lanes = ["folders", "photos", "messages", "calls"];
    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        connection.CompanionMessageReceived += OnMessage;
        registry.SyncSetupChanged += () =>
        {
            engine.Reconfigure();
            _ = SendStateAsync();
        };
        engine.StatusChanged += () => _ = SendStateAsync();
    }

    private void OnMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.SyncStateGet:
                _ = SendStateAsync();
                break;
            case MessageType.SyncPairsSet:
                var pairs = (envelope.Payload["pairs"] as JsonArray)?.Select(FolderPair.FromJson).OfType<FolderPair>().ToList() ?? [];
                log.Log(LogLevel.Info, $"Sync: the phone set {pairs.Count} folder pair{(pairs.Count == 1 ? "" : "s")}");
                registry.SaveFolderPairs(pairs); // SyncSetupChanged reconfigures and answers
                break;
            case MessageType.SyncLaneSet:
                if ((string?)envelope.Payload["lane"] is { } lane && Lanes.Contains(lane))
                {
                    var on = (bool?)envelope.Payload["on"] ?? false;
                    log.Log(LogLevel.Info, $"Sync: the phone turned {lane} {(on ? "on" : "off")}");
                    registry.SaveSyncLane(lane, on);
                }
                break;
            case MessageType.SyncNow:
                _ = engine.ReconcileNowAsync(CancellationToken.None);
                break;
        }
    }

    public JsonObject State()
    {
        var lanes = new JsonObject();
        foreach (var lane in Lanes) lanes[lane] = registry.SyncLane(lane);
        return new JsonObject
        {
            ["lanes"] = lanes,
            ["pairs"] = new JsonArray(registry.FolderPairs.Select(p => (JsonNode)p.ToJson()).ToArray()),
            ["status"] = engine.StatusText,
            ["photosPcPath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc", "Photos"),
        };
    }

    private async Task SendStateAsync()
    {
        if (connection.Current is not { Companion.V: >= 30 }) return;
        try { await connection.SendToPhoneAsync(Envelope.Create(MessageType.SyncState, State()), CancellationToken.None); }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"Sync: couldn't tell the phone the sync setup ({ex.Message})"); }
    }
}
