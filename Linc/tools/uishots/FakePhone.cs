using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Linc.UiShots;

/// <summary>
/// A phone as far as the PC app can tell: dials its Direct TLS listener with a client certificate
/// the PC was told to trust (settings.json), answers the handshake, the health probe and the
/// requests Home makes, and serves a wallpaper over the bulk channel. Canned data only: what's
/// under test is how the PC draws a connected phone, not the phone.
/// </summary>
public sealed class FakePhone(X509Certificate2 cert, Action<string> log)
{
    public const string Serial = "UISHOTS-PIXEL7";
    public const string Model = "Pixel 7";
    private const int Port = 46001;
    private const int Version = 29;

    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly object _writeLock = new();

    public bool Connected { get; private set; }
    public int Requests { get; private set; }

    /// <summary>A self-signed client certificate with a persisted key (SChannel needs one).</summary>
    public static X509Certificate2 CreateCertificate(string pfxPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Linc uishots phone", key, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(pfxPath, ephemeral.Export(X509ContentType.Pfx, "uishots"));
        return new X509Certificate2(pfxPath, "uishots", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>Dial until the PC adopts the link, then serve it until <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ssl = await DialAsync(ct);
                await ServeControlAsync(ssl, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (Connected) log($"fake phone: link ended ({ex.GetType().Name}: {ex.Message})");
                Connected = false;
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task<SslStream> DialAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", Port, ct);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true, (_, _, _, _, _) => cert);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "linc-desktop",
            ClientCertificates = new X509CertificateCollection { cert },
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }, ct);
        return ssl;
    }

    // ---- control -----------------------------------------------------------------------------

    private async Task ServeControlAsync(SslStream ssl, CancellationToken ct)
    {
        // The desktop speaks first; a control connection stays silent until then.
        while (!ct.IsCancellationRequested)
        {
            var text = await ReadFrameAsync(ssl, ct) ?? throw new IOException("the PC closed the link");
            var msg = JsonNode.Parse(text)!.AsObject();
            var type = (string?)msg["type"] ?? "";
            var id = (string?)msg["id"];
            var payload = msg["payload"] as JsonObject ?? new JsonObject();
            Requests++;
            switch (type)
            {
                case "hello":
                    Send(ssl, "hello", id, new JsonObject { ["app"] = "linc-android", ["appVersion"] = "1.0.0", ["v"] = Version, ["sessionToken"] = _token });
                    break;
                case "subscribe":
                    Send(ssl, "ok", id, new JsonObject());
                    if ((string?)payload["topic"] == "media")
                    {
                        Connected = true;
                        log("fake phone: connected (hello and four subscriptions answered)");
                        _ = Task.Run(() => PushExtrasAsync(ssl, ct), ct);
                    }
                    break;
                case "ping": Send(ssl, "pong", id, new JsonObject()); break;
                case "status.get": Send(ssl, "status", id, Status()); break;
                case "apps.get": Send(ssl, "apps", id, Apps()); break;
                case "photos.recent": Send(ssl, "photos.recent", id, new JsonObject { ["photos"] = new JsonArray() }); break;
                case "sms.list": Send(ssl, "sms.list", id, Sms()); break;
                case "call.log": Send(ssl, "call.log", id, Calls()); break;
                case "sync.config" or "clipboard.set" or "sound.set" or "dnd.set" or "media.control" or "device.locate":
                    Send(ssl, "ok", id, new JsonObject());
                    break;
                case "channel.open":
                    var channel = (int?)payload["channel"] ?? -1;
                    Send(ssl, "ok", id, new JsonObject());
                    _ = Task.Run(() => ServeChannelAsync(channel, ct), ct);
                    break;
                // Everything else a phone would ignore too; a request the PC waits on times out there.
            }
        }
    }

