using System.IO;
using System.Text.Json;

namespace Linc.Desktop.Services;

public interface ISyncEngine
{
    /// <summary>Human-readable last-pass result for the Sync page.</summary>
    string StatusText { get; }
    event Action? StatusChanged;

    /// <summary>Re-read the lane toggles and folder paths, (re)start watchers/timer, and kick a pass.</summary>
    void Reconfigure();

    /// <summary>Run one reconcile pass now (also used by the "Sync now" button and the scripted verify).</summary>
    Task ReconcileNowAsync(CancellationToken ct = default);
}

/// <summary>
/// Folder/Photos sync engine (M18c, D-027). Runs entirely on the desktop over the M17
/// files channel (<see cref="IFileService"/>, transport-transparent) — new files propagate
/// both ways, newest-wins on conflict, no delete propagation. Per-pair seen-state persisted
/// under %LOCALAPPDATA%\Linc/sync-state.json is the loop/duplicate guard. Reconcile fires on
/// connect, on a 60 s timer, and on PC-side file changes (bidirectional pairs).
/// </summary>
public sealed class SyncEngine : ISyncEngine, IDisposable
{
    // A file's last-observed reality on both sides. mtimes are unix seconds (ADB sync stores
    // second granularity), compared with MtimeToleranceSec slack.
    private sealed record FileState(long PcSize, long PcMtime, long PhoneSize, long PhoneMtime);

    private sealed record SyncPair(string Id, string PcFolder, string PhoneFolder, bool Bidirectional);

    private const int MtimeToleranceSec = 2;
    private const int SettleSeconds = 2; // ignore PC files touched this recently — still being written
    private static readonly IProgress<double> NoProgress = new Progress<double>();

    private readonly IFileService _files;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly ILogService _log;
    private readonly string _statePath;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _timer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, Dictionary<string, FileState>> _state;

    private List<SyncPair> _pairs = new();
    private int _pending;

    public SyncEngine(IFileService files, IConnectionSupervisor supervisor, IDeviceRegistry registry, ILogService log)
    {
        _files = files;
        _supervisor = supervisor;
        _registry = registry;
        _log = log;
        _statePath = Path.Combine(_registry.RootPath, "sync-state.json");
        _state = Load();
        _timer = new Timer(_ => Kick(), null, Timeout.Infinite, Timeout.Infinite);
        _supervisor.StateChanged += OnLinkStateChanged;
        Reconfigure();
    }

    public string StatusText { get; private set; } = "";
    public event Action? StatusChanged;

    private bool Connected => _supervisor.State == LinkState.Connected;

    public void Reconfigure()
    {
        _pairs = BuildPairs();
        RebuildWatchers();
        _timer.Change(_pairs.Count > 0 ? TimeSpan.FromSeconds(60) : Timeout.InfiniteTimeSpan,
            TimeSpan.FromSeconds(60));
        Kick();
    }

    private List<SyncPair> BuildPairs()
    {
        var pairs = new List<SyncPair>();
        if (_registry.SyncLane("folders") && !string.IsNullOrWhiteSpace(_registry.FolderSyncPcPath))
        {
            pairs.Add(new SyncPair("folders", _registry.FolderSyncPcPath!, _registry.FolderSyncPhonePath, Bidirectional: true));
        }
        if (_registry.SyncLane("photos"))
        {
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc", "Photos");
            pairs.Add(new SyncPair("photos", dest, "/sdcard/DCIM/Camera", Bidirectional: false));
        }
        return pairs;
    }

