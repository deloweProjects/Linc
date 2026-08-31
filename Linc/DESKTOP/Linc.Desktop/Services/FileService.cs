using System.IO;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace Linc.Desktop.Services;

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset Modified);

public sealed record StorageRoot(string Label, string Path);

public interface IFileService
{
    Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct);

    /// <returns>The local path actually written (renamed if a conflict existed).</returns>
    Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct);

    /// <returns>The remote path actually written (renamed if a conflict existed).</returns>
    Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct);

    Task RenameAsync(string path, string newName, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task CreateFolderAsync(string parentPath, string name, CancellationToken ct);

    /// <summary>Takes a screenshot on the phone and saves it under Pictures\Linc (v7 quick action; pure ADB, no protocol).</summary>
    /// <returns>The local file written.</returns>
    Task<string> PullScreenshotAsync(CancellationToken ct);
}

/// <summary>
/// Phone filesystem access over ADB: listings and transfers via the sync protocol,
/// mutations via quoted shell commands. All failures map to plain-language messages.
/// </summary>
public sealed class FileService(IConnectionManager connection, ILogService log) : IFileService
{
    private readonly AdbClient _adb = new();

    private DeviceData Device => connection.RawDevice
        ?? throw new LincException(connection.Current is null
            ? "Not connected to a phone right now."
            : "Files need a USB or wireless-debugging connection — the current direct link can't carry them yet.");

