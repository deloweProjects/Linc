using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Linc.Desktop.Services;

/// <summary>
/// The virtual audio cable Linc sets up so the phone can be this PC's microphone (D-071).
///
/// <para><b>Why VB-CABLE, fetched rather than bundled.</b> Windows only lets a kernel driver
/// create a microphone, and on a Secure-Boot Windows 11 that driver has to be Microsoft-signed.
/// No open-source virtual cable ships such a build (VirtualDrivers/Virtual-Audio-Driver is
/// test-signed and has no loopback — its mic is the SysVAD sample tone; AudioMirror and the GPL
/// ones are unfinished and unsigned), so Linc does what nearly every "phone as a mic" app does:
/// it uses VB-Audio's free, signed VB-CABLE (driver catalog signed by Microsoft's Windows Hardware
/// Compatibility Publisher). Its licence allows passing the package on unmodified but not
/// integrating it into another installer without the author's agreement, so Linc neither bundles
/// it nor installs it silently: after the user has agreed, it downloads the maker's own package
/// from vb-audio.com — pinned to one version and checked against its SHA-256 — and opens VB-Audio's
/// own setup (one Windows elevation prompt), where the user clicks "Install Driver".</para>
/// </summary>
public static class VirtualCablePackage
{
    public const string DisplayName = "VB-CABLE Virtual Audio Device (VB-Audio, donationware)";
    public const string Version = "4.5 (Driver Pack 45)";
    public const string Url = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    /// <summary>
    /// SHA-256 of <see cref="Url"/>. VB-Audio replaces packs in place now and then; a mismatch
    /// stops the install with a plain message rather than running something unverified.
    /// </summary>
    public const string Sha256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";

    /// <summary>The pinned pack's exact size, so it can be fetched in ranges without asking first.</summary>
    public const long Bytes = 1_318_877;

    public const string FileName = "VBCABLE_Driver_Pack45.zip";

    /// <summary>
    /// The byte ranges the pack is fetched in. vb-audio.com serves one connection very slowly
    /// (a single GET timed out after five minutes, twice, on 2026-10-10), so the pack comes down
    /// as small ranges over several connections, and each finished range is kept on disk.
    /// </summary>
    public static IReadOnlyList<(long Start, long End)> Chunks(long total, int chunk = 64 * 1024)
    {
        var list = new List<(long, long)>();
        for (long s = 0; s < total; s += chunk) list.Add((s, Math.Min(s + chunk, total) - 1));
        return list;
    }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc", "drivers", "vb-cable-45");

    public static bool HashMatches(ReadOnlySpan<byte> data, string expectedHex) =>
        expectedHex.Length == 64 &&
        Convert.ToHexString(SHA256.HashData(data)).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pack's setup for this machine. Pack 45 has two: x64 (which also carries the ARM64
    /// driver and runs emulated on ARM64 Windows) and 32-bit.
    /// </summary>
    public static string? SetupFor(IEnumerable<string> files, Architecture os)
    {
        string wanted = os is Architecture.X64 or Architecture.Arm64 ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }
}

public enum CableSetupOutcome { Installed, AlreadyInstalled, Declined, Failed }

public sealed record CableSetupResult(CableSetupOutcome Outcome, string? Message = null)
{
    public bool Ok => Outcome is CableSetupOutcome.Installed or CableSetupOutcome.AlreadyInstalled;
}

/// <summary>
/// Downloads, verifies, installs and removes the cable (see <see cref="VirtualCablePackage"/>).
/// <paramref name="url"/> and <paramref name="folder"/> exist for the test harness, which serves
/// the pack from a deliberately slow and flaky local server.
/// </summary>
public sealed class VirtualCableSetup(ILogService log, Func<bool> cablePresent, string? url = null, string? folder = null)
{
    private readonly string _url = url ?? VirtualCablePackage.Url;
    private readonly string _folder = folder ?? VirtualCablePackage.Folder;

    private static readonly SemaphoreSlim Busy = new(1, 1);
    private const int ERROR_CANCELLED = 1223; // the user said No to the elevation prompt

    public bool IsBusy => Busy.CurrentCount == 0;

