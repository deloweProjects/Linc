using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › Rescue (TOOLS.md §2): "Screen broken or black?" in three steps. See it (the phone's
/// screen here, the phone's own screen kept off), get in (wake, swipe, type the PIN — never
/// saved), and save your stuff (photos and files copied to this PC). Works only when USB
/// debugging was allowed on the phone before the screen broke.
/// </summary>
public partial class RescueToolViewModel(
    ToolsContext ctx,
    TypeToolViewModel type,
    IMirrorService mirror,
    IDeviceRegistry registry,
    ILogService log) : ToolSection(ctx)
{
    private CancellationTokenSource? _backup;
    private bool _loadingAwake;

    // ---- 1. See it ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task SeeItAsync()
    {
        if (mirror.State == MirrorState.Running)
        {
            Ctx.Say("The phone's screen is already open on this PC.");
            return;
        }
        var model = Ctx.Device?.Model ?? "Phone";
        // The phone's own screen stays off (it may be broken, or showing to the room) and awake.
        // The phone keeps its own shape: "Fill this monitor" would resize a display nobody can see.
        var settings = registry.Mirror with { TurnScreenOff = true, StayAwake = true, ScreenShape = null };
        try
        {
            Ctx.ShowCommand("scrcpy --turn-screen-off --stay-awake");
            await mirror.StartAsync(Ctx.Serial, $"Linc — {model}", settings);
        }
        catch (LincException ex)
        {
            Ctx.Say(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Log(LogLevel.Warn, $"Rescue: mirror didn't start: {ex.Message}");
            Ctx.Say("Couldn't open the phone's screen here. Try again, or reconnect the cable.");
        }
    }

    // ---- 2. Get in ---------------------------------------------------------------------------

    /// <summary>Wake, swipe up, type the PIN and press Enter. The PIN only passes through: the
    /// view hands it over and clears its box, and it is never logged or shown.</summary>
    public async Task GetInAsync(string pin)
    {
        if (!await type.WakeAsync()) return;
        await Task.Delay(400);
        if (!await type.SwipeUpAsync()) return;
        if (pin.Length == 0)
        {
            Ctx.Say("The phone is awake. Type your PIN and press Wake & unlock again.");
            return;
        }
        await Task.Delay(600); // the PIN pad slides in
        if (await type.TypeAsync(pin, enter: true))
        {
            Ctx.Say("PIN sent. Still locked? Try again, or use See it to look at the screen.");
        }
    }

    // ---- 3. Save your stuff ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupIdle), nameof(PauseLabel))]
    public partial bool BackupRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool BackupPaused { get; set; }

    [ObservableProperty]
    public partial double BackupProgress { get; set; }

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = "Photos, downloads and documents to this PC.";

    [ObservableProperty]
    public partial bool BackupDone { get; set; }

    public bool BackupIdle => !BackupRunning;

    public string PauseLabel => BackupPaused ? "Resume" : "Pause";

    private string _backupFolder = "";

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (BackupRunning) return;
        var cts = _backup = new CancellationTokenSource();
        var ct = cts.Token;
        var serial = Ctx.Serial;
        var model = Ctx.Device?.Model ?? "Phone";
        BackupRunning = true;
        BackupPaused = false;
        BackupDone = false;
        BackupProgress = 0;
        BackupStatus = "Looking for files on the phone…";
        try
        {
            var listing = await Ctx.RunAsync(BackupPlan.ListCommand(), TimeSpan.FromMinutes(5), ct: ct);
            if (!listing.Ok)
            {
                BackupStatus = listing.Text;
                return;
            }
            var files = BackupPlan.ParseListing(listing.Text);
            if (files.Count == 0)
            {
                BackupStatus = "No photos or files found in the phone's usual folders.";
                return;
            }
            var safeModel = string.Concat(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            _backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Linc Backup", $"{safeModel} {DateTime.Now:yyyy-MM-dd}");
            Directory.CreateDirectory(_backupFolder);
            int done = 0, copied = 0, failed = 0;
            long total = files.Sum(f => f.Size), bytesDone = 0;
            foreach (var file in files)
            {
                while (BackupPaused && !ct.IsCancellationRequested) await Task.Delay(200, ct);
                ct.ThrowIfCancellationRequested();
                BackupStatus = BackupPlan.Progress(done, files.Count);
                if (BackupPlan.LocalRelativePath(file.RemotePath) is { } relative)
                {
                    var local = Path.Combine(_backupFolder, relative);
                    var info = new FileInfo(local);
                    if (!(info.Exists && BackupPlan.AlreadyCopied(info.Length, info.LastWriteTimeUtc, file)))
                    {
                        var before = bytesDone;
                        var progress = new Progress<double>(p =>
                            BackupProgress = total > 0 ? (before + file.Size * p / 100) * 100 / total : 0);
                        if (await Ctx.Shell.PullAsync(serial, file.RemotePath, local, progress, ct))
                        {
                            copied++;
                            try { File.SetLastWriteTimeUtc(local, DateTimeOffset.FromUnixTimeSeconds(file.ModifiedUnix).UtcDateTime); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }
                bytesDone += file.Size;
                done++;
                BackupProgress = total > 0 ? bytesDone * 100.0 / total : done * 100.0 / files.Count;
            }
            BackupDone = true;
            BackupStatus = BackupPlan.Progress(done, files.Count) + (failed > 0 ? $" · {failed} couldn't be copied" : " · all saved");
            log.Log(LogLevel.Info, $"Rescue: backup finished, {copied} copied, {done - copied - failed} already there, {failed} failed");
        }
        catch (OperationCanceledException)
        {
            BackupStatus = "Stopped. Start again to carry on where it left off.";
            BackupDone = _backupFolder.Length > 0;
        }
        finally
        {
            BackupRunning = false;
            BackupPaused = false;
            if (_backup == cts) _backup = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void PauseBackup() => BackupPaused = !BackupPaused;

    [RelayCommand]
    private void CancelBackup() => _backup?.Cancel();

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (_backupFolder.Length == 0 || !Directory.Exists(_backupFolder)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _backupFolder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Ctx.Say("Couldn't open the folder: " + _backupFolder);
        }
    }

    // ---- hidden extra: keep awake while charging ---------------------------------------------

    /// <summary>The phone's screen stays on while it's plugged in to USB (<c>svc power stayon</c>).</summary>
    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    public override async Task ShownAsync()
    {
        var value = await Ctx.Shell.RunAsync(Ctx.Serial, "settings get global stay_on_while_plugged_in");
        _loadingAwake = true;
        // A bit mask: 1 AC, 2 USB, 4 wireless.
        KeepAwake = value.Ok && int.TryParse(value.Text.Trim(), out var mask) && (mask & 2) != 0;
        _loadingAwake = false;
    }

    partial void OnKeepAwakeChanged(bool value)
    {
        if (_loadingAwake || !Ctx.HasDevice) return;
        _ = Ctx.DoAsync(value ? "svc power stayon usb" : "svc power stayon false",
            value ? "The phone stays awake while it's plugged in." : "The phone sleeps as usual again.");
    }
}
