using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Location.Nearby.Connections;
using Sharing.Nearby;
using NcV1 = Location.Nearby.Connections.V1Frame;
using NcResponse = Location.Nearby.Connections.ConnectionResponseFrame;
using ShV1 = Sharing.Nearby.V1Frame;
using ShResponse = Sharing.Nearby.ConnectionResponseFrame;

namespace Linc.Desktop.QuickShare;

/// <summary>What a sender is offering, as the receiver's prompt shows it.</summary>
public sealed record QsOffer(
    IReadOnlyList<FileMetadata> Files,
    IReadOnlyList<TextMetadata> Texts,
    IReadOnlyList<WifiCredentialsMetadata> Wifi)
{
    public long TotalBytes => Files.Sum(f => f.Size) + Texts.Sum(t => t.Size);

    /// <summary>One line per item, in plain language.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var f in Files)
        {
            yield return f.Name;
        }
        foreach (var t in Texts)
        {
            yield return t.Type == TextMetadata.Types.Type.Url ? $"Link: {t.TextTitle}" : $"Text: {t.TextTitle}";
        }
        foreach (var w in Wifi)
        {
            yield return $"Wi-Fi network: {w.Ssid}";
        }
    }
}

/// <summary>A text item that arrived (plain text, a link, an address, a phone number).</summary>
public sealed record QsReceivedText(TextMetadata.Types.Type Kind, string Text);

/// <summary>
/// One Quick Share connection, either direction (see NearDrop's PROTOCOL.md for the sequence).
/// Owns the socket. Every negotiation message after the key exchange is a Sharing frame carried
/// as a BYTES payload inside an encrypted Nearby Connections offline frame.
/// </summary>
public sealed class QsConnection : IAsyncDisposable
{
    private const int ChunkSize = 512 * 1024;
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, MemoryStream> _bytesPayloads = [];
    private readonly Dictionary<long, FileSink> _fileSinks = [];
    private readonly CancellationTokenSource _life = new();
    private QsSecureChannel? _channel;
    private Task? _keepAlive;

    public string Pin { get; private set; } = "";
    public string? RemoteName { get; private set; }
    public QsWire.DeviceType RemoteType { get; private set; }

    /// <summary>Bytes written into received files so far; progress for the UI.</summary>
    public long ReceivedBytes { get; private set; }