    public async Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct)
    {
        var roots = new List<StorageRoot> { new("Internal storage", "/storage/emulated/0") };
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync("ls -1 /storage", Device, receiver, ct);
            foreach (var line in receiver.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line is not ("emulated" or "self") && !line.Contains("Permission denied"))
                {
                    roots.Add(new StorageRoot($"SD card ({line})", $"/storage/{line}"));
                }
            }
        }
        catch (Exception ex) when (ex is not (LincException or OperationCanceledException))
        {
            // SD detection is best effort; internal storage always works.
        }
        // Over ADB the shell user can read much of the tree, so offer the device root as a jump
        // target too (M2a) — this is what makes the whole readable filesystem reachable in one
        // click, not just by repeatedly pressing Up. Kept last so internal storage stays default.
        roots.Add(new StorageRoot("Phone root (/)", "/"));
        return roots;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct)
    {
        try
        {
            using var sync = new SyncService(_adb, Device);
            var listing = await sync.GetDirectoryListingAsync(path, ct);
            var entries = listing
                .Where(stat => stat.Path is not ("." or ".."))
                .Select(stat => new RemoteEntry(
                    Name: stat.Path,
                    FullPath: Combine(path, stat.Path),
                    IsDirectory: stat.FileMode.HasFlag(UnixFileStatus.Directory),
                    Size: stat.Size,
                    Modified: stat.Time))
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A folder the shell user can't read (e.g. /data) comes back as an empty listing over
            // the sync protocol, not an error — so an empty result is ambiguous. Probe with a shell
            // ls only in that case, so a denied folder gets a plain reason instead of looking like
            // an ordinary empty one (M2a). A genuinely empty folder stays empty.
            if (entries.Count == 0 && await IsPermissionDeniedAsync(path, ct))
            {
                throw new LincException("Couldn't read that folder on the phone — the connection isn't allowed to see inside it.");
            }
            return entries;
        }
        catch (Exception ex) when (ex is not (LincException or OperationCanceledException))
        {
            throw new LincException("Couldn't read that folder on the phone. It may not be accessible.", ex);
        }
    }

    /// <summary>Shell probe used to tell an empty folder apart from one the shell user can't read.</summary>
    private async Task<bool> IsPermissionDeniedAsync(string path, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync($"ls -1 {Quote(path)}", Device, receiver, ct);
            return receiver.ToString().Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not (LincException or OperationCanceledException))
        {
            return false; // best-effort: if the probe itself fails, treat the folder as just empty
        }
    }

    public async Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var localPath = UniqueLocalPath(Path.Combine(localDirectory, Path.GetFileName(remotePath)));
        try
        {
            using var sync = new SyncService(_adb, Device);
            await using var stream = File.Create(localPath);
            await sync.PullAsync(remotePath, stream, CreateProgressAdapter(progress), false, ct);
            log.Log(LogLevel.Info, $"Downloaded {Path.GetFileName(remotePath)}");
            return localPath;
        }
        catch (OperationCanceledException)
        {
            TryDeleteLocal(localPath);
            throw;
        }
        catch (Exception ex) when (ex is not LincException)
        {
            TryDeleteLocal(localPath);
            log.Log(LogLevel.Error, $"Download of {Path.GetFileName(remotePath)} failed: {ex.Message}");
            throw new LincException($"Couldn't download {Path.GetFileName(remotePath)} from the phone.", ex);
        }
    }

    public async Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(localPath);
        try
        {
            using var sync = new SyncService(_adb, Device);
            var remotePath = await UniqueRemotePathAsync(sync, Combine(remoteDirectory, fileName), ct);
            await using var stream = File.OpenRead(localPath);
            await sync.PushAsync(
                stream,
                remotePath,
                (UnixFileStatus)0b110_100_100, // 0644
                File.GetLastWriteTime(localPath),
                CreateProgressAdapter(progress),
                false,
                ct);
            log.Log(LogLevel.Info, $"Sent {fileName} to the phone");
            return remotePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LincException)
        {
            log.Log(LogLevel.Error, $"Sending {fileName} failed: {ex.Message}");
            throw new LincException($"Couldn't send {fileName} to the phone.", ex);
        }
    }

    public Task RenameAsync(string path, string newName, CancellationToken ct)
    {
        var parent = ParentOf(path);
        return RunShellAsync($"mv {Quote(path)} {Quote(Combine(parent, newName))}",
            "Couldn't rename that item on the phone.", ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct) =>
        RunShellAsync($"rm -rf {Quote(path)}", "Couldn't delete that item on the phone.", ct);

    public Task CreateFolderAsync(string parentPath, string name, CancellationToken ct) =>
        RunShellAsync($"mkdir {Quote(Combine(parentPath, name))}", "Couldn't create that folder on the phone.", ct);

    public async Task<string> PullScreenshotAsync(CancellationToken ct)
    {
        const string remote = "/sdcard/.linc-screenshot.png";
        await RunShellAsync($"screencap -p {remote}", "Couldn't take a screenshot on the phone.", ct);
        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc");
        Directory.CreateDirectory(localDir);
        var pulled = await PullAsync(remote, localDir, new Progress<double>(), ct);
        var final = Path.Combine(localDir, $"phone-screen-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.Move(pulled, final);
        try
        {
            await RunShellAsync($"rm {remote}", "cleanup", ct);
        }
        catch (LincException)
        {
            // The screenshot is safely on the PC; a leftover temp file is harmless.
        }
        log.Log(LogLevel.Info, "Screenshot saved from the phone");
        return final;
    }

    private async Task RunShellAsync(string command, string friendlyFailure, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await _adb.ExecuteRemoteCommandAsync(command, Device, receiver, ct);
            // mv/rm/mkdir are silent on success; any output is an error report.
            var output = receiver.ToString().Trim();
            if (output.Length > 0)
            {
                throw new LincException(friendlyFailure, new IOException(output));
            }
        }
        catch (Exception ex) when (ex is not (LincException or OperationCanceledException))
        {
            throw new LincException(friendlyFailure, ex);
        }
    }

    private static Action<SyncProgressChangedEventArgs> CreateProgressAdapter(IProgress<double> progress) =>
        args => progress.Report(
            args.TotalBytesToReceive > 0 ? args.ReceivedBytesSize * 100.0 / args.TotalBytesToReceive : 0);

    private async Task<string> UniqueRemotePathAsync(SyncService sync, string desiredPath, CancellationToken ct)
    {
        var candidate = desiredPath;
        for (var i = 2; i < 100; i++)
        {
            var stat = await sync.StatAsync(candidate, ct);
            if (stat.FileMode == 0)
            {
                return candidate; // does not exist
            }
            var directory = ParentOf(desiredPath);
            var stem = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);
            candidate = Combine(directory, $"{stem} ({i}){extension}");
        }
        return candidate;
    }

    private static string UniqueLocalPath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }
        var directory = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Leftover partial file; harmless.
        }
    }

    private static string Combine(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : $"{directory}/{name}";

    private static string ParentOf(string path)
    {
        var index = path.TrimEnd('/').LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    /// <summary>Single-quote a path for the Android shell.</summary>
    private static string Quote(string path) => $"'{path.Replace("'", "'\\''")}'";
}
