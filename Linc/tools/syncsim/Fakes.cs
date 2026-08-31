namespace Linc.Desktop.Services;

// Minimal stand-ins for the types SyncEngine.cs depends on, so the real engine compiles
// into this harness without dragging in WinUI. Shapes match the app's definitions.

public enum LinkState { NoDevice, Searching, Connecting, Connected, Paused }

public enum LogLevel { Info, Warn, Error }

public sealed class LincException(string friendlyMessage, Exception? inner = null)
    : Exception(friendlyMessage, inner);

public sealed record RemoteEntry(
    string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset Modified);

public interface ILogService { void Log(LogLevel level, string message); }

public interface IConnectionSupervisor
{
    LinkState State { get; }
    event Action? StateChanged;
}

public interface IDeviceRegistry
{
    bool SyncLane(string lane);
    string? FolderSyncPcPath { get; }
    string FolderSyncPhonePath { get; }
    string RootPath { get; }
}

public interface IFileService
{
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct);
    Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct);
    Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

public sealed class FakeLog : ILogService
{
    public List<string> Lines { get; } = [];
    public void Log(LogLevel level, string message) => Lines.Add($"{level}: {message}");
}

public sealed class FakeSupervisor : IConnectionSupervisor
{
    public LinkState State { get; set; } = LinkState.Connected;
    public event Action? StateChanged;
    public void Raise() => StateChanged?.Invoke();
}

public sealed class FakeRegistry(string pcFolder, string phoneFolder, string rootPath) : IDeviceRegistry
{
    // Photos stays OFF: enabling it would point a real pair at the user's Pictures\Linc/Photos.
    public bool SyncLane(string lane) => lane == "folders";
    public string? FolderSyncPcPath => pcFolder;
    public string FolderSyncPhonePath => phoneFolder;
    public string RootPath => rootPath;
}

/// <summary>
/// A local directory standing in for the phone. <paramref name="overwriteOnPush"/> picks the
/// transport's real behaviour: ADB sync auto-renames to "name (2).ext" on conflict and supports
/// delete, while Direct TLS writes the path in place and has no delete at all (D-024).
/// </summary>
public sealed class FakePhoneFiles(string root, bool overwriteOnPush, bool supportsDelete) : IFileService
{
    /// <summary>File names that fail to pull — stands in for a locked or unreadable file.</summary>
    public HashSet<string> FailPullFor { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How many times the engine has listed the phone folder. Every reconcile pass lists before
    /// it decides anything, so a non-zero count is proof that a pass has already looked at the
    /// world — which is how the harness detects a pass running against half-written fixtures.
    /// </summary>
    public int ListCount;

    private string Local(string remotePath) => Path.Combine(root, remotePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref ListCount);
        var dir = Local(path);
        Directory.CreateDirectory(dir);
        IReadOnlyList<RemoteEntry> entries = new DirectoryInfo(dir).GetFiles()
            .Select(f => new RemoteEntry(f.Name, $"{path.TrimEnd('/')}/{f.Name}", false, f.Length,
                new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
        return Task.FromResult(entries);
    }

    public Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct)
    {
        if (FailPullFor.Contains(Path.GetFileName(remotePath)))
        {
            throw new IOException($"The process cannot access the file '{Path.GetFileName(remotePath)}'.");
        }
        Directory.CreateDirectory(localDirectory);
        var source = Local(remotePath);
        var target = Path.Combine(localDirectory, Path.GetFileName(source));
        if (File.Exists(target))
        {
            target = NextFreeName(target); // matches the app's conflict-safe naming
        }
        File.Copy(source, target);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        return Task.FromResult(target);
    }

    public Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Local(remoteDirectory);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(localPath));
        if (File.Exists(target) && !overwriteOnPush)
        {
            target = NextFreeName(target);
        }
        File.Copy(localPath, target, overwrite: true);
        // The files channel stamps the phone mtime from the PC file (D-027).
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(localPath));
        return Task.FromResult(target);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        if (!supportsDelete)
        {
            throw new LincException("That needs a USB or wireless-debugging connection. Turn one on and try again.");
        }
        var local = Local(path);
        if (File.Exists(local)) File.Delete(local);
        return Task.CompletedTask;
    }

    private static string NextFreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
