using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › Capture (TOOLS.md §4): one click for a screenshot, one to start and stop a recording,
/// the last capture with Open · Copy · Show in folder. Clean status bar, touches and quality
/// sit under More.
/// </summary>
public partial class CaptureToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    private Task<ShellResult>? _recording;
    private CancellationTokenSource? _clock;
    private string? _touchesBefore;
    private bool _finishing;
    private bool _demoOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordTitle), nameof(RecordGlyph), nameof(NotRecording))]
    public partial bool Recording { get; set; }

    public bool NotRecording => !Recording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordTitle))]
    public partial int RecordSeconds { get; set; }

    public string RecordTitle => Recording ? "Stop · " + CaptureCommands.Clock(RecordSeconds) : "Record";

    public string RecordGlyph => Recording ? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    public partial bool Busy { get; set; }

    public bool NotBusy => !Busy;

    /// <summary>The file the last capture went to, or "" before the first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLast), nameof(LastName), nameof(LastIsImage), nameof(LastIsVideo))]
    public partial string LastPath { get; set; } = "";

    public bool HasLast => LastPath.Length > 0;

    public string LastName => Path.GetFileName(LastPath);

    public bool LastIsImage => LastPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    public bool LastIsVideo => HasLast && !LastIsImage;

    /// <summary>The phone shows 12:00, full battery and signal, no notification icons.</summary>
    [ObservableProperty]
    public partial bool CleanStatusBar { get; set; }

    [ObservableProperty]
    public partial bool ShowTouches { get; set; }

    /// <summary>0 = 720p, 1 = 1080p, 2 = the phone's own size.</summary>
    [ObservableProperty]
    public partial int Quality { get; set; }

    public static string ScreenshotFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Linc", "Screenshots");

    public static string VideoFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Linc");

    public override void Hidden()
    {
        // Nothing keeps running on the phone once Capture is out of sight.
        if (Recording) _ = StopRecordingAsync();
        if (CleanStatusBar) CleanStatusBar = false;
    }

    [RelayCommand]
    private async Task ScreenshotAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Ctx.ShowCommand("adb exec-out screencap -p");
            var png = await Ctx.Shell.ExecOutAsync(Ctx.Serial, "screencap -p", TimeSpan.FromSeconds(20));
            if (png is null)
            {
                Ctx.Say("Couldn't take a screenshot. Check the cable or wireless debugging.");
                return;
            }
            Directory.CreateDirectory(ScreenshotFolder);
            var path = Path.Combine(ScreenshotFolder, CaptureCommands.FileName(DateTime.Now, "png"));
            await File.WriteAllBytesAsync(path, png);
            LastPath = path;
            Ctx.Say(ScreenshotCheck.LooksBlack(png)
                ? "The screenshot is black: the app on screen blocks screenshots."
                : "Screenshot saved to Pictures › Linc › Screenshots.");
        }
        catch (IOException ex)
        {
            Ctx.Say("Couldn't save the screenshot: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Ctx.Say("Couldn't save the screenshot: " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (Recording)
        {
            await StopRecordingAsync();
            return;
        }
        if (Busy) return;
        if (ShowTouches)
        {
            var before = await Ctx.RunAsync("settings get system show_touches");
            _touchesBefore = before.Ok && before.Text.Trim() is "0" or "1" ? before.Text.Trim() : "0";
            await Ctx.RunAsync("settings put system show_touches 1");
        }
        Recording = true;
        RecordSeconds = 0;
        _finishing = false;
        // The run ends when screenrecord does: stopped by Stop, or by its own 180 s limit.
        _recording = Ctx.RunAsync(CaptureCommands.Screenrecord(Quality), TimeSpan.FromSeconds(CaptureCommands.RecordLimitSeconds + 20));
        var clock = _clock = new CancellationTokenSource();
        _ = TickAsync(clock.Token);
        _ = FinishWhenDoneAsync(_recording);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Recording)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            RecordSeconds++;
        }
    }

    private async Task FinishWhenDoneAsync(Task<ShellResult> run)
    {
        await run;
        if (run == _recording) await FinishAsync(run);
    }

    private async Task StopRecordingAsync()
    {
        if (_recording is not { } run || _finishing) return;
        var stop = await Ctx.RunAsync(CaptureCommands.StopRecording);
        if (!stop.Ok || stop.Text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            await Ctx.RunAsync(CaptureCommands.StopRecordingFallback);
        }
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        await FinishAsync(run);
    }

    /// <summary>Copies the recording to the PC once, however it ended.</summary>
    private async Task FinishAsync(Task<ShellResult> run)
    {
        if (_finishing || run != _recording) return;
        _finishing = true;
        _clock?.Cancel();
        Busy = true;
        try
        {
            // screenrecord writes the end of the file as it exits; give it a moment.
            await Task.Delay(800);
            Directory.CreateDirectory(VideoFolder);
            var path = Path.Combine(VideoFolder, CaptureCommands.FileName(DateTime.Now, "mp4"));
            Ctx.ShowCommand("adb pull " + CaptureCommands.RecordingPath);
            if (await Ctx.Shell.PullAsync(Ctx.Serial, CaptureCommands.RecordingPath, path, null, CancellationToken.None))
            {
                LastPath = path;
                Ctx.Say("Recording saved to Videos › Linc.");
            }
            else
            {
                Ctx.Say(run.IsCompletedSuccessfully && !run.Result.Ok ? run.Result.Text : "Couldn't copy the recording off the phone.");
            }
            await Ctx.RunAsync("rm -f " + CaptureCommands.RecordingPath);
            if (_touchesBefore is { } touches)
            {
                await Ctx.RunAsync("settings put system show_touches " + touches);
                _touchesBefore = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ctx.Say("Couldn't save the recording: " + ex.Message);
        }
        finally
        {
            _recording = null;
            Recording = false;
            Busy = false;
        }
    }

    partial void OnCleanStatusBarChanged(bool value) => _ = ApplyDemoAsync(value);

    private async Task ApplyDemoAsync(bool on)
    {
        if (on == _demoOn) return;
        _demoOn = on;
        if (on)
        {
            foreach (var command in CaptureCommands.DemoEnter)
            {
                if (!(await Ctx.RunAsync(command)).Ok)
                {
                    Ctx.Say("The phone didn't take the clean status bar.");
                    break;
                }
            }
        }
        else
        {
            await Ctx.RunAsync(CaptureCommands.DemoExit);
        }
    }

    [RelayCommand]
    private void OpenLast()
    {
        if (!File.Exists(LastPath)) return;
        try { Process.Start(new ProcessStartInfo(LastPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Ctx.Say("Windows couldn't open it."); }
    }

    [RelayCommand]
    private void ShowLastInFolder()
    {
        if (!File.Exists(LastPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
