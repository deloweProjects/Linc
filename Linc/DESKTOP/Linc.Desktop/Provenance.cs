namespace Linc.Desktop;

/// <summary>
/// Who made Linc, stated openly.
///
/// This is plain authorship information, the same idea as a copyright line in a book. It is shown
/// in the log (the first line of every run, and the header of Settings › Logs › Copy log), sent to
/// the phone as an optional <c>author</c> field in the handshake, and matches the Company /
/// Authors / Copyright fields in the exe's file properties. That is all it does: nothing here is
/// hidden, encrypted, checked, enforced or sent anywhere else, and the app behaves exactly the
/// same if these values are changed. The phone app has the same constants in
/// <c>app/linc/android/Provenance.kt</c>; keep the two identical. See <c>NOTICE</c> at the
/// repository root.
/// </summary>
public static class Provenance
{
    public const string Product = "Linc";
    public const string Author = "deloweProjects";
    public const string Source = "https://github.com/deloweProjects/Linc";
    public const string Copyright = "© 2026 deloweProjects";

    /// <summary>The sentence the fingerprint is computed from (UTF-8, no trailing newline).</summary>
    public const string Statement = "Linc by deloweProjects — https://github.com/deloweProjects/Linc";

    /// <summary>
    /// A stable id for this project: the SHA-256 of <see cref="Statement"/>. Anyone can reproduce it:
    /// <code>
    /// printf '%s' 'Linc by deloweProjects — https://github.com/deloweProjects/Linc' | sha256sum
    /// </code>
    /// or in PowerShell 7:
    /// <code>
    /// [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Linc by deloweProjects — https://github.com/deloweProjects/Linc'))).ToLower()
    /// </code>
    /// </summary>
    public const string Fingerprint = "93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281";

    /// <summary>"Linc 1.0.0.17 by deloweProjects — https://github.com/deloweProjects/Linc".</summary>
    public static string Banner(string version) => $"{Product} {version} by {Author} — {Source}";

    /// <summary>This build's four-part version (the one release.ps1 stamps into the csproj).</summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
}
