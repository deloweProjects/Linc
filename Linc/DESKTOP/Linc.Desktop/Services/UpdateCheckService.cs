using System.Security.Cryptography;

namespace Linc.Desktop.Services;

/// <summary>
/// M17b B2/B3: fetches the manifest and turns it into an <see cref="UpdateAction"/>.
///
/// Deliberately has NO WinUI types and takes its inputs as delegates, so <c>tools\updatesim</c>
/// links this exact file and proves the gate against the real code rather than a model of it
/// (GUIDE 4.1, the D-036 posture). The view model supplies the delegates in production.
///
/// <para>THE INERT RULE (M19 C3, acceptance item 9b): with <b>"Keep Linc up to date" turned off</b>
/// the feature does nothing at all — no request, no UI, no log line. <see cref="Requests"/> exists
/// so a harness can prove "zero network calls" as a number rather than by reading the code. M17b
/// keyed this off an empty manifest URL; M19 keys it off the switch, because the URL is now a
/// build-time constant (<see cref="UpdateChannel.ManifestUrl"/>) rather than a setting.</para>
/// </summary>
public sealed class UpdateCheckService(
    Func<bool> updatesEnabled,
    Func<string> installedVersion,
    Func<string?> skippedVersion,
    HttpClient http,
    Action<string>? debugLog = null)
{
    /// <summary>B3: at most one check per this interval.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private bool _inFlight;

    /// <summary>How many HTTP requests this service has actually issued. Starts at 0 and MUST stay 0 while checking is off.</summary>
    public int Requests { get; private set; }

    /// <summary>How many log lines this service has emitted. MUST stay 0 while checking is off (M19 C3: "nothing in the logs").</summary>
    public int LogLines { get; private set; }

    /// <summary>The manifest from the last successful fetch, or null.</summary>
    public UpdateManifest? Latest { get; private set; }

    /// <summary>
    /// M19 C3: raised after any check that produced something to show, so a "Check now" pressed on
    /// the Settings page still surfaces through the shell's card — the shell owns the UI state, and
    /// the Settings page must not grow its own copy of it.
    /// </summary>
    public event Action<UpdateAction>? Checked;

    /// <summary>
    /// Runs a check if the gate allows one. Never throws: a dead backend must never reach the user
    /// (B3). Returns the action to take — <see cref="UpdateAction.None"/> whenever it did not run.
    /// </summary>
    public async Task<UpdateAction> CheckAsync(CancellationToken token = default)
    {
        // ---- the gate. Nothing above this line may touch the network or the log. ----
        // M19 C3: the switch is the whole on/off. Off means zero HTTP requests, ever - that is
        // what tools\updatesim counts, and breaking this line is how its negative proof fails.
        if (!updatesEnabled())
        {
            return UpdateAction.None; // Checking is off: no request, no UI, no log line.
        }

        var url = UpdateChannel.ManifestUrl;

        if (DateTime.UtcNow - _lastCheckUtc < MinimumInterval || _inFlight)
        {
            return UpdateAction.None; // B3: at most one check per 6 h, one in flight at a time.
        }

        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
        {
            return UpdateAction.None;
        }

        try
        {
            _inFlight = true;
            Requests++;
            var json = await http.GetStringAsync(url, token).ConfigureAwait(false);
            _lastCheckUtc = DateTime.UtcNow;

            var manifest = UpdateManifest.TryParse(json);
            if (manifest is null)
            {
                Log("update: manifest did not parse; ignoring.");
                return UpdateAction.None;
            }

            Latest = manifest;
            var decided = UpdateDecision.Decide(
                installedVersion(), manifest.Latest, manifest.MinimumSupported, skippedVersion());
            if (decided != UpdateAction.None)
            {
                Checked?.Invoke(decided);
            }
            return decided;
        }
        catch (Exception ex)
        {
            // B3: silent except a debug line. Never surfaced, never rethrown.
            _lastCheckUtc = DateTime.UtcNow; // a dead backend must not be retried in a tight loop
            Log($"update: check failed ({ex.GetType().Name}); ignoring.");
            return UpdateAction.None;
        }
        finally
        {
            _inFlight = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// M19 C3: what the "Check now" button calls. Clears the 6-hour gate so a deliberate press
    /// always does something, then runs the ordinary check — which still refuses outright when
    /// "Keep Linc up to date" is off, so this button cannot be used to sneak past the switch.
    /// </summary>
    public async Task<UpdateAction> CheckNowAsync(CancellationToken token = default)
    {
        if (!updatesEnabled())
        {
            return UpdateAction.None; // The switch wins. Still no request, still no log line.
        }

        _lastCheckUtc = DateTime.MinValue;
        return await CheckAsync(token).ConfigureAwait(false);
    }

    private void Log(string message)
    {
        LogLines++;
        debugLog?.Invoke(message);
    }

    /// <summary>
    /// M17b C: the desktop apply path. Downloads to a temp file and verifies the manifest's SHA256
    /// BEFORE handing the file to the OS. A mismatch returns false and a plain-language reason —
    /// an unverified binary is never launched.
    /// <para>This method downloads and verifies only. It does NOT start the installer; see
    /// <see cref="LaunchInstaller"/>, which the caller invokes separately after this returns true.</para>
    /// </summary>
    public async Task<(bool Ok, string? Path, string Message)> DownloadAndVerifyAsync(
        UpdateManifest manifest, string destinationDirectory, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.Url))
        {
            return (false, null, "This update has no download link yet. Try again later.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            // No checksum means we cannot prove what we downloaded. Refuse rather than trust it.
            return (false, null, "Linc can't verify this update, so it wasn't installed.");
        }

        var path = Path.Combine(destinationDirectory, $"Linc-{manifest.Latest ?? "update"}.tmp");
        try
        {
            await using (var source = await http.GetStreamAsync(manifest.Url, token).ConfigureAwait(false))
            await using (var file = File.Create(path))
            {
                await source.CopyToAsync(file, token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return (false, null, "Linc couldn't download this update. Check your connection and try again.");
        }

        var actual = Sha256OfFile(path);
        if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            return (false, null, "This update didn't match its security check, so Linc didn't install it.");
        }

        return (true, path, "Update verified.");
    }

    /// <summary>Hex SHA256 of a file. Public so the harness can compute a fixture's real hash.</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort — a leftover temp file is not worth surfacing.
        }
    }
}