    private void RebuildWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        // Only bidirectional pairs push PC changes, so only they need a PC-side watcher.
        foreach (var pair in _pairs.Where(p => p.Bidirectional && Directory.Exists(p.PcFolder)))
        {
            try
            {
                var w = new FileSystemWatcher(pair.PcFolder) { IncludeSubdirectories = false };
                w.Created += OnPcChanged;
                w.Changed += OnPcChanged;
                w.Renamed += OnPcChanged;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // A bad path just means no live watch; the 60 s timer still covers this pair.
            }
        }
    }

    private void OnPcChanged(object sender, FileSystemEventArgs e) => Kick();
    private void OnLinkStateChanged() => Kick();

    private void Kick() => _ = ReconcileNowAsync();

    public async Task ReconcileNowAsync(CancellationToken ct = default)
    {
        if (!Connected || _pairs.Count == 0)
        {
            return;
        }
        Interlocked.Exchange(ref _pending, 1);
        if (!await _gate.WaitAsync(0, ct))
        {
            return; // a pass is already running; the pending flag makes it loop once more
        }
        try
        {
            while (Interlocked.Exchange(ref _pending, 0) == 1 && Connected)
            {
                var copied = 0;
                string? error = null;
                try
                {
                    foreach (var pair in _pairs)
                    {
                        try
                        {
                            copied += await ReconcilePairAsync(pair, ct);
                        }
                        catch (LincException ex)
                        {
                            error = ex.Message; // e.g. All-files grant missing, or folder unreadable
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Anything else (a locked destination, a refused move) used to escape
                            // the whole pass and skip Persist below — so a single bad file threw
                            // away the seen-state for every file already copied, and the next run
                            // re-synced the entire folder. Contain it to the pair instead.
                            error = "Some files couldn't be synced this time.";
                            _log.Log(LogLevel.Warn, $"Sync pair '{pair.Id}' stopped early: {ex.Message}");
                        }
                    }
                    SetStatus(error ?? (copied > 0
                        ? $"Last synced {DateTime.Now:t} — {copied} file{(copied == 1 ? "" : "s")} copied"
                        : $"Up to date · checked {DateTime.Now:t}"));
                }
                finally
                {
                    // Always record what did get copied. This is the loop guard's whole value:
                    // losing it means re-copying everything next launch.
                    Persist();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ReconcilePairAsync(SyncPair pair, CancellationToken ct)
    {
        Directory.CreateDirectory(pair.PcFolder);

        var phone = (await _files.ListAsync(pair.PhoneFolder, ct))
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.Name, StringComparer.Ordinal);
        var pc = new DirectoryInfo(pair.PcFolder).GetFiles()
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        var seen = _state.TryGetValue(pair.Id, out var s) ? s : _state[pair.Id] = new();
        var names = phone.Keys.Concat(pc.Keys).Concat(seen.Keys).Distinct(StringComparer.Ordinal).ToList();

        var copied = 0;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var hasPhone = phone.TryGetValue(name, out var p);
            var hasPc = pc.TryGetValue(name, out var c);
            var known = seen.TryGetValue(name, out var st);

            if (!hasPhone && !hasPc)
            {
                seen.Remove(name); // gone from both sides — forget it
                continue;
            }
            // In seen + present on only one side ⇒ deleted on the other. No delete propagation
            // and no resurrection: leave both as they are (the seen entry blocks a re-copy).
            if (known && (!hasPhone || !hasPc))
            {
                continue;
            }

            var phoneMtime = hasPhone ? p!.Modified.ToUnixTimeSeconds() : 0;
            var pcMtime = hasPc ? new DateTimeOffset(c!.LastWriteTimeUtc).ToUnixTimeSeconds() : 0;

            // A PC file still settling (just written) is skipped this pass; the timer catches it.
            if (hasPc && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - pcMtime < SettleSeconds)
            {
                continue;
            }

            var phoneChanged = hasPhone && (st is null || p!.Size != st.PhoneSize || Math.Abs(phoneMtime - st.PhoneMtime) > MtimeToleranceSec);
            var pcChanged = hasPc && (st is null || c!.Length != st.PcSize || Math.Abs(pcMtime - st.PcMtime) > MtimeToleranceSec);

            var remotePath = CombineRemote(pair.PhoneFolder, name);
            var pcPath = Path.Combine(pair.PcFolder, name);

            // Decide the winner. Pull-only pairs (photos) never push, so a PC-only file or a
            // PC-side edit is ignored — only phone → PC flows.
            var pullWins = hasPhone && phoneChanged && (!pair.Bidirectional || !pcChanged || phoneMtime >= pcMtime);
            var pushWins = pair.Bidirectional && hasPc && pcChanged && (!phoneChanged || pcMtime > phoneMtime);

            // One unreadable or locked file must not cost the rest of the folder its progress.
            try
            {
                if (pullWins)
                {
                    await PullAsync(remotePath, pcPath, p!.Modified, hasPc, ct);
                    seen[name] = new FileState(p!.Size, phoneMtime, p.Size, phoneMtime);
                    copied++;
                }
                else if (pushWins)
                {
                    await PushAsync(pcPath, pair.PhoneFolder, remotePath, hasPhone, ct);
                    seen[name] = new FileState(c!.Length, pcMtime, c.Length, pcMtime);
                    copied++;
                }
                else if (!known && hasPhone && hasPc && !pair.Bidirectional)
                {
                    // Pull-only, same name already on both sides, phone not newer — adopt as in-sync.
                    seen[name] = new FileState(c!.Length, pcMtime, p!.Size, phoneMtime);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left out of `seen`, so the next pass retries it.
                _log.Log(LogLevel.Warn, $"Couldn't sync {name}: {ex.Message}");
            }
        }
        return copied;
    }

    /// <summary>Phone → PC. Overwrites when the PC copy already exists (newest-wins), else a fresh pull.</summary>
    private async Task PullAsync(string remotePath, string pcPath, DateTimeOffset phoneModified, bool overwrite, CancellationToken ct)
    {
        if (overwrite)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "linc-sync");
            Directory.CreateDirectory(tmpDir);
            var pulled = await _files.PullAsync(remotePath, tmpDir, NoProgress, ct);
            try
            {
                File.Move(pulled, pcPath, overwrite: true);
            }
            finally
            {
                // A failed move used to strand the staging copy forever (leftovers dating back
                // days were found in %TEMP%\linc-sync), and a stale name there makes the next
                // pull auto-rename around it.
                if (File.Exists(pulled))
                {
                    try { File.Delete(pulled); } catch (IOException) { /* best effort */ }
                }
            }
        }
        else
        {
            await _files.PullAsync(remotePath, Path.GetDirectoryName(pcPath)!, NoProgress, ct);
        }
        // Match mtimes so future passes compare cleanly across the two sides.
        File.SetLastWriteTimeUtc(pcPath, phoneModified.UtcDateTime);
        _log.Log(LogLevel.Info, $"Synced {Path.GetFileName(pcPath)} from the phone");
    }

    /// <summary>PC → phone. Deletes the stale remote first when overwriting (files channel has no overwrite).</summary>
    private async Task PushAsync(string pcPath, string phoneFolder, string remotePath, bool overwrite, CancellationToken ct)
    {
        if (overwrite)
        {
            try
            {
                await _files.DeleteAsync(remotePath, ct);
            }
            catch (LincException)
            {
                // Direct TLS has no delete (D-024) — but its push writes the path in place, so
                // the overwrite still happens. Only ADB sync needs the delete, because it
                // auto-renames to "name (2).ext" instead of replacing (D-027).
            }
        }
        await _files.PushAsync(pcPath, phoneFolder, NoProgress, ct);
        _log.Log(LogLevel.Info, $"Synced {Path.GetFileName(pcPath)} to the phone");
    }

    private static string CombineRemote(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : $"{dir}/{name}";

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke();
    }

    private Dictionary<string, Dictionary<string, FileState>> Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, FileState>>>(
                    File.ReadAllText(_statePath)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt state just means a fresh (re-adopting) first pass.
        }
        return new();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Losing this file silently is expensive — it means re-syncing every file next
            // launch — so say so rather than swallowing it. (UnauthorizedAccessException is not
            // an IOException, so the old narrower catch would have let it escape the pass.)
            _log.Log(LogLevel.Warn, $"Couldn't save the sync bookkeeping file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _supervisor.StateChanged -= OnLinkStateChanged;
        _timer.Dispose();
        foreach (var w in _watchers)
        {
            w.Dispose();
        }
        _gate.Dispose();
    }
}
