using System.IO;

namespace Linc.Desktop.Services;

/// <summary>
/// Where the bundled companion APK lives — resolved with no WinUI or ADB dependency, so the exact
/// logic the real desktop runs can be exercised by a plain console harness (tools/apkprobe).
///
/// The desktop <b>carries</b> the companion (M2b): Linc.Desktop.csproj copies the Android build
/// output to <c>Assets/companion.apk</c> beside the executable, so a machine that has never seen
/// this repo can still install it. We look there first, then fall back to this repo's Android
/// build output for developer runs (same posture as scrcpy in D-023 — the binary is a build
/// input, not a source blob committed to git).
/// </summary>
public static class ApkResolver
{
    /// <summary>
    /// Resolves the companion APK from <paramref name="baseDirectory"/> (the executable's folder).
    /// <paramref name="fileExists"/> is injected so the branch logic can be driven without touching
    /// a real filesystem. Returns the first candidate that exists, or null when neither is present
    /// (a desktop-only build, where onboarding degrades to "no copy was found to install").
    /// </summary>
    public static string? Resolve(string baseDirectory, Func<string, bool> fileExists)
    {
        // Shipped location: next to the exe (this is what the csproj copy step produces).
        var beside = Path.Combine(baseDirectory, "Assets", "companion.apk");
        if (fileExists(beside))
        {
            return beside;
        }
        // Dev fallback: bin/x64/Debug/<tfm>/ is four levels below the project, six below the repo
        // root, from which the Android debug APK sits at its usual Gradle output path.
        var dev = Path.GetFullPath(Path.Combine(
            baseDirectory, "..", "..", "..", "..", "..", "..",
            "ANDROID", "app", "build", "outputs", "apk", "debug", "app-debug.apk"));
        return fileExists(dev) ? dev : null;
    }
}
