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
}
