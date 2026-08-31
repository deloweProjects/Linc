using System.IO;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// Files over the Direct TLS files channel (2) — the ADB-free path (PROTOCOL.md v10,
/// D-024). Browse + transfer only; file mutations (rename/delete/new folder) and
/// screenshots still need an ADB link and say so. The desktop's <see cref="FileServiceRouter"/>
/// picks this when there's no ADB.
/// </summary>
public sealed class TlsFileService(IConnectionManager connection, ILogService log) : IFileService
{
    public Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct) =>
        // SD-card discovery needs a shell; over TLS we offer internal storage and let the
        // user browse from there.
        Task.FromResult<IReadOnlyList<StorageRoot>>([new("Internal storage", "/storage/emulated/0")]);

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct)
    {
        using var stream = await connection.OpenFileChannelAsync(ct);
        await WriteJsonAsync(stream, new JsonObject { ["op"] = "list", ["path"] = path }, ct);
        var header = await ReadJsonAsync(stream, ct);
        ThrowIfError(header);
        var entries = new List<RemoteEntry>();
        if (header["entries"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject o || (string?)o["name"] is not { } name)
                {
                    continue;
                }
                entries.Add(new RemoteEntry(
                    Name: name,
                    FullPath: $"{path.TrimEnd('/')}/{name}",
                    IsDirectory: (bool?)o["dir"] ?? false,
                    Size: (long?)o["size"] ?? 0,
                    Modified: DateTimeOffset.FromUnixTimeMilliseconds((long?)o["modified"] ?? 0)));
            }
        }
        return entries;
    }

    public async Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var localPath = UniqueLocalPath(Path.Combine(localDirectory, Path.GetFileName(remotePath)));
        using var stream = await connection.OpenFileChannelAsync(ct);
        await WriteJsonAsync(stream, new JsonObject { ["op"] = "pull", ["path"] = remotePath }, ct);
        var header = await ReadJsonAsync(stream, ct);
        ThrowIfError(header);
        var size = (long?)header["size"] ?? 0;
        await using (var file = File.Create(localPath))
        {
            var buffer = new byte[64 * 1024];
            long received = 0;
            while (received < size)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - received)), ct);
                if (read <= 0)
                {
                    throw new LincException($"The transfer of {Path.GetFileName(remotePath)} was cut short.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress.Report(size > 0 ? received * 100.0 / size : 100);
            }
        }
        log.Log(LogLevel.Info, $"Downloaded {Path.GetFileName(remotePath)} (direct)");
        return localPath;
    }

    public async Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(localPath);
        var remotePath = $"{remoteDirectory.TrimEnd('/')}/{fileName}";
        var size = new FileInfo(localPath).Length;
        using var stream = await connection.OpenFileChannelAsync(ct);
        await WriteJsonAsync(stream, new JsonObject { ["op"] = "push", ["path"] = remotePath, ["size"] = size }, ct);
        await using (var file = File.OpenRead(localPath))
        {
            var buffer = new byte[64 * 1024];
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read;
                progress.Report(size > 0 ? sent * 100.0 / size : 100);
            }
            await stream.FlushAsync(ct);
        }
        var reply = await ReadJsonAsync(stream, ct);
        ThrowIfError(reply);
        log.Log(LogLevel.Info, $"Uploaded {fileName} (direct)");
        return remotePath;
    }

    public Task RenameAsync(string path, string newName, CancellationToken ct) => throw NeedsAdb();
    public Task DeleteAsync(string path, CancellationToken ct) => throw NeedsAdb();
    public Task CreateFolderAsync(string parentPath, string name, CancellationToken ct) => throw NeedsAdb();
    public Task<string> PullScreenshotAsync(CancellationToken ct) => throw NeedsAdb();

    private static LincException NeedsAdb() => new(
        "That needs a USB or wireless-debugging connection. Turn one on and try again.");

    private static async Task WriteJsonAsync(Stream stream, JsonObject obj, CancellationToken ct) =>
        await Framing.WriteAsync(stream, obj.ToJsonString(), ct);

    private static async Task<JsonObject> ReadJsonAsync(Stream stream, CancellationToken ct)
    {
        var json = await Framing.ReadAsync(stream, ct)
            ?? throw new LincException("The phone closed the file connection unexpectedly.");
        return JsonNode.Parse(json) as JsonObject ?? [];
    }

    private static void ThrowIfError(JsonObject header)
    {
        if ((string?)header["error"] is { } error)
        {
            throw new LincException(error == "not-granted"
                ? "Linc on the phone needs the “All files access” permission for direct file transfer. " +
                  "Grant it on the phone's Settings screen (or connect over USB/wireless debugging)."
                : $"The phone couldn't do that ({error}).");
        }
    }

    private static string UniqueLocalPath(string desired)
    {
        if (!File.Exists(desired))
        {
            return desired;
        }
        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>Routes file operations to ADB sync or Direct TLS by the live transport (D-024).</summary>
public sealed class FileServiceRouter(FileService adb, TlsFileService tls, IConnectionManager connection) : IFileService
{
    private IFileService Active => connection.HasAdb ? adb : tls;

    public Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct) => Active.GetRootsAsync(ct);
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Active.ListAsync(path, ct);
    public Task<string> PullAsync(string r, string l, IProgress<double> p, CancellationToken ct) => Active.PullAsync(r, l, p, ct);
    public Task<string> PushAsync(string l, string r, IProgress<double> p, CancellationToken ct) => Active.PushAsync(l, r, p, ct);
    public Task RenameAsync(string path, string newName, CancellationToken ct) => Active.RenameAsync(path, newName, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => Active.DeleteAsync(path, ct);
    public Task CreateFolderAsync(string parent, string name, CancellationToken ct) => Active.CreateFolderAsync(parent, name, ct);
    public Task<string> PullScreenshotAsync(CancellationToken ct) => Active.PullScreenshotAsync(ct);
}
