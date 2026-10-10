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

/// <summary>Downloads, verifies, installs and removes the cable (see <see cref="VirtualCablePackage"/>).</summary>
public sealed class VirtualCableSetup(ILogService log, Func<bool> cablePresent)
{
    private static readonly SemaphoreSlim Busy = new(1, 1);
    private const int ERROR_CANCELLED = 1223; // the user said No to the elevation prompt

    public bool IsBusy => Busy.CurrentCount == 0;

    public async Task<CableSetupResult> InstallAsync(CancellationToken ct = default)
    {
        if (cablePresent()) return new(CableSetupOutcome.AlreadyInstalled);
        if (!await Busy.WaitAsync(0, ct)) return new(CableSetupOutcome.Failed, "The microphone is already being set up.");
        try
        {
            var setup = await PrepareAsync(ct);
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
            var setup = await PrepareAsync(ct);
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

    /// <summary>The verified pack, extracted; downloaded only if it isn't here already.</summary>
    private async Task<string> PrepareAsync(CancellationToken ct)
    {
        var folder = VirtualCablePackage.Folder;
        var zipPath = Path.Combine(folder, "VBCABLE_Driver_Pack45.zip");
        Directory.CreateDirectory(folder);

        byte[]? zip = File.Exists(zipPath) ? await File.ReadAllBytesAsync(zipPath, ct) : null;
        if (zip is null || !VirtualCablePackage.HashMatches(zip, VirtualCablePackage.Sha256))
        {
            log.Log(LogLevel.Info, $"Voice Access: downloading the virtual microphone from {new Uri(VirtualCablePackage.Url).Host}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            zip = await http.GetByteArrayAsync(VirtualCablePackage.Url, ct);
            if (!VirtualCablePackage.HashMatches(zip, VirtualCablePackage.Sha256))
            {
                throw new LincException("the download didn't match the version Linc was tested with, so it wasn't run. Update Linc and try again.");
            }
            await File.WriteAllBytesAsync(zipPath, zip, ct);
        }

        var extracted = Path.Combine(folder, "pack");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extracted);
        return VirtualCablePackage.SetupFor(Directory.EnumerateFiles(extracted, "*.exe", SearchOption.AllDirectories),
                   RuntimeInformation.OSArchitecture)
               ?? throw new LincException("the VB-CABLE package has no setup for this version of Windows.");
    }

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