    private QsConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (_keepAlive is not null)
        {
            try { await _keepAlive; } catch (OperationCanceledException) { /* stopping it is the point */ }
        }
        foreach (var sink in _fileSinks.Values)
        {
            await sink.Stream.DisposeAsync();
        }
        _client.Dispose();
        _life.Dispose();
    }

    // =========================================================================================
    // Receiver (server) side
    // =========================================================================================

    /// <summary>Runs the handshake as the receiver, up to the sender's Introduction.</summary>
    public static async Task<(QsConnection Connection, QsOffer Offer)> AcceptAsync(TcpClient client, CancellationToken ct)
    {
        var c = new QsConnection(client);
        try
        {
            var request = OfflineFrame.Parser.ParseFrom(await QsFraming.ReadAsync(c._stream, ct));
            if (request.V1?.Type != NcV1.Types.FrameType.ConnectionRequest)
            {
                throw new InvalidDataException("Quick Share: the sender didn't open with a connection request.");
            }
            (c.RemoteName, c.RemoteType) = QsWire.ParseEndpointInfo(request.V1.ConnectionRequest.EndpointInfo.Span);
            c.RemoteName ??= request.V1.ConnectionRequest.EndpointName;

            using var ukey = new QsUkey2();
            var serverInit = ukey.HandleClientInit(await QsFraming.ReadAsync(c._stream, ct));
            await QsFraming.WriteAsync(c._stream, serverInit, ct);
            var channel = ukey.HandleClientFinish(await QsFraming.ReadAsync(c._stream, ct), out var pin);
            c.Pin = pin;

            // Both sides send a plaintext connection response; everything after is encrypted.
            var theirResponse = OfflineFrame.Parser.ParseFrom(await QsFraming.ReadAsync(c._stream, ct));
            if (theirResponse.V1?.ConnectionResponse?.Response != NcResponse.Types.ResponseStatus.Accept)
            {
                throw new InvalidDataException("Quick Share: the sender withdrew the connection.");
            }
            await QsFraming.WriteAsync(c._stream, ConnectionResponse().ToByteArray(), ct);
            c._channel = channel;
            c.StartKeepAlive();

            await c.SendSharingAsync(PairedKeyEncryption(), ct);
            await c.ReceiveSharingAsync(ShV1.Types.FrameType.PairedKeyEncryption, ct);
            await c.SendSharingAsync(PairedKeyResult(), ct);
            await c.ReceiveSharingAsync(ShV1.Types.FrameType.PairedKeyResult, ct);
            var intro = (await c.ReceiveSharingAsync(ShV1.Types.FrameType.Introduction, ct)).Introduction;
            return (c, new QsOffer(intro.FileMetadata.ToList(), intro.TextMetadata.ToList(), intro.WifiCredentialsMetadata.ToList()));
        }
        catch
        {
            await c.DisposeAsync();
            throw;
        }
    }

    /// <summary>Tells the sender no, politely, so its screen says "declined" rather than "failed".</summary>
    public Task RejectAsync(CancellationToken ct) =>
        SendSharingAsync(Response(ShResponse.Types.Status.Reject), ct);

    /// <summary>
    /// Accepts and receives everything in <paramref name="offer"/>. Files land in
    /// <paramref name="folder"/> under non-clashing names; texts are returned.
    /// </summary>
    public async Task<(List<string> Files, List<QsReceivedText> Texts)> ReceiveAsync(
        QsOffer offer, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        foreach (var file in offer.Files)
        {
            var path = UniquePath(folder, SafeName(file.Name));
            _fileSinks[file.PayloadId] = new FileSink(path, File.Create(path));
            files.Add(path);
        }
        var textIds = offer.Texts.ToDictionary(t => t.PayloadId);
        var texts = new List<QsReceivedText>();
        var pending = new HashSet<long>(_fileSinks.Keys.Concat(textIds.Keys));

        await SendSharingAsync(Response(ShResponse.Types.Status.Accept), ct);

        while (pending.Count > 0)
        {
            var frame = await ReceiveOfflineAsync(ct);
            switch (frame.V1?.Type)
            {
                case NcV1.Types.FrameType.PayloadTransfer:
                    var transfer = frame.V1.PayloadTransfer;
                    var id = transfer.PayloadHeader?.Id ?? 0;
                    if (_fileSinks.TryGetValue(id, out var sink))
                    {
                        var chunk = transfer.PayloadChunk;
                        if (chunk is null)
                        {
                            break;
                        }
                        if (chunk.Body.Length > 0)
                        {
                            sink.Stream.Position = chunk.Offset;
                            await sink.Stream.WriteAsync(chunk.Body.Memory, ct);
                            ReceivedBytes += chunk.Body.Length;
                            progress?.Report(ReceivedBytes);
                        }
                        if ((chunk.Flags & 1) != 0)
                        {
                            await sink.Stream.DisposeAsync();
                            _fileSinks.Remove(id);
                            pending.Remove(id);
                        }
                    }
                    else if (AccumulateBytes(transfer) is { } payload)
                    {
                        if (textIds.TryGetValue(id, out var meta))
                        {
                            texts.Add(new QsReceivedText(meta.Type, System.Text.Encoding.UTF8.GetString(payload)));
                            pending.Remove(id);
                        }
                        else if (TryParseSharing(payload) is { V1.Type: ShV1.Types.FrameType.Cancel })
                        {
                            throw new OperationCanceledException("The sender cancelled the transfer.");
                        }
                    }
                    break;
                case NcV1.Types.FrameType.Disconnection:
                    throw new IOException("The sender disconnected before everything arrived.");
            }
        }
        await SendDisconnectionAsync(ct);
        return (files, texts);
    }

    // =========================================================================================
    // Sender (client) side
    // =========================================================================================

    /// <summary>Connects to a receiver and runs the handshake, up to the point of introducing files.</summary>
    public static async Task<QsConnection> ConnectAsync(
        System.Net.IPEndPoint endpoint, string myName, CancellationToken ct)
    {
        var client = new TcpClient();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(endpoint, connectTimeout.Token);
        }
        var c = new QsConnection(client);
        try
        {
            var endpointId = QsWire.NewEndpointId();
            var request = new OfflineFrame
            {
                Version = OfflineFrame.Types.Version.V1,
                V1 = new NcV1
                {
                    Type = NcV1.Types.FrameType.ConnectionRequest,
                    ConnectionRequest = new ConnectionRequestFrame
                    {
                        EndpointId = endpointId,
                        EndpointName = myName,
                        EndpointInfo = ByteString.CopyFrom(QsWire.EndpointInfo(myName, QsWire.DeviceType.Laptop)),
                        Mediums = { ConnectionRequestFrame.Types.Medium.WifiLan },
                    },
                },
            };
            await QsFraming.WriteAsync(c._stream, request.ToByteArray(), ct);

            using var ukey = new QsUkey2();
            await QsFraming.WriteAsync(c._stream, ukey.BuildClientInit(), ct);
            var (finish, channel) = ukey.HandleServerInit(await QsFraming.ReadAsync(c._stream, ct), out var pin);
            c.Pin = pin;
            await QsFraming.WriteAsync(c._stream, finish, ct);
            await QsFraming.WriteAsync(c._stream, ConnectionResponse().ToByteArray(), ct);
            var theirResponse = OfflineFrame.Parser.ParseFrom(await QsFraming.ReadAsync(c._stream, ct));
            if (theirResponse.V1?.ConnectionResponse?.Response != NcResponse.Types.ResponseStatus.Accept)
            {
                throw new InvalidDataException("The device refused the Quick Share connection.");
            }
            c._channel = channel;
            c.StartKeepAlive();

            await c.SendSharingAsync(PairedKeyEncryption(), ct);
            await c.ReceiveSharingAsync(ShV1.Types.FrameType.PairedKeyEncryption, ct);
            await c.SendSharingAsync(PairedKeyResult(), ct);
            await c.ReceiveSharingAsync(ShV1.Types.FrameType.PairedKeyResult, ct);
            return c;
        }
        catch
        {
            await c.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Offers <paramref name="paths"/>, waits for the person at the other end to accept (the
    /// phone shows the same PIN as <see cref="Pin"/>), then streams them. Returns false when
    /// they declined.
    /// </summary>
    public Task<bool> SendFilesAsync(IReadOnlyList<string> paths, IProgress<long>? progress, CancellationToken ct) =>
        SendAsync(paths, null, progress, ct);

    /// <summary>
    /// Offers a piece of text — a link is recognised and offered as one, so Android shows
    /// "Open" rather than "Copy". Same accept flow as files.
    /// </summary>
    public Task<bool> SendTextAsync(string text, CancellationToken ct) => SendAsync([], text, null, ct);

    private async Task<bool> SendAsync(
        IReadOnlyList<string> paths, string? text, IProgress<long>? progress, CancellationToken ct)
    {
        var intro = new IntroductionFrame();
        byte[]? textBytes = null;
        long textPayloadId = 0;
        if (!string.IsNullOrEmpty(text))
        {
            textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            textPayloadId = NewPayloadId();
            var trimmed = text.Trim();
            var isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            intro.TextMetadata.Add(new TextMetadata
            {
                // Android previews the title, so it is the text itself (bounded, like Android's own).
                TextTitle = trimmed.Length > 100 ? trimmed[..100] + "…" : trimmed,
                Type = isUrl ? TextMetadata.Types.Type.Url : TextMetadata.Types.Type.Text,
                PayloadId = textPayloadId,
                Size = textBytes.Length,
                Id = NewPayloadId(),
            });
        }
        var payloads = new List<(long Id, string Path, long Size)>();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            var id = NewPayloadId();
            payloads.Add((id, path, info.Length));
            intro.FileMetadata.Add(new FileMetadata
            {
                Name = info.Name,
                Size = info.Length,
                PayloadId = id,
                Id = NewPayloadId(),
                MimeType = MimeFor(info.Extension),
                Type = TypeFor(info.Extension),
            });
        }
        await SendSharingAsync(new Frame
        {
            Version = Frame.Types.Version.V1,
            V1 = new ShV1 { Type = ShV1.Types.FrameType.Introduction, Introduction = intro },
        }, ct);

        // The receiver is now showing its accept prompt; people take their time.
        using var answerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answerTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        var answer = await ReceiveSharingAsync(ShV1.Types.FrameType.Response, answerTimeout.Token);
        if (answer.ConnectionResponse.Status != ShResponse.Types.Status.Accept)
        {
            return false;
        }

        // The receiver keeps sending keep-alives while we stream; drain and answer them so its
        // socket buffer never fills and a mid-transfer cancel is noticed.
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drain = DrainWhileSendingAsync(streaming);
        long sent = 0;
        var buffer = new byte[ChunkSize];
        try
        {
            if (textBytes is not null)
            {
                var bytesType = PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes;
                await SendPayloadChunkAsync(textPayloadId, bytesType, textBytes.Length, 0, textBytes,
                    last: false, null, streaming.Token);
                await SendPayloadChunkAsync(textPayloadId, bytesType, textBytes.Length, textBytes.Length,
                    ReadOnlyMemory<byte>.Empty, last: true, null, streaming.Token);
            }
            foreach (var (id, path, size) in payloads)
            {
                await using var file = File.OpenRead(path);
                long offset = 0;
                int read;
                while ((read = await file.ReadAsync(buffer, streaming.Token)) > 0)
                {
                    await SendPayloadChunkAsync(id, PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.File,
                        size, offset, buffer.AsMemory(0, read), last: false, Path.GetFileName(path), streaming.Token);
                    offset += read;
                    sent += read;
                    progress?.Report(sent);
                }
                await SendPayloadChunkAsync(id, PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.File,
                    size, offset, ReadOnlyMemory<byte>.Empty, last: true, Path.GetFileName(path), streaming.Token);
            }
        }
        finally
        {
            streaming.Cancel();
            try { await drain; } catch (OperationCanceledException) { /* drain ends with the stream */ }
        }
        if (drain.IsCompletedSuccessfully && drain.Result is { } problem)
        {
            throw new IOException(problem);
        }
        await SendDisconnectionAsync(ct);
        return true;
    }

    private async Task<string?> DrainWhileSendingAsync(CancellationTokenSource streaming)
    {
        try
        {
            while (!streaming.IsCancellationRequested)
            {
                var frame = await ReceiveOfflineAsync(streaming.Token);
                if (frame.V1?.Type == NcV1.Types.FrameType.Disconnection)
                {
                    streaming.Cancel();
                    return "The device disconnected during the transfer.";
                }
                if (frame.V1?.Type == NcV1.Types.FrameType.PayloadTransfer &&
                    AccumulateBytes(frame.V1.PayloadTransfer) is { } bytes &&
                    TryParseSharing(bytes) is { V1.Type: ShV1.Types.FrameType.Cancel })
                {
                    streaming.Cancel();
                    return "The person on the other device cancelled the transfer.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            // The socket closing under us is how most receivers end a finished transfer.
        }
        return null;
    }

    // =========================================================================================
    // Framing helpers
    // =========================================================================================

    private void StartKeepAlive()
    {
        var ct = _life.Token;
        _keepAlive = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveEvery, ct);
                try
                {
                    await SendOfflineAsync(KeepAlive(ack: false), ct);
                }
                catch (IOException)
                {
                    return; // the connection is gone; the main flow reports that
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, ct);
    }

    private async Task SendOfflineAsync(OfflineFrame frame, CancellationToken ct)
    {
        var bytes = frame.ToByteArray();
        await _writeLock.WaitAsync(ct);
        try
        {
            await QsFraming.WriteAsync(_stream, _channel is null ? bytes : _channel.Seal(bytes), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Next non-keep-alive offline frame; keep-alives are answered here.</summary>
    private async Task<OfflineFrame> ReceiveOfflineAsync(CancellationToken ct)
    {
        while (true)
        {
            var raw = await QsFraming.ReadAsync(_stream, ct);
            var frame = OfflineFrame.Parser.ParseFrom(_channel is null ? raw : _channel.Open(raw));
            if (frame.V1?.Type == NcV1.Types.FrameType.KeepAlive)
            {
                if (frame.V1.KeepAlive is not { Ack: true })
                {
                    await SendOfflineAsync(KeepAlive(ack: true), ct);
                }
                continue;
            }
            return frame;
        }
    }

    /// <summary>Sends a Sharing frame the way Android does: the whole body, then an empty last chunk.</summary>
    private async Task SendSharingAsync(Frame frame, CancellationToken ct)
    {
        var body = frame.ToByteArray();
        var id = NewPayloadId();
        var type = PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes;
        await SendPayloadChunkAsync(id, type, body.Length, 0, body, last: false, null, ct);
        await SendPayloadChunkAsync(id, type, body.Length, body.Length, ReadOnlyMemory<byte>.Empty, last: true, null, ct);
    }

    private Task SendPayloadChunkAsync(long id, PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType type,
        long totalSize, long offset, ReadOnlyMemory<byte> body, bool last, string? fileName, CancellationToken ct)
    {
        var header = new PayloadTransferFrame.Types.PayloadHeader { Id = id, Type = type, TotalSize = totalSize };
        if (fileName is not null)
        {
            header.FileName = fileName;
        }
        return SendOfflineAsync(new OfflineFrame
        {
            Version = OfflineFrame.Types.Version.V1,
            V1 = new NcV1
            {
                Type = NcV1.Types.FrameType.PayloadTransfer,
                PayloadTransfer = new PayloadTransferFrame
                {
                    PacketType = PayloadTransferFrame.Types.PacketType.Data,
                    PayloadHeader = header,
                    PayloadChunk = new PayloadTransferFrame.Types.PayloadChunk
                    {
                        Offset = offset,
                        Flags = last ? 1 : 0,
                        Body = ByteString.CopyFrom(body.Span),
                    },
                },
            },
        }, ct);
    }

    /// <summary>Pumps until a complete Sharing frame of <paramref name="want"/> arrives.</summary>
    private async Task<ShV1> ReceiveSharingAsync(ShV1.Types.FrameType want, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReceiveOfflineAsync(ct);
            if (frame.V1?.Type == NcV1.Types.FrameType.Disconnection)
            {
                throw new IOException("The other device closed the Quick Share connection.");
            }
            if (frame.V1?.Type != NcV1.Types.FrameType.PayloadTransfer ||
                AccumulateBytes(frame.V1.PayloadTransfer) is not { } bytes ||
                TryParseSharing(bytes) is not { V1: { } v1 })
            {
                continue;
            }
            if (v1.Type == want)
            {
                return v1;
            }
            if (v1.Type == ShV1.Types.FrameType.Cancel)
            {
                throw new OperationCanceledException("The other device cancelled.");
            }
        }
    }

    /// <summary>Collects a BYTES payload; returns it once its last chunk arrives, else null.</summary>
    private byte[]? AccumulateBytes(PayloadTransferFrame transfer)
    {
        if (transfer.PayloadHeader is not { Type: PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes } header ||
            transfer.PayloadChunk is not { } chunk)
        {
            return null;
        }
        if (!_bytesPayloads.TryGetValue(header.Id, out var buffer))
        {
            if (header.TotalSize > QsFraming.MaxFrame)
            {
                throw new InvalidDataException("Quick Share: a control message claimed an absurd size.");
            }
            buffer = new MemoryStream();
            _bytesPayloads[header.Id] = buffer;
        }
        if (chunk.Body.Length > 0)
        {
            buffer.Position = chunk.Offset;
            buffer.Write(chunk.Body.Span);
        }
        if ((chunk.Flags & 1) == 0)
        {
            return null;
        }
        _bytesPayloads.Remove(header.Id);
        return buffer.ToArray();
    }

    private static Frame? TryParseSharing(byte[] bytes)
    {
        try
        {
            return Frame.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return null; // a text payload, not a control message
        }
    }

    private async Task SendDisconnectionAsync(CancellationToken ct)
    {
        try
        {
            await SendOfflineAsync(new OfflineFrame
            {
                Version = OfflineFrame.Types.Version.V1,
                V1 = new NcV1 { Type = NcV1.Types.FrameType.Disconnection, Disconnection = new DisconnectionFrame() },
            }, ct);
        }
        catch (IOException)
        {
            // Already closed from the other side — which is also "done".
        }
    }

    // ---- frame builders ---------------------------------------------------------------------

    private static OfflineFrame ConnectionResponse() => new()
    {
        Version = OfflineFrame.Types.Version.V1,
        V1 = new NcV1
        {
            Type = NcV1.Types.FrameType.ConnectionResponse,
            ConnectionResponse = new NcResponse
            {
                Response = NcResponse.Types.ResponseStatus.Accept,
                Status = 0,
                OsInfo = new OsInfo { Type = OsInfo.Types.OsType.Windows },
            },
        },
    };

    private static OfflineFrame KeepAlive(bool ack) => new()
    {
        Version = OfflineFrame.Types.Version.V1,
        V1 = new NcV1 { Type = NcV1.Types.FrameType.KeepAlive, KeepAlive = new KeepAliveFrame { Ack = ack } },
    };

    /// <summary>
    /// Contacts-only visibility needs certificates from Google's servers; everyone-mode peers
    /// exchange random bytes here and both carry on (PROTOCOL.md, "paired key encryption").
    /// </summary>
    private static Frame PairedKeyEncryption() => new()
    {
        Version = Frame.Types.Version.V1,
        V1 = new ShV1
        {
            Type = ShV1.Types.FrameType.PairedKeyEncryption,
            PairedKeyEncryption = new Sharing.Nearby.PairedKeyEncryptionFrame
            {
                SecretIdHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(6)),
                SignedData = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(72)),
            },
        },
    };

    private static Frame PairedKeyResult() => new()
    {
        Version = Frame.Types.Version.V1,
        V1 = new ShV1
        {
            Type = ShV1.Types.FrameType.PairedKeyResult,
            PairedKeyResult = new PairedKeyResultFrame { Status = PairedKeyResultFrame.Types.Status.Unable },
        },
    };

    private static Frame Response(ShResponse.Types.Status status) => new()
    {
        Version = Frame.Types.Version.V1,
        V1 = new ShV1 { Type = ShV1.Types.FrameType.Response, ConnectionResponse = new ShResponse { Status = status } },
    };

    private static long NewPayloadId() => RandomNumberGenerator.GetInt32(1, int.MaxValue) * 4096L + RandomNumberGenerator.GetInt32(4096);

    // ---- file naming ------------------------------------------------------------------------

    /// <summary>The sender chooses the name; never let it choose the folder.</summary>
    public static string SafeName(string? name)
    {
        // Take the last path segment by hand: Path.GetFileName would read "a:b" as a drive.
        var leaf = (name ?? "").Replace('\\', '/').Split('/').Last();
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            leaf = leaf.Replace(bad, '_');
        }
        leaf = leaf.Trim().TrimEnd('.');
        return leaf.Length == 0 || leaf is "." or ".." ? "Quick Share file" : leaf;
    }

    public static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        }
        return path;
    }

    private static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        ".apk" => "application/vnd.android.package-archive",
        ".doc" or ".docx" => "application/msword",
        _ => "application/octet-stream",
    };

    private static FileMetadata.Types.Type TypeFor(string ext) => MimeFor(ext) switch
    {
        var m when m.StartsWith("image/") => FileMetadata.Types.Type.Image,
        var m when m.StartsWith("video/") => FileMetadata.Types.Type.Video,
        var m when m.StartsWith("audio/") => FileMetadata.Types.Type.Audio,
        "application/vnd.android.package-archive" => FileMetadata.Types.Type.AndroidApp,
        "application/octet-stream" => FileMetadata.Types.Type.Unknown,
        _ => FileMetadata.Types.Type.Document,
    };

    private sealed record FileSink(string Path, FileStream Stream);
}
