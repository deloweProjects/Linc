using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Protocol;

// Companion protocol v0 — the wire contract with the Linc Android companion.
// Spec: docs/PROTOCOL.md. Wire-format changes require a version bump there first.

public static class ProtocolConstants
{
    public const int Version = 32;

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
    public const string ClipboardGet = "clipboard.get";       // either direction: "send me your clipboard" (v24)

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
    public const string PcMirrorCursor = "pc.mirror.cursor";  // desktop → phone: pointer position/shape (v20)
    public const string PcMirrorStats = "pc.mirror.stats";    // desktop → phone: link health, 1/s (v20)
    public const string PcMirrorPing = "pc.mirror.ping";      // phone → desktop: round-trip probe (v20)
    public const string PcMirrorPong = "pc.mirror.pong";      // desktop → phone: echo of the ping (v20)
    public const string PcAudioStart = "pc.audio.start";      // phone → desktop: stream PC sound on channel 6 (v20)
    public const string PcAudioStop = "pc.audio.stop";        // phone → desktop (v20)
    public const string PcVoiceAccessSet = "pc.voiceaccess.set";     // phone → desktop: start/stop Voice Access (v21)
    public const string PcVoiceAccessGet = "pc.voiceaccess.get";     // phone → desktop: ask for its state (v21)
    public const string PcVoiceAccessState = "pc.voiceaccess.state"; // desktop → phone (v21)
    public const string PcTabletModeSet = "pc.tabletmode.set";       // phone → desktop: Windows tablet mode on/off (v22)
    public const string PcTabletModeGet = "pc.tabletmode.get";       // phone → desktop: ask for its state (v22)
    public const string PcTabletModeState = "pc.tabletmode.state";   // desktop → phone (v22)
    public const string PcMicSet = "pc.mic.set";                     // phone → desktop: the phone as the PC's mic, any app (v23)
    public const string PcMicAuto = "pc.mic.auto";                   // phone → desktop: PC Remote with "Phone" chosen (v23)
    public const string PcSpeakerSet = "pc.speaker.set";       // phone → desktop: {on} phone as PC speaker (v25)
    public const string PcSpeakerState = "pc.speaker.state";   // desktop → phone: {on, cable, error?} (v25)

    // v26 (D-096) — Play everywhere: the PC's sound on its own speakers, its Bluetooth devices
    // and the phone's at once, in step. Audio rides pc-audio channel 6 with PC-clock capture
    // timestamps; the phone times the PC's clock over the same channel's back direction.
    public const string AudioEverywhereSet = "audio.everywhere.set";       // phone → desktop: {on?, device?, marginMs?, calibrate?, clicks?, resetAlignment?}
    public const string AudioEverywhereState = "audio.everywhere.state";   // desktop → phone: the mixer, the group delay
    public const string AudioEverywhereReport = "audio.everywhere.report"; // phone → desktop: its outputs, their needs, calibration results
    public const string AudioEverywhereTest = "audio.everywhere.test";     // desktop → phone: test sounds to play / listen for

    // v27 (D-097) — the phone's full-page PC tools: look, info, running apps, files, terminal.
    public const string PcLookGet = "pc.look.get";             // phone → desktop: {}
    public const string PcLookSet = "pc.look.set";             // phone → desktop: {appsDark?, systemDark?, accent?, autoAccent?, transparency?}
    public const string PcLookState = "pc.look.state";         // desktop → phone
    public const string PcWallpaperSet = "pc.wallpaper.set";   // phone → desktop: {jpeg (base64), fit}
    public const string PcInfoGet = "pc.info.get";             // phone → desktop: {}
    public const string PcInfo = "pc.info";                    // desktop → phone: name, os, cpu, memory, disks, battery
    public const string PcAppsGet = "pc.apps.get";             // phone → desktop: {}
    public const string PcApps = "pc.apps";                    // desktop → phone: {apps: [{pid, name, title, memMb}]}
    public const string PcAppsEnd = "pc.apps.end";             // phone → desktop: {pid}
    public const string PcOpen = "pc.open";                    // phone → desktop: {url} or {path}
    public const string PcFilesList = "pc.files.list";         // phone → desktop: {path} ("" = the starting places)
    public const string PcFilesListing = "pc.files.listing";   // desktop → phone: {path, parent, entries, error?}
    public const string PcFilesOp = "pc.files.op";             // phone → desktop: {op: mkdir|rename|delete|open|download|upload, id, ...}
    public const string PcFilesDone = "pc.files.done";         // desktop → phone: {op, id, ok, path?, error?}
    public const string PcTermGet = "pc.term.get";             // phone → desktop: {}
    public const string PcTermOpen = "pc.term.open";           // phone → desktop: {shell, cols, rows} → channel 9
    public const string PcTermState = "pc.term.state";         // desktop → phone: {shells, running, error?}

