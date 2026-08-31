using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Linc.Probe;

/// <summary>
/// A desktop-emulating control connection to the phone's companion service, reached the same
/// way Linc Desktop reaches it: `adb forward tcp:0 localabstract:linc`, then TCP to loopback.
/// Replies are matched by `replyTo`; anything unsolicited lands in <see cref="Unsolicited"/>.
/// </summary>
public sealed class Link : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly int _localPort;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();

    public int NegotiatedVersion { get; private set; }
    public string? SessionToken { get; private set; }
    public string PhoneAppVersion { get; private set; } = "?";
    public ConcurrentQueue<JsonObject> Unsolicited { get; } = new();

    private Link(TcpClient tcp, int localPort)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _localPort = localPort;
    }

    public static string Adb =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");

    public static string RunAdb(string arguments, string? serial = null)
    {
        var args = serial is null ? arguments : $"-s {serial} {arguments}";
        using var process = Process.Start(new ProcessStartInfo(Adb, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    /// <summary>Forwards a fresh local port to `localabstract:linc`, connects, and handshakes.</summary>
    public static async Task<Link> OpenAsync(string? serial, int maxVersion, CancellationToken ct = default)
    {
        var forwarded = RunAdb("forward tcp:0 localabstract:linc", serial);
        if (!int.TryParse(forwarded.Split('\n')[^1].Trim(), out var port))
        {
            throw new InvalidOperationException($"adb forward did not return a port: {forwarded}");
        }

        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var link = new Link(tcp, port);
        _ = link.ReceiveLoopAsync();
        await link.HandshakeAsync(maxVersion, ct);
        return link;
    }

    private async Task HandshakeAsync(int maxVersion, CancellationToken ct)
    {
        var reply = await RequestAsync("hello", new JsonObject
        {
            ["app"] = "linc-desktop",
            ["appVersion"] = "probe",
            ["minV"] = 0,
            ["maxV"] = maxVersion,
        }, ct);
        var payload = reply["payload"]!.AsObject();
        NegotiatedVersion = (int?)payload["v"] ?? 0;
        SessionToken = (string?)payload["sessionToken"];
        PhoneAppVersion = (string?)payload["appVersion"] ?? "?";
    }

    /// <summary>Sends a request and awaits the frame whose `replyTo` matches it.</summary>
    public async Task<JsonObject> RequestAsync(
        string type, JsonObject payload, CancellationToken ct = default, int timeoutSeconds = 10)
    {
        var id = Guid.NewGuid().ToString();
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var envelope = new JsonObject
        {
            ["v"] = NegotiatedVersion,
            ["type"] = type,
            ["id"] = id,
            ["replyTo"] = null,
            ["payload"] = payload,
        };
        await Framing.WriteAsync(_stream, envelope.ToJsonString(), ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await using var registration = timeout.Token.Register(() =>
            waiter.TrySetException(new TimeoutException($"No reply to '{type}' within {timeoutSeconds}s.")));
        return await waiter.Task;
    }

    /// <summary>Fire-and-forget send (no reply expected).</summary>
    public Task SendAsync(string type, JsonObject payload, CancellationToken ct = default) =>
        Framing.WriteAsync(_stream, new JsonObject
        {
            ["v"] = NegotiatedVersion,
            ["type"] = type,
            ["id"] = Guid.NewGuid().ToString(),
            ["replyTo"] = null,
            ["payload"] = payload,
        }.ToJsonString(), ct);

    /// <summary>Opens a typed channel over ADB by dialing the same forwarded port with a header.</summary>
    public async Task<Stream> OpenChannelAsync(int channel, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _localPort, ct);
        var stream = tcp.GetStream();
        var header = new JsonObject { ["channel"] = channel, ["sessionToken"] = SessionToken };
        await Framing.WriteAsync(stream, header.ToJsonString(), ct);
        return stream;
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var json = await Framing.ReadAsync(_stream);
                if (JsonNode.Parse(json) is not JsonObject frame)
                {
                    continue;
                }
                var replyTo = (string?)frame["replyTo"];
                if (replyTo is not null && _pending.TryRemove(replyTo, out var waiter))
                {
                    waiter.TrySetResult(frame);
                }
                else
                {
                    Unsolicited.Enqueue(frame);
                }
            }
        }
        catch
        {
            // Link closed; outstanding waiters fail on their own timeout.
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
