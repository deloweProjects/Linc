using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Linc.Desktop.Services;

/// <summary>
/// Puts a downloaded, hash-checked test build in place (the PC half of the test feed): unzipped to
/// <c>&lt;store root&gt;\test-builds\&lt;build&gt;</c>, started, and this copy exits. The new copy's
/// startup takes over the tray and rewrites the sign-in entry to its own path, as it already does
/// for a copy run from a new folder. Settings live in the store root, so nothing is lost. The two
/// newest builds' folders are kept and older ones removed.
/// </summary>
public static class TestInstall
{
    public static string FolderFor(string storeRoot, int build) => Path.Combine(storeRoot, "test-builds", build.ToString());

    /// <summary>Unzips <paramref name="verifiedZip"/> and returns the new Linc.Desktop.exe, or null
    /// with a plain reason.</summary>
    public static (string? Exe, string? Problem) Unpack(string verifiedZip, string storeRoot, int build)
    {
        var target = FolderFor(storeRoot, build);
        var staging = target + ".part";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(verifiedZip, staging);
            // The zip may hold the files at its top or inside one folder.
            var exe = Directory.GetFiles(staging, "Linc.Desktop.exe", SearchOption.AllDirectories)
                .OrderBy(p => p.Length).FirstOrDefault();
            if (exe is null) return (null, "The test build didn't contain Linc. Try again later.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return (Path.Combine(target, Path.GetRelativePath(staging, exe)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            return (null, "Linc couldn't unpack the test build: " + ex.Message);
        }
        finally
        {
            try { File.Delete(verifiedZip); } catch { }
        }
    }

    public static void Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });

    /// <summary>Removes test-build folders older than the newest two (never the running one).</summary>
    public static void Prune(string storeRoot)
    {
        try
        {
            var root = Path.Combine(storeRoot, "test-builds");
            if (!Directory.Exists(root)) return;
            var running = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var old = Directory.GetDirectories(root)
                .Select(d => (Dir: d, Build: int.TryParse(Path.GetFileName(d), out var n) ? n : -1))
                .Where(x => x.Build > 0)
                .OrderByDescending(x => x.Build)
                .Skip(2);
            foreach (var (dir, _) in old)
            {
                if (running.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch (Exception) { }
    }
}