    /// <summary>What a phone pushes unasked: a few notifications and something playing.</summary>
    private async Task PushExtrasAsync(SslStream ssl, CancellationToken ct)
    {
        await Task.Delay(1500, ct);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        (string App, string Pkg, string Title, string Text)[] notes =
        [
            ("Messages", "com.google.android.apps.messaging", "Thandi", "Are we still on for 7?"),
            ("Gmail", "com.google.android.gm", "Your order has shipped", "It arrives on Thursday."),
            ("WhatsApp", "com.whatsapp", "Family", "Mpho: photos from today 📷"),
        ];
        int i = 0;
        foreach (var n in notes)
        {
            Send(ssl, "notification.posted", null, new JsonObject
            {
                ["key"] = $"uishots-{i}", ["app"] = n.App, ["appPackage"] = n.Pkg, ["title"] = n.Title, ["text"] = n.Text,
                ["postedAt"] = now - (i++ * 420_000), ["existing"] = true, ["actions"] = new JsonArray(),
            });
        }
        Send(ssl, "media.state", null, new JsonObject
        {
            ["title"] = "INtombi Entsha", ["artist"] = "TNS", ["album"] = "", ["positionMs"] = 74_000, ["durationMs"] = 213_000,
            ["playing"] = true, ["appPackage"] = "com.spotify.music",
        });
    }

    private static JsonObject Status()
    {
        string[] roles =
        [
            "primary", "onPrimary", "primaryContainer", "onPrimaryContainer", "secondaryContainer", "onSecondaryContainer",
            "tertiaryContainer", "onTertiaryContainer", "surface", "onSurface", "surfaceContainer", "surfaceContainerHigh",
            "onSurfaceVariant", "outline", "outlineVariant", "error", "errorContainer", "onErrorContainer",
        ];
        // The violet scheme the design uses (Material You from the sample wallpaper).
        string[] dark = ["#CBBEFF", "#32246F", "#4A3C88", "#E6DEFF", "#4A455E", "#E7E0F7", "#6B3F52", "#FFD8E6", "#15121F", "#E6E0E9", "#211E2B", "#2B2836", "#CAC4D0", "#948F99", "#49454F", "#FFB4AB", "#93000A", "#FFDAD6"];
        string[] light = ["#6352A3", "#FFFFFF", "#E6DEFF", "#1E0F5C", "#E7E0F7", "#1D1A2B", "#FFD8E6", "#3A0B22", "#FDF8FF", "#1C1B20", "#F2ECF6", "#ECE6F0", "#49454F", "#7A757F", "#CAC4D0", "#BA1A1A", "#FFDAD6", "#410002"];
        JsonObject Theme(string[] values)
        {
            var o = new JsonObject();
            for (int i = 0; i < roles.Length; i++) o[roles[i]] = values[i];
            return o;
        }
        return new JsonObject
        {
            ["battery"] = 82, ["charging"] = true,
            ["storageFreeBytes"] = 61_000_000_000L, ["storageTotalBytes"] = 128_000_000_000L,
            ["wifiSsid"] = null, ["cpuLoad1m"] = 1.4, ["ramUsedBytes"] = 5_100_000_000L, ["ramTotalBytes"] = 8_000_000_000L,
            ["uptimeMillis"] = 302_000_000L, ["dndEnabled"] = false, ["soundMode"] = "normal",
            ["wallpaperId"] = "uishots-wallpaper-1", ["wifiSignalLevel"] = 4, ["wifiLinkSpeedMbps"] = 433,
            ["themeLight"] = Theme(light), ["themeDark"] = Theme(dark),
            ["rotationMode"] = "auto", ["brightnessAuto"] = true, ["brightnessLevel"] = 60,
        };
    }

    private static JsonObject Apps()
    {
        var apps = new JsonArray();
        foreach (var (pkg, label) in new[]
        {
            ("com.whatsapp", "WhatsApp"), ("com.spotify.music", "Spotify"), ("com.google.android.youtube", "YouTube"),
            ("com.instagram.android", "Instagram"), ("com.google.android.apps.maps", "Maps"), ("com.google.android.gm", "Gmail"),
            ("com.android.chrome", "Chrome"), ("com.google.android.GoogleCamera", "Camera"),
        })
            apps.Add(new JsonObject { ["package"] = pkg, ["label"] = label, ["versionName"] = "1.0", ["system"] = false });
        return new JsonObject { ["apps"] = apps };
    }

