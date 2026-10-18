using System;

namespace Linc.Desktop.Services;

/// <summary>The pure parts of the phone-as-microphone bridge (D-070), kept apart so they are testable.</summary>
public static class PcMicAudio
{
    /// <summary>Linear upsampling of 16-bit PCM to float samples at <paramref name="outRate"/>.</summary>
    public static float[] Upsample(short[] input, int inRate, int outRate)
    {
        if (input.Length == 0) return [];
        int outLength = (int)((long)input.Length * outRate / inRate);
        var output = new float[outLength];
        double step = (double)inRate / outRate;
        for (int i = 0; i < outLength; i++)
        {
            double pos = i * step;
            int a = (int)pos;
            int b = Math.Min(a + 1, input.Length - 1);
            double t = pos - a;
            output[i] = (float)((input[a] * (1 - t) + input[b] * t) / 32768.0);
        }
        return output;
    }

    /// <summary>The playback side of a virtual cable: VB-CABLE's "CABLE Input", or any "Virtual … Input".</summary>
    public static bool IsCableInput(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && name.Contains("Input", StringComparison.OrdinalIgnoreCase));

    /// <summary>The recording side: "CABLE Output", or any "Virtual … Output".</summary>
    public static bool IsCableOutput(string name) =>
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && name.Contains("Output", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The device-interface path Voice Access stores as its microphone (HKCU\…\VoiceAccess,
    /// VoiceAccessMicrophoneId) for a capture endpoint id like <c>{0.0.1.00000000}.{guid}</c>. Voice
    /// Access keeps this choice instead of following the default microphone (D-075).
    /// </summary>
    public static string VoiceAccessMicId(string endpointId) =>
        $@"\\?\SWD#MMDEVAPI#{endpointId}#{{2eef81be-33fa-4800-9670-1cd474972c3f}}";

    /// <summary>
    /// Is any app other than Linc using a microphone right now? Windows records every capture in
    /// the privacy consent store — the same data behind the taskbar's microphone icon — with a
    /// start time and, once it stops, a stop time.
    /// </summary>
    private static bool IsRunning(string exePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(exePath);
        var running = System.Diagnostics.Process.GetProcessesByName(name);
        try { return running.Length > 0; }
        finally { foreach (var p in running) p.Dispose(); }
    }

    public static bool MicInUseByOtherApps()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone");
            if (root is null) return false;
            bool InUse(Microsoft.Win32.RegistryKey key, string name) =>
                !name.Contains("Linc", StringComparison.OrdinalIgnoreCase) &&
                key.GetValue("LastUsedTimeStart") is long start && start > 0 &&
                key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
            foreach (var name in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(name);
                if (app is null) continue;
                if (name == "NonPackaged")
                {
                    foreach (var exe in app.GetSubKeyNames())
                    {
                        using var e = app.OpenSubKey(exe);
                        // A crashed app never writes its stop time, so for desktop apps (whose
                        // record is their exe path, '\' written as '#') the exe must be running too.
                        if (e is not null && InUse(e, exe) && IsRunning(exe.Replace('#', '\\'))) return true;
                    }
                }
                else if (InUse(app, name)) return true;
            }
        }
        catch { /* unreadable: treat as quiet */ }
        return false;
    }
}
