using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public sealed class RemoteEntryVm(RemoteEntry entry)
{
    public RemoteEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public string Glyph => Entry.IsDirectory ? "" : "";
    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);
    public string ModifiedText => Entry.Modified.LocalDateTime.ToString("g");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

public partial class FilesViewModel : ObservableObject
{
    private readonly IFileService _files;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _transferCts;
    private string? _resumePath;

    public ObservableCollection<RemoteEntryVm> Entries { get; } = [];
    public ObservableCollection<StorageRoot> Roots { get; } = [];

    public FilesViewModel(IFileService files, IConnectionSupervisor supervisor)
    {
        _files = files;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        CurrentPath = "";
        TransferText = "";

        // Ready when connected, or with ADB only (no Linc app answering, Tools PLAN.md §1.2).
        // StateChanged also fires for ADB-chip changes, so only a not-ready → ready edge reloads.
        bool Ready() => supervisor.State == LinkState.Connected ||
            supervisor.Presence is LinkPresence.ConnectedAdbOnly or LinkPresence.ConnectedStartingApp;
        var wasReady = Ready();
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            var ready = Ready();
            var edge = ready && (!wasReady || Entries.Count == 0 && CurrentPath.Length == 0);
            var reconnected = supervisor.State == LinkState.Connected && !_wasConnected;
            wasReady = ready;
            _wasConnected = supervisor.State == LinkState.Connected;
            if (ready)
            {
                if (edge || reconnected)
                {
                    _ = InitializeAsync();
                }
            }
            else
            {
                // Remembered so a reconnect reopens the same folder instead of jumping to the top.
                if (CurrentPath.Length > 0)
                {
                    _resumePath = CurrentPath;
                }
                Entries.Clear();
                Roots.Clear();
                CurrentPath = "";
                CancelTransfer();
            }
        });

        // This page-scoped VM is created lazily on first navigation to Files, which normally
        // happens *after* AppShellViewModel has already started the supervisor and reached
        // Connected. StateChanged is edge-triggered, so a transition that fired before this VM
        // existed is lost — without this, the listing stays blank until the next reconnect.
        _wasConnected = supervisor.State == LinkState.Connected;
        if (wasReady)
        {
            _ = InitializeAsync();
        }
    }

    private bool _wasConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathSegments))]
    public partial string CurrentPath { get; set; }

    /// <summary>Segments for a BreadcrumbBar's ItemsSource, e.g. ["", "Download", "Photos"].</summary>
    public IReadOnlyList<string> PathSegments =>
        CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial RemoteEntryVm? SelectedEntry { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotTransferring))]
    public partial bool TransferActive { get; set; }

    [ObservableProperty]
    public partial double TransferProgress { get; set; }

    [ObservableProperty]
    public partial string TransferText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The PC file the last download wrote, for "Show in folder"; null until one finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastSaved))]
    public partial string? LastSavedPath { get; set; }

    public bool HasLastSaved => LastSavedPath is not null;

    public bool HasSelection => SelectedEntry is not null;
    public bool IsNotTransferring => !TransferActive;
    public bool HasError => ErrorMessage is not null;

    private async Task InitializeAsync()
    {
        try
        {
            Roots.Clear();
            foreach (var root in await _files.GetRootsAsync(CancellationToken.None))
            {
                Roots.Add(root);
            }
            // Auto-open at the first root (internal storage / sdcard); the "Go to" menu jumps to
            // any other root — including the phone root "/" — on demand (M2a).
            if (_resumePath is { } resume)
            {
                _resumePath = null;
                await NavigateAsync(resume);
            }
            else if (Roots.FirstOrDefault() is { } start)
            {
                await NavigateAsync(start.Path);
            }
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task NavigateAsync(string path)
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var listing = await _files.ListAsync(path, CancellationToken.None);
            CurrentPath = path;
            SelectedEntry = null;
            Entries.Clear();
            foreach (var entry in listing)
            {
                Entries.Add(new RemoteEntryVm(entry));
            }
        }
        catch (LincException ex)
        {
            // A folder the ADB shell user can't read (e.g. /data) degrades to an empty listing plus
            // a plain note rather than a dead end (M2a): move into it anyway so the breadcrumb and
            // Up still work and the user can climb back out.
            CurrentPath = path;
            SelectedEntry = null;
            Entries.Clear();
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Jump to a BreadcrumbBar segment by its index into <see cref="PathSegments"/>.</summary>
    public Task NavigateToSegmentAsync(int index)
    {
        var segments = PathSegments;
        if (index < 0 || index >= segments.Count)
        {
            return Task.CompletedTask;
        }
        return NavigateAsync("/" + string.Join("/", segments.Take(index + 1)));
    }

    public Task OpenEntryAsync(RemoteEntryVm entry) =>
        entry.IsDirectory ? NavigateAsync(entry.Entry.FullPath) : Task.CompletedTask;

    [RelayCommand]
    private Task UpAsync()
    {
        // Up climbs all the way to the Android root "/", not just to the selected storage root, so
        // the whole tree the ADB shell user can read is browsable (M2a). "/" has no parent.
        if (CurrentPath.Length == 0 || CurrentPath == "/")
        {
            return Task.CompletedTask;
        }
        var index = CurrentPath.TrimEnd('/').LastIndexOf('/');
        var parent = index <= 0 ? "/" : CurrentPath[..index];
        return NavigateAsync(parent);
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        CurrentPath.Length > 0 ? NavigateAsync(CurrentPath) : Task.CompletedTask;

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedEntry is not { IsDirectory: false } file)
        {
            ErrorMessage = SelectedEntry is null ? null : "Folder download isn't supported yet — open the folder and download files individually.";
            return;
        }
        // Pulled files land in Downloads\Linc (created on demand), matching the Pictures\Linc /
        // Downloads\Linc share convention rather than dumping into the Downloads root (M2a).
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Linc");
        LastSavedPath = null;
        await RunTransferAsync($"Downloading {file.Name}…", async (progress, ct) =>
        {
            Directory.CreateDirectory(downloads);
            var localPath = await _files.PullAsync(file.Entry.FullPath, downloads, progress, ct);
            LastSavedPath = localPath;
            return $"Saved to {localPath}";
        });
    }

    /// <summary>Puts the selected item's phone path (or the open folder's) on the PC clipboard.</summary>
    [RelayCommand]
    private void CopyPath()
    {
        var path = SelectedEntry?.Entry.FullPath ?? CurrentPath;
        if (path.Length == 0)
        {
            return;
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(path);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        TransferText = $"Copied {path}";
    }

    [RelayCommand]
    private void ShowLastSaved()
    {
        if (LastSavedPath is { } path && File.Exists(path))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    public async Task UploadFilesAsync(IReadOnlyList<string> localPaths)
    {
        if (CurrentPath.Length == 0)
        {
            return;
        }
        var destination = CurrentPath;
        foreach (var localPath in localPaths.Where(File.Exists))
        {
            var name = Path.GetFileName(localPath);
            var completed = await RunTransferAsync($"Sending {name}…", async (progress, ct) =>
            {
                await _files.PushAsync(localPath, destination, progress, ct);
                return $"Sent {name}";
            });
            if (!completed)
            {
                break; // cancelled or failed; don't continue the batch
            }
        }
        await RefreshAsync();
    }

    public async Task RenameSelectedAsync(string newName)
    {
        if (SelectedEntry is null || newName.Length == 0 || newName == SelectedEntry.Name)
        {
            return;
        }
        ErrorMessage = null;
        try
        {
            await _files.RenameAsync(SelectedEntry.Entry.FullPath, newName, CancellationToken.None);
            await RefreshAsync();
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedEntry is null)
        {
            return;
        }
        ErrorMessage = null;
        try
        {
            await _files.DeleteAsync(SelectedEntry.Entry.FullPath, CancellationToken.None);
            await RefreshAsync();
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task CreateFolderAsync(string name)
    {
        if (name.Length == 0 || CurrentPath.Length == 0)
        {
            return;
        }
        ErrorMessage = null;
        try
        {
            await _files.CreateFolderAsync(CurrentPath, name, CancellationToken.None);
            await RefreshAsync();
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
    }

    /// <returns>True when the transfer ran to completion.</returns>
    private async Task<bool> RunTransferAsync(string description, Func<IProgress<double>, CancellationToken, Task<string>> transfer)
    {
        if (TransferActive)
        {
            return false;
        }
        ErrorMessage = null;
        TransferActive = true;
        TransferText = description;
        TransferProgress = 0;
        _transferCts = new CancellationTokenSource();
        var finished = false;
        try
        {
            var progress = new Progress<double>(value =>
            {
                // Progress reports are posted to the UI thread and can land after the transfer
                // returned; without this a late "97%" overwrote "Saved to …".
                if (finished)
                {
                    return;
                }
                TransferProgress = value;
                TransferText = $"{description} {value:0}%";
            });
            var result = await transfer(progress, _transferCts.Token);
            finished = true;
            TransferText = result;
            return true;
        }
        catch (OperationCanceledException)
        {
            finished = true;
            TransferText = "Cancelled";
            return false;
        }
        catch (Exception ex)
        {
            // Not only LincException: anything else used to escape the command unreported.
            finished = true;
            TransferText = "";
            ErrorMessage = ex is LincException ? ex.Message : $"The transfer failed: {ex.Message}";
            return false;
        }
        finally
        {
            TransferActive = false;
            _transferCts.Dispose();
            _transferCts = null;
        }
    }
}