    /// <summary>Set up the cable. <paramref name="progress"/> gets the download's percentage.</summary>
    public async Task<CableSetupResult> InstallAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (cablePresent()) return new(CableSetupOutcome.AlreadyInstalled);
        if (!await Busy.WaitAsync(0, ct)) return new(CableSetupOutcome.Failed, "The microphone is already being set up.");
        try
        {
            var setup = await PrepareAsync(progress, ct);
            log.Log(LogLevel.Info, $"Voice Access: installing {VirtualCablePackage.DisplayName} {VirtualCablePackage.Version}");
            var run = await RunElevatedAsync(setup, ct);
            if (run is not null) return run;

            // The endpoints appear a moment after the driver loads.
            for (int i = 0; i < 40 && !cablePresent(); i++) await Task.Delay(500, ct);
            if (!cablePresent())
            {
                return Fail("The virtual microphone isn't showing yet. If VB-Audio's setup said it installed, restart the PC, then try again.");
            }
            log.Log(LogLevel.Info, "Voice Access: the virtual microphone is ready");
            return new(CableSetupOutcome.Installed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail($"Couldn't set up the microphone: {ex.Message}"); }
        finally { Busy.Release(); }
    }

    public async Task<CableSetupResult> UninstallAsync(CancellationToken ct = default)
    {
        if (!await Busy.WaitAsync(0, ct)) return new(CableSetupOutcome.Failed, "The microphone is being set up right now.");
        try
        {
            var setup = await PrepareAsync(null, ct);
            log.Log(LogLevel.Info, "Voice Access: removing the virtual microphone");
            var run = await RunElevatedAsync(setup, ct);
            if (run is not null) return run;
            return new(CableSetupOutcome.Installed, cablePresent()
                ? "Still installed — use \"Remove Driver\" in VB-Audio's setup, then restart the PC."
                : "Removed. Restart the PC to finish.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail($"Couldn't remove the microphone: {ex.Message}"); }
        finally { Busy.Release(); }
    }

    /// <summary>
    /// The verified pack, extracted. Taken from Linc's own copy, else from the user's Downloads
    /// folder (a browser download of the same file is accepted once its SHA-256 matches), else
    /// fetched from vb-audio.com.
    /// </summary>
    private async Task<string> PrepareAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var folder = _folder;
        var zipPath = await FetchPackAsync(progress, ct);

        var extracted = Path.Combine(folder, "pack");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extracted);
        return VirtualCablePackage.SetupFor(Directory.EnumerateFiles(extracted, "*.exe", SearchOption.AllDirectories),
                   RuntimeInformation.OSArchitecture)
               ?? throw new LincException("the VB-CABLE package has no setup for this version of Windows.");
    }

