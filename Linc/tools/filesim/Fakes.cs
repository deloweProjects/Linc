using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

// Minimal stand-ins for the types TlsFileService.cs / FileServiceRouter depend on, so the real
// code compiles into this harness without dragging in WinUI, ADB, or the connection stack. Shapes
// match the app's definitions (FileService.cs, ConnectionManager.cs, LogService.cs).

public enum LogLevel { Info, Warn, Error }

public sealed class LincException(string friendlyMessage, Exception? inner = null)
    : Exception(friendlyMessage, inner);

public interface ILogService { void Log(LogLevel level, string message); }

public sealed class FakeLog : ILogService
{
    public void Log(LogLevel level, string message) { /* quiet in the harness */ }
}

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset Modified);

public sealed record StorageRoot(string Label, string Path);

public interface IFileService
{
    Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct);
    Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct);
    Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct);
    Task RenameAsync(string path, string newName, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task CreateFolderAsync(string parentPath, string name, CancellationToken ct);
    Task<string> PullScreenshotAsync(CancellationToken ct);
}

/// <summary>Only the members the router (HasAdb) and TlsFileService (OpenFileChannelAsync) touch.</summary>
public interface IConnectionManager
{
    bool HasAdb { get; }
    Task<Stream> OpenFileChannelAsync(CancellationToken ct);
}

/// <summary>
/// A tiny in-memory phone filesystem shared by BOTH transports, so a file pushed over one is
/// visible over the other — exactly the single phone the router is choosing a path to.
/// </summary>
public sealed class InMemoryPhone
{
    public const string DeniedDir = "/storage/emulated/0/denied";

    private readonly object _lock = new();
    // Full path -> (content; null means a directory), last-write unix ms.
    private readonly Dictionary<string, (byte[]? Content, long Mtime)> _nodes = new(StringComparer.Ordinal);

    public InMemoryPhone()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Mkdir("/storage", now);
        Mkdir("/storage/emulated", now);
        Mkdir("/storage/emulated/0", now);
        Mkdir("/storage/emulated/0/Download", now);
        WriteFile("/storage/emulated/0/notes.txt", "hello from the phone"u8.ToArray(), now);
        WriteFile("/storage/emulated/0/photo.jpg", [1, 2, 3, 4, 5, 6, 7, 8], now);
    }

    public void Mkdir(string path, long mtime) { lock (_lock) { _nodes[path] = (null, mtime); } }
    public void WriteFile(string path, byte[] content, long mtime) { lock (_lock) { _nodes[path] = (content, mtime); } }

    public bool Exists(string path) { lock (_lock) { return _nodes.ContainsKey(path); } }

    public byte[] Read(string path)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(path, out var node) || node.Content is null)
            {
                throw new FileNotFoundException(path);
            }
            return node.Content;
        }
    }

    public IReadOnlyList<RemoteEntry> Children(string path)
    {
        var prefix = path.TrimEnd('/') + "/";
        lock (_lock)
        {
            return _nodes
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                             && !kv.Key.AsSpan(prefix.Length).Contains('/'))
                .Select(kv => new RemoteEntry(
                    Name: kv.Key[prefix.Length..],
                    FullPath: kv.Key,
                    IsDirectory: kv.Value.Content is null,
                    Size: kv.Value.Content?.Length ?? 0,
                    Modified: DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.Mtime)))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Delete(string path)
    {
        lock (_lock)
        {
            foreach (var key in _nodes.Keys
                         .Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal))
                         .ToList())
            {
                _nodes.Remove(key);
            }
        }
    }

    public void Rename(string path, string newPath)
    {
        lock (_lock)
        {
            if (_nodes.Remove(path, out var node))
            {
                _nodes[newPath] = node;
            }
        }
    }
}