    private static JsonObject Sms()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["address"] = "+27 82 555 0142", ["body"] = "Are we still on for 7?", ["date"] = now - 600_000, ["incoming"] = true },
                new JsonObject { ["address"] = "+27 82 555 0142", ["body"] = "Yes! See you there", ["date"] = now - 540_000, ["incoming"] = false },
                new JsonObject { ["address"] = "Bank", ["body"] = "Your OTP is 482913", ["date"] = now - 7_200_000, ["incoming"] = true }),
        };
    }

    private static JsonObject Calls()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["calls"] = new JsonArray(
                new JsonObject { ["number"] = "+27 82 555 0142", ["type"] = "incoming", ["date"] = now - 3_600_000, ["duration"] = 312 },
                new JsonObject { ["number"] = "+27 11 555 0199", ["type"] = "missed", ["date"] = now - 9_000_000, ["duration"] = 0 }),
        };
    }

    // ---- dial-backs ------------------------------------------------------------------------------

    /// <summary>Dial back for a channel the PC asked for; only bulk (3) gets an answer.</summary>
    private async Task ServeChannelAsync(int channel, CancellationToken ct)
    {
        try
        {
            using var ssl = await DialAsync(ct);
            WriteFrame(ssl, Encoding.UTF8.GetBytes($"{{\"channel\":{channel},\"sessionToken\":\"{_token}\"}}"));
            if (channel != 3) return; // files, audio and the rest aren't faked: closing ends them
            var request = await ReadFrameAsync(ssl, ct);
            var o = request is null ? null : JsonNode.Parse(request) as JsonObject;
            var bytes = (string?)o?["kind"] == "wallpaper" ? Wallpaper() : Array.Empty<byte>();
            WriteFrame(ssl, bytes);
            await ssl.FlushAsync(ct);
            await Task.Delay(500, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log($"fake phone: channel {channel} dial-back failed ({ex.Message})");
        }
    }

    private static byte[]? _wallpaper;

    /// <summary>A portrait wallpaper as a phone sends it (720×1600 JPEG): soft violet and peach light.</summary>
    private static byte[] Wallpaper()
    {
        if (_wallpaper is not null) return _wallpaper;
        using var bmp = new Bitmap(720, 1600);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 720, 1600), Color.FromArgb(0x2E, 0x24, 0x6B), Color.FromArgb(0x15, 0x12, 0x1F), 90f))
                g.FillRectangle(bg, 0, 0, 720, 1600);
            void Glow(int cx, int cy, int r, Color c)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                using var brush = new PathGradientBrush(path) { CenterColor = c, SurroundColors = [Color.FromArgb(0, c)] };
                g.FillPath(brush, path);
            }
            Glow(560, 380, 520, Color.FromArgb(200, 0xF5, 0xB9, 0xA2));
            Glow(160, 900, 560, Color.FromArgb(190, 0x8F, 0x5A, 0x9E));
            Glow(420, 1380, 480, Color.FromArgb(170, 0x4A, 0x3C, 0x88));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return _wallpaper = ms.ToArray();
    }

    // ---- framing (4-byte big-endian length, then the bytes) ----------------------------------

    private void Send(SslStream ssl, string type, string? replyTo, JsonObject payload)
    {
        var envelope = new JsonObject
        {
            ["v"] = Version, ["type"] = type, ["id"] = Guid.NewGuid().ToString(), ["replyTo"] = replyTo, ["payload"] = payload,
        };
        WriteFrame(ssl, Encoding.UTF8.GetBytes(envelope.ToJsonString()));
    }

    private void WriteFrame(Stream stream, byte[] body)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        lock (_writeLock)
        {
            stream.Write(header);
            stream.Write(body);
            stream.Flush();
        }
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, ct)) return null;
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > 4 * 1024 * 1024) throw new IOException($"bad frame length {length}");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct)) return null;
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
