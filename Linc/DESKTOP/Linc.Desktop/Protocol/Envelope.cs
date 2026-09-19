using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Protocol;

// Companion protocol v0 — the wire contract with the Linc Android companion.
// Spec: docs/PROTOCOL.md. Wire-format changes require a version bump there first.

public static class ProtocolConstants
{
    public const int Version = 19;

    /// <summary>Lowest version this app can still speak (peers may negotiate down to it).</summary>
    public const int MinVersion = 0;

    public const string SocketName = "linc";

    /// <summary>The companion's package and service, for starting it over ADB (M01, D-035).</summary>
    public const string CompanionPackage = "app.linc.android";
    public const string CompanionService = ".service.CompanionService";
}

public static class MessageType
{
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string StatusGet = "status.get";
    public const string Status = "status";
    public const string Error = "error";

    // v1
    public const string Ok = "ok";
    public const string ClipboardSet = "clipboard.set";
    public const string ClipboardChanged = "clipboard.changed";

    // v2
    public const string NotificationPosted = "notification.posted";
    public const string NotificationRemoved = "notification.removed";
    public const string NotificationDismiss = "notification.dismiss";

    // v6
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string NotificationAction = "notification.action";
    public const string NotificationReply = "notification.reply";
    public const string MediaState = "media.state";
    public const string MediaControl = "media.control";

    // v7
    public const string DeviceLocate = "device.locate";
    public const string DndSet = "dnd.set";

    // v8
    public const string SoundSet = "sound.set";

    // v9
    public const string TlsExchange = "tls.exchange";
    public const string ChannelOpen = "channel.open";

    // v10
    public const string PhotosRecent = "photos.recent";
    public const string ContinueUrl = "continue.url";
    public const string ShareItem = "share.item";

    // v11
    public const string SyncConfig = "sync.config";
    public const string SmsList = "sms.list";
    public const string SmsSend = "sms.send";
    public const string SmsReceived = "sms.received";

    // v12
    public const string CallLog = "call.log";
    public const string CallDial = "call.dial";
    public const string CallDecline = "call.decline";
    public const string CallIncoming = "call.incoming";

    // v13 — PC → phone push (media + share) behind the phone's Home/Share screens
    public const string PcMediaState = "pc.media.state";      // desktop → phone
    public const string PcMediaControl = "pc.media.control";  // phone → desktop
    public const string ShareIncoming = "share.incoming";     // desktop → phone

    // v14 — reverse mirror: the phone views and controls the PC (M05, D-029)
    public const string PcMirrorStart = "pc.mirror.start";    // phone → desktop
    public const string PcMirrorStop = "pc.mirror.stop";      // either direction
    public const string PcDisplaysGet = "pc.displays.get";    // phone → desktop
    public const string PcDisplays = "pc.displays";           // desktop → phone
    public const string PcInput = "pc.input";                 // phone → desktop
    public const string PcMirrorKeyframe = "pc.mirror.keyframe"; // phone → desktop: resend an IDR (v19)
    public const string PcTextFocus = "pc.textfocus";         // desktop → phone: a text field gained/lost focus

    // v15 — display control from the PC (M5c, D-001/D-055): rotation and brightness widgets
    // ride the protocol so a Direct-TLS link without ADB still works, and the phone applies
    // them through Settings.System (it has the WRITE_SETTINGS appop, the desktop doesn't).
    public const string DisplayRotationSet = "display.rotation.set";    // desktop → phone
    public const string DisplayBrightnessSet = "display.brightness.set"; // desktop → phone

    // v16 — the installed-app inventory (M6c, D-058). A pull on connect, reconciled against
    // the per-device cache; there is deliberately no install/remove push event. Icons stay on
    // the v5 bulk kind `appIcon` — M6c adds no second icon path.
    public const string AppsGet = "apps.get";                 // desktop → phone
    public const string Apps = "apps";                        // phone → desktop (replyTo set)

    // v17 — phone drives the PC: quick controls (M4b, D-042). Request/reply, unlike pc.input:
    // a dropped shutdown is not self-correcting the way a dropped mouse delta is.
    public const string PcControl = "pc.control";             // phone → desktop (replyTo: ok/error)
    public const string PcStateGet = "pc.state.get";          // phone → desktop
    public const string PcState = "pc.state";                 // desktop → phone (replyTo set)

    // v18 — instant ADB link-up over a hotspot (M13b). Discovery becomes a protocol message:
    // the desktop already holds a socket over the link, so the peer address answers "where is
    // the phone"; only the port is unknown, and the phone knows it. All four carry `gen`.
    public const string AdbAnnounce = "adb.announce";          // phone → desktop (unsolicited)
    public const string AdbDown = "adb.down";                  // phone → desktop
    public const string AdbArm = "adb.arm";                    // desktop → phone
    public const string AdbAck = "adb.ack";                    // desktop → phone
}

public static class Topic
{
    public const string Status = "status";
    public const string Notifications = "notifications";
    public const string Clipboard = "clipboard";
    public const string Media = "media";
}

public sealed record Envelope(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("replyTo")] string? ReplyTo,
    [property: JsonPropertyName("payload")] JsonObject Payload)
{
    public static Envelope Create(string type, JsonObject? payload = null, string? replyTo = null) =>
        new(ProtocolConstants.Version, type, Guid.NewGuid().ToString(), replyTo, payload ?? []);

    private static readonly JsonSerializerOptions Options = new()
    {
        // Unknown fields in known payloads must be ignored (spec §Message envelope);
        // replyTo is serialized even when null.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static Envelope Parse(string json) =>
        JsonSerializer.Deserialize<Envelope>(json, Options)
            ?? throw new JsonException("envelope deserialized to null");
}