/// <summary>
/// Stands in for the real ADB-sync <c>FileService</c>, whose AdvancedSharpAdbClient transport needs
/// a live adb server + device and so can't run in a plain console (that plumbing stays covered by
/// tools/probe and the on-device checks). This models ADB <i>semantics</i> — mutations supported —
/// so the router regression can prove HasAdb=true routes here while HasAdb=false routes to Direct
/// TLS (which refuses mutations).
/// </summary>
public sealed class FileService(InMemoryPhone phone) : IFileService
{
    /// <summary>Bumped on every listing, so a test can assert the router actually picked this service.</summary>
    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StorageRoot>>([new("Internal storage", "/storage/emulated/0")]);

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult(phone.Children(path));
    }

    public Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(localDirectory);
        var local = Path.Combine(localDirectory, Path.GetFileName(remotePath));
        File.WriteAllBytes(local, phone.Read(remotePath));
        progress.Report(100);
        return Task.FromResult(local);
    }

    public Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var remote = $"{remoteDirectory.TrimEnd('/')}/{Path.GetFileName(localPath)}";
        phone.WriteFile(remote, File.ReadAllBytes(localPath), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        progress.Report(100);
        return Task.FromResult(remote);
    }

    public Task RenameAsync(string path, string newName, CancellationToken ct)
    {
        phone.Rename(path, $"{path[..path.LastIndexOf('/')]}/{newName}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        phone.Delete(path);
        return Task.CompletedTask;
    }

    public Task CreateFolderAsync(string parentPath, string name, CancellationToken ct)
    {
        phone.Mkdir($"{parentPath.TrimEnd('/')}/{name}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Task.CompletedTask;
    }

    public Task<string> PullScreenshotAsync(CancellationToken ct) => Task.FromResult("/local/screenshot.png");
}

/// <summary>
/// Runs a loopback TCP server that speaks the REAL Direct-TLS files-channel protocol, so the real
/// <see cref="TlsFileService"/> talks to it over the real <see cref="Framing"/> codec exactly as it
/// would to a phone. Each op opens a fresh connection, matching production (one connection per op).
/// </summary>
public sealed class FakeConnection : IConnectionManager, IDisposable
{
    private readonly TcpListener _listener;
    private readonly InMemoryPhone _phone;
    private readonly CancellationTokenSource _cts = new();

    public bool HasAdb { get; set; }

    public FakeConnection(InMemoryPhone phone)
    {
        _phone = phone;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task<Stream> OpenFileChannelAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, ct);
        return new NetworkStream(client.Client, ownsSocket: true);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(ct);
                _ = HandleAsync(new NetworkStream(socket, ownsSocket: true), ct);
            }
        }
        catch (Exception)
        {
            // Listener stopped on Dispose; nothing to do.
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using (stream)
            {
                var requestJson = await Framing.ReadAsync(stream, ct);
                if (requestJson is null)
                {
                    return;
                }
                var request = JsonNode.Parse(requestJson)!.AsObject();
                var op = (string?)request["op"];
                var path = (string?)request["path"] ?? "";
                switch (op)
                {
                    case "list":
                        if (path.StartsWith(InMemoryPhone.DeniedDir, StringComparison.Ordinal))
                        {
                            // Exercises the 'not-granted' -> plain-language mapping in TlsFileService.
                            await WriteJson(stream, new JsonObject { ["error"] = "not-granted" }, ct);
                            return;
                        }
                        var entries = new JsonArray();
                        foreach (var entry in _phone.Children(path))
                        {
                            entries.Add(new JsonObject
                            {
                                ["name"] = entry.Name,
                                ["dir"] = entry.IsDirectory,
                                ["size"] = entry.Size,
                                ["modified"] = entry.Modified.ToUnixTimeMilliseconds(),
                            });
                        }
                        await WriteJson(stream, new JsonObject { ["entries"] = entries }, ct);
                        break;

                    case "pull":
                        var bytes = _phone.Read(path);
                        await WriteJson(stream, new JsonObject { ["size"] = bytes.Length }, ct);
                        await stream.WriteAsync(bytes, ct);
                        await stream.FlushAsync(ct);
                        break;

                    case "push":
                        var size = (long?)request["size"] ?? 0;
                        var buffer = new byte[size];
                        await stream.ReadExactlyAsync(buffer, ct);
                        _phone.WriteFile(path, buffer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        await WriteJson(stream, new JsonObject { ["ok"] = true }, ct);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Client went away or teardown; the harness closes connections aggressively.
        }
    }

    private static Task WriteJson(Stream stream, JsonObject obj, CancellationToken ct) =>
        Framing.WriteAsync(stream, obj.ToJsonString(), ct);

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