    /// <summary>The verified pack on disk — Linc's copy, the user's Downloads, or vb-audio.com.</summary>
    internal async Task<string> FetchPackAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var zipPath = Path.Combine(_folder, VirtualCablePackage.FileName);
        Directory.CreateDirectory(_folder);
        if (!await IsVerifiedAsync(zipPath, ct))
        {
            var fromDownloads = url is null ? FindInDownloads() : null;
            if (fromDownloads is not null)
            {
                File.Copy(fromDownloads, zipPath, overwrite: true);
                log.Log(LogLevel.Info, $"Voice Access: using the VB-CABLE pack from {fromDownloads}");
            }
            else
            {
                await DownloadAsync(zipPath, progress, ct);
            }
        }
        progress?.Report(100);
        return zipPath;
    }

    private static async Task<bool> IsVerifiedAsync(string path, CancellationToken ct) =>
        File.Exists(path) && new FileInfo(path).Length == VirtualCablePackage.Bytes &&
        VirtualCablePackage.HashMatches(await File.ReadAllBytesAsync(path, ct), VirtualCablePackage.Sha256);

    /// <summary>A copy of the exact pack the user downloaded themselves, if there is one.</summary>
    private static string? FindInDownloads()
    {
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads)) return null;
            foreach (var file in Directory.EnumerateFiles(downloads, "VBCABLE_Driver_Pack45*.zip"))
            {
                if (new FileInfo(file).Length == VirtualCablePackage.Bytes &&
                    VirtualCablePackage.HashMatches(File.ReadAllBytes(file), VirtualCablePackage.Sha256))
                    return file;
            }
        }
        catch { /* not readable: just download it */ }
        return null;
    }

    /// <summary>
    /// Fetch the pack in ranges over several connections, keeping each finished range on disk so
    /// a later attempt carries on where this one stopped. Gives up only when nothing at all has
    /// arrived for <see cref="StallLimit"/>, not after a fixed time — the server is slow, not dead.
    /// </summary>
    private async Task DownloadAsync(string zipPath, IProgress<int>? progress, CancellationToken ct)
    {
        var parts = Path.Combine(_folder, "parts");
        Directory.CreateDirectory(parts);
        var chunks = VirtualCablePackage.Chunks(VirtualCablePackage.Bytes);
        string PartPath(int i) => Path.Combine(parts, $"part_{i:D3}");
        bool Done(int i) => File.Exists(PartPath(i)) && new FileInfo(PartPath(i)).Length == chunks[i].End - chunks[i].Start + 1;

        long have = Enumerable.Range(0, chunks.Count).Where(Done).Sum(i => chunks[i].End - chunks[i].Start + 1);
        log.Log(LogLevel.Info, $"Voice Access: downloading the virtual microphone from {new Uri(_url).Host}" +
            (have > 0 ? $" (resuming at {have * 100 / VirtualCablePackage.Bytes} %)" : ""));
        progress?.Report((int)(have * 100 / VirtualCablePackage.Bytes));

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        long lastProgressTicks = DateTime.UtcNow.Ticks;
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastProgressTicks)) > StallLimit) stalled.Cancel();
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using var lanes = new SemaphoreSlim(8);

        var tasks = Enumerable.Range(0, chunks.Count).Where(i => !Done(i)).Select(async i =>
        {
            await lanes.WaitAsync(stalled.Token);
            try
            {
                var (start, end) = chunks[i];
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(stalled.Token);
                        attemptTimeout.CancelAfter(TimeSpan.FromSeconds(90));
                        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                        using var response = await http.SendAsync(request, attemptTimeout.Token);
                        response.EnsureSuccessStatusCode();
                        var bytes = await response.Content.ReadAsByteArrayAsync(attemptTimeout.Token);
                        if (bytes.LongLength != end - start + 1) throw new IOException("short range");
                        await File.WriteAllBytesAsync(PartPath(i), bytes, stalled.Token);
                        Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                        long now = Interlocked.Add(ref have, bytes.LongLength);
                        progress?.Report((int)(now * 100 / VirtualCablePackage.Bytes));
                        return;
                    }
                    catch (Exception) when (!stalled.IsCancellationRequested && attempt < 10)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 10)), stalled.Token);
                    }
                }
            }
            finally { lanes.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        // Stopped on purpose: whatever the in-flight ranges failed with, it is a cancellation (the
        // finished ranges stay on disk for next time).
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception)
        {
            long got = Interlocked.Read(ref have) * 100 / VirtualCablePackage.Bytes;
            throw new LincException($"vb-audio.com is very slow right now ({got} % downloaded). Linc kept what it got; try again and it carries on. " +
                $"Or download {VirtualCablePackage.FileName} from vb-audio.com/Cable in a browser — Linc picks it up from Downloads.");
        }

        await using (var output = File.Create(zipPath))
        {
            for (int i = 0; i < chunks.Count; i++)
                await using (var part = File.OpenRead(PartPath(i))) await part.CopyToAsync(output, ct);
        }
        if (!await IsVerifiedAsync(zipPath, ct))
        {
            File.Delete(zipPath);
            Directory.Delete(parts, recursive: true);
            throw new LincException("the download didn't match the version Linc was tested with, so it wasn't run. Try again, or update Linc.");
        }
        Directory.Delete(parts, recursive: true);
        log.Log(LogLevel.Info, "Voice Access: VB-CABLE pack downloaded and verified");
    }

    /// <summary>How long with no new data before the download is given up (and kept for later).</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Opens VB-Audio's setup elevated — its own window, unmodified, where the user clicks
    /// Install Driver or Remove Driver — and waits for it to close. Null on success.
    /// </summary>
    private async Task<CableSetupResult?> RunElevatedAsync(string setup, CancellationToken ct)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup)!,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            log.Log(LogLevel.Info, "Voice Access: the Windows permission prompt was declined");
            return new(CableSetupOutcome.Declined, "Windows' permission prompt was declined on the PC, so the microphone wasn't set up.");
        }
        if (process is null) return Fail("Windows didn't start the VB-CABLE setup.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail("VB-Audio's setup is still open on the PC. Finish it there, then try again.");
            }
            log.Log(LogLevel.Info, $"Voice Access: VB-CABLE setup exited with {process.ExitCode}");
        }
        return null;
    }

    private CableSetupResult Fail(string message)
    {
        log.Log(LogLevel.Warn, $"Voice Access: {message}");
        return new(CableSetupOutcome.Failed, message);
    }
}
