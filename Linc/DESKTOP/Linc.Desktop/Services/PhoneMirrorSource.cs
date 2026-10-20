using System.Diagnostics;

namespace Linc.Desktop.Services;

/// <summary>
/// Play everywhere with the phone as the source (v31, owner concerns item 13): the phone's sound
/// taken the way Mirror screen takes it, scrcpy with no picture, but <b>without</b>
/// <c>--audio-dup</c>, so the phone's own speaker goes quiet and the PC plays it instead.
///
/// <para><b>How it reaches every speaker.</b> Nothing new: Play everywhere is already on with the
/// PC as the source, so the virtual cable is Windows' default output and Linc captures it.
/// scrcpy (SDL) plays into the <i>default</i> output like any other app, so the phone's sound
/// lands in the cable, and the existing pipeline plays it on each PC speaker and back on the
/// phone (channel 6), all on the PC's clock and in step.</para>
///
/// <para><b>Why <c>--audio-source=playback</c>.</b> scrcpy's default source ("output",
/// REMOTE_SUBMIX) takes the phone's whole output, Linc's own copy included, which would feed back
/// on itself. Playback capture only takes media (USAGE_MEDIA) players; the phone plays its copy
/// as USAGE_GAME while this runs (<c>SyncedAudio</c>), which the capture leaves alone. Android 13+;
/// <c>--require-audio</c> makes scrcpy exit (and say "Android 13") instead of running silent.</para>
///
/// <para>Needs an adb route (USB or wireless debugging). <see cref="PhoneSoundOnPc"/> (the
/// phone's sound on the PC <i>outside</i> Play everywhere, with <c>--audio-dup</c>) is separate
/// and unchanged.</para>
/// </summary>
public sealed class PhoneMirrorSource(ILogService log)
{
    /// <summary>scrcpy's own jitter buffer, ms (its default is 50): Play everywhere's group delay already covers jitter.</summary>
    public const int AudioBufferMs = 40;

    private readonly object _lock = new();
    private Process? _process;
    private bool _stopping;

    /// <summary>scrcpy ended on its own: <c>"needs-android-13"</c> or <c>"mirror-stopped"</c>. From any thread.</summary>
    public event Action<string>? Failed;

    public bool Running
    {
        get { lock (_lock) return _process is not null; }
    }

    /// <summary>scrcpy can reach the phone: a USB or wireless-debugging link, and scrcpy is bundled.</summary>
    public static bool HasRoute(ConnectedDevice? live) =>
        live is not null && live.Transport != LinkTransport.DirectTls && ToolLocator.FindScrcpy() is not null;

    /// <summary>Start taking the phone's sound. Null when running, else <c>"needs-adb"</c> or <c>"failed"</c>.</summary>
    public string? Start(ConnectedDevice? live)
    {
        var scrcpy = ToolLocator.FindScrcpy();
        if (live is null || live.Transport == LinkTransport.DirectTls || scrcpy is null) return "needs-adb";
        lock (_lock)
        {
            if (_process is not null) return null;
            var start = new ProcessStartInfo(scrcpy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (ToolLocator.FindAdb() is { } adb) start.EnvironmentVariables["ADB"] = adb;
            foreach (var arg in new[]
            {
                "-s", live.Serial, "--no-video", "--no-window", "--no-control",
                "--audio-source=playback", "--require-audio", $"--audio-buffer={AudioBufferMs}",
            })
                start.ArgumentList.Add(arg);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var lastLine = "";
            var sawAndroid13 = false;
            void Line(string? data)
            {
                if (string.IsNullOrWhiteSpace(data)) return;
                lastLine = data.Trim();
                if (lastLine.Contains("Android 13", StringComparison.OrdinalIgnoreCase)) sawAndroid13 = true;
            }
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.Exited += (_, _) => OnExited(process, () => lastLine, () => sawAndroid13);
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                log.Log(LogLevel.Warn, $"Play everywhere: couldn't start taking the phone's sound ({ex.Message})");
                process.Dispose();
                return "failed";
            }
            _process = process;
            _stopping = false;
            log.Log(LogLevel.Info, $"Play everywhere: taking {live.Model}'s sound the way Mirror screen does (no picture, the phone's speaker quiet; scrcpy buffer {AudioBufferMs} ms)");
            return null;
        }
    }

    public void Stop(string why)
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            if (process is null) return;
            _stopping = true;
            _process = null;
        }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        log.Log(LogLevel.Info, $"Play everywhere: stopped taking the phone's sound ({why}); it plays on the phone itself again");
    }

    private void OnExited(Process process, Func<string> lastLine, Func<bool> sawAndroid13)
    {
        string? error = null;
        // Let the output readers finish, so scrcpy's last words (the Android 13 note) are in.
        try { process.WaitForExit(); } catch { }
        lock (_lock)
        {
            if (ReferenceEquals(process, _process))
            {
                _process = null;
                if (!_stopping)
                {
                    error = sawAndroid13() ? "needs-android-13" : "mirror-stopped";
                    var line = lastLine();
                    log.Log(LogLevel.Info, $"Play everywhere: the phone's sound stopped coming ({(line.Length > 0 ? line : "scrcpy exited")})");
                }
            }
        }
        try { process.Dispose(); } catch { }
        if (error is not null) Failed?.Invoke(error);
    }
}