    // v28 — the PC's sound output, a curtain over the PC's screen while the phone uses it, and
    // the Device page reaching into the phone: its sound on the PC, screen time, wallpaper.
    public const string PcAudioOutputsGet = "pc.audio.outputs.get"; // phone → desktop: {}
    public const string PcAudioOutputs = "pc.audio.outputs";        // desktop → phone: {outputs: [{id, name, bluetooth, default}]}
    public const string PcAudioOutputSet = "pc.audio.output.set";   // phone → desktop: {id}
    public const string PcCurtainSet = "pc.curtain.set";            // phone → desktop: {on}
    public const string PcCurtainState = "pc.curtain.state";        // desktop → phone: {on, available, error?}
    public const string PhoneSoundAsk = "phone.sound.ask";          // desktop → phone: {on}
    public const string PhoneUsageGet = "phone.usage.get";          // desktop → phone: {}
    public const string PhoneUsage = "phone.usage";                 // phone → desktop: {allowed, totalMin, unlocks?, apps}
    public const string PhoneWallpaperSet = "phone.wallpaper.set";  // desktop → phone: {jpeg (base64), target}

    // v29 — the phone's sound on the PC over Bluetooth: Windows becomes the phone's Bluetooth
    // speaker (AudioPlaybackConnection), so nothing is recorded on the phone.
    public const string PcBtAudioSet = "pc.btaudio.set";            // phone → desktop: {on, names: [the phone's Bluetooth names]}
    public const string PcBtAudioGet = "pc.btaudio.get";            // phone → desktop: {}
    public const string PcBtAudioState = "pc.btaudio.state";        // desktop → phone: {on, supported, device?, error?}

    // v30 — the phone's Sync page: the same lanes and folder pairs as the PC's, either side edits.
    public const string SyncStateGet = "sync.state.get";            // phone → desktop: {}
    public const string SyncState = "sync.state";                   // desktop → phone: {lanes, pairs: [{id, name, pcPath, phonePath, direction, enabled}], status, photosPcPath}
    public const string SyncPairsSet = "sync.pairs.set";            // phone → desktop: {pairs}
    public const string SyncLaneSet = "sync.lane.set";              // phone → desktop: {lane, on}
    public const string SyncNow = "sync.now";                       // phone → desktop: {}

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

    // v32 — LincLink (Vibe/agent/design/linclink.md): one session over many routes.
    public const string LinkAck = "link.ack";       // either way: {ack, echo?}; never sequenced
    public const string LinkPair = "link.pair";     // phone → desktop, inside the PC's pairing window only
    public const string LinkSecret = "link.secret"; // desktop → phone: {secret, gen}
    public const string LinkWhere = "link.where";   // either way: {addrs, port}
    public const string LinkNoop = "link.noop";     // either way: a numbered frame that does nothing (stands in for a request given up on)
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
    [property: JsonPropertyName("payload")] JsonObject Payload,
    // v32 (LincLink): the frame's number in its session and the highest number received, so a
    // session can move to another transport and replay what was lost. Absent before v32 and on
    // the hello; never written as null, so an older peer sees exactly the envelope it always did.
    [property: JsonPropertyName("seq"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Seq = null,
    [property: JsonPropertyName("ack"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Ack = null)
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
