using System.IO;

namespace Linc.Desktop.Services;

/// <summary>
/// "Phone as PC speaker" (v25, D-093): the PC's sound plays on the phone <i>instead of</i> on
/// the PC.
///
/// <para><b>Why not just stream it.</b> Plain pc-audio (v20) captures what the PC is already
/// playing, so the PC's own speakers carry on too, and the phone, ~150 ms behind, sounds like an
/// echo of them. The two can't be lined up from here (the PC's output can't be delayed), so
/// speaker mode removes one of them instead: it makes the virtual cable's playback side ("CABLE
/// Input", the same VB-CABLE Voice Access installs, D-071) the default output. Apps then play
/// into the cable, the PC's speakers go quiet, and pc-audio's loopback, which always follows the
/// default output, captures the cable and sends it to the phone.</para>
///
/// <para>The user's own output is written to a note first and put back on stop, when the phone
/// goes away, and at the next start after a crash (<see cref="RestorePending"/>), the same way
/// Voice Access protects the default microphone.</para>
/// </summary>
public sealed class PcSpeakerMode(ILogService log)
{
    private readonly object _lock = new();
    private string? _restoreRenderId;

    /// <summary>Under the store root, set at startup (D-057), like Voice Access's note.</summary>
    public static string? StoreRoot { get; set; }

    public bool On
    {
        get { lock (_lock) return _restoreRenderId is not null; }
    }

    /// <summary>The user's own default output, put aside while the cable is the default (Play everywhere plays to it).</summary>
    public string? OwnOutputId
    {
        get { lock (_lock) return _restoreRenderId; }
    }

    /// <summary>
    /// Route the PC's sound into the cable. Null on success, else <c>"no-cable"</c> (nothing to
    /// route into) or <c>"denied"</c> (Windows refused the switch).
    /// </summary>
    public string? Enable()
    {
        lock (_lock)
        {
            if (_restoreRenderId is not null) return null;
            var cable = PcVoiceAccess.CableRenderId();
            if (cable is null) return "no-cable";
            var current = PcVoiceAccess.DefaultRenderId() ?? cable;
            if (current != cable)
            {
                WriteNote(current);
                if (!PcVoiceAccess.SetDefaultEndpoint(cable))
                {
                    DeleteNote();
                    return "denied";
                }
            }
            _restoreRenderId = current;
            log.Log(LogLevel.Info, "Speaker mode: this PC's sound now plays on the phone");
            return null;
        }
    }

    /// <summary>Put the user's own output back. Safe to call when not on.</summary>
    public void Disable()
    {
        string? restore;
        lock (_lock)
        {
            restore = _restoreRenderId;
            _restoreRenderId = null;
        }
        if (restore is null) return;
        if (!PcVoiceAccess.SetDefaultEndpoint(restore))
            log.Log(LogLevel.Warn, "Speaker mode: couldn't put this PC's own speakers back; choose them in Sound settings");
        DeleteNote();
        log.Log(LogLevel.Info, "Speaker mode: this PC's sound plays on the PC again");
    }

    /// <summary>A speaker-mode session cut short by a crash or a kill: put the user's output back.</summary>
    public static void RestorePending(ILogService log)
    {
        string? id;
        try { id = NotePath is { } path && File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return; }
        if (string.IsNullOrEmpty(id)) return;
        if (PcVoiceAccess.SetDefaultEndpoint(id))
            log.Log(LogLevel.Info, "Speaker mode: put this PC's own speakers back as the default");
        DeleteNote();
    }

    private static string? NotePath =>
        StoreRoot is { } root ? Path.Combine(root, "speaker-restore-output.txt") : null;

    private static void WriteNote(string id)
    {
        try
        {
            if (NotePath is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch { /* best effort: without the note only a crash mid-session loses the restore */ }
    }

    private static void DeleteNote()
    {
        try { if (NotePath is { } path) File.Delete(path); } catch { }
    }
}
